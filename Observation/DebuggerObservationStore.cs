using System.Collections.ObjectModel;

namespace VisualStudioMcpServer.Observation;

internal enum DebuggerObservedState
{
    Unknown,
    Running,
    Break,
    Design
}

internal enum DebuggerWaitDesiredState
{
    Running,
    Break,
    Design,
    AnyChange
}

internal sealed record DebuggerObservation(
    string ProviderEpoch,
    long Sequence,
    string? DebugSessionId,
    string? LaunchOperationId,
    DebuggerObservedState State,
    DateTimeOffset ObservedAtUtc,
    ulong ProviderSequence = 0);

internal enum DebuggerObservationWaitStatus
{
    Matched,
    EventHistoryExpired,
    SessionChanged,
    InvalidCursor,
    WaiterLimit,
    TimedOut,
    Cancelled,
    ProviderDisconnected
}

internal sealed record DebuggerObservationWaitResult(
    DebuggerObservationWaitStatus Status,
    DebuggerObservation? Observation = null,
    long? OldestAvailableSequence = null);

/// <summary>
/// Stores provider-neutral debugger mode observations and satisfies bounded, read-only waits.
/// Session IDs and launch operation IDs must come from an authoritative provider; mode changes
/// alone must not be used to manufacture either identity.
/// </summary>
internal sealed class DebuggerObservationStore
{
    internal const int MaximumHistoryEntries = 256;
    internal const int MaximumWaiters = 8;
    internal static readonly TimeSpan HistoryLifetime = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly LinkedList<DebuggerObservation> _history = new();
    private readonly Dictionary<Guid, Waiter> _waiters = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _providerEpoch;
    private readonly List<long> _gaps = new();
    private long _sequence;
    private ulong _lastProviderSequence;
    private bool _closed;

    internal DebuggerObservationStore(
        string providerEpoch,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (!ProviderEpochIsValid(providerEpoch))
            throw new ArgumentException("Provider epoch must be a lowercase UUID-N v4 value.", nameof(providerEpoch));

        _providerEpoch = providerEpoch;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    internal string ProviderEpoch => _providerEpoch;

    internal long CurrentSequence
    {
        get { lock (_sync) return _sequence; }
    }

    /// <summary>最後に反映した provider の mode sequence。0 は未受信。</summary>
    internal ulong LastProviderSequence
    {
        get { lock (_sync) return _lastProviderSequence; }
    }

    internal bool IsClosed
    {
        get { lock (_sync) return _closed; }
    }

    /// <summary>
    /// provider の event 欠落を履歴の gap として記録する。公開 sequence を 1 つ進めて gap の位置を示し、
    /// gap をまたぐ待機（既存 waiter を含む）は event_history_expired とする。
    /// </summary>
    internal long MarkHistoryGap()
    {
        Waiter[] waiters;
        long gap;
        lock (_sync)
        {
            gap = checked(++_sequence);
            _gaps.Add(gap);
            while (_gaps.Count > MaximumHistoryEntries) _gaps.RemoveAt(0);
            waiters = _waiters.Values.ToArray();
        }
        foreach (var waiter in waiters)
            waiter.Completion.TrySetResult(new(DebuggerObservationWaitStatus.EventHistoryExpired, OldestAvailableSequence: gap + 1));
        return gap;
    }

    /// <summary>provider channel の終端。待機中の waiter を provider_disconnected で終え、以後の待機も同じ結果にする。</summary>
    internal void Close()
    {
        Waiter[] waiters;
        lock (_sync)
        {
            _closed = true;
            waiters = _waiters.Values.ToArray();
        }
        foreach (var waiter in waiters)
            waiter.Completion.TrySetResult(new(DebuggerObservationWaitStatus.ProviderDisconnected));
    }

    /// <summary>
    /// provider の sequence に対応する公開 sequence を返す。providerSequence 0 は event を 1 件も受けていない状態を表す。
    /// 履歴から外れた値は false を返す。
    /// </summary>
    internal bool TryGetSequenceForProviderSequence(ulong providerSequence, out long sequence)
    {
        lock (_sync)
        {
            sequence = 0;
            if (providerSequence == 0)
            {
                if (_lastProviderSequence != 0) return false;
                sequence = _sequence;
                return true;
            }
            foreach (var observation in _history)
            {
                if (observation.ProviderSequence != providerSequence) continue;
                sequence = observation.Sequence;
                return true;
            }
            return false;
        }
    }

    /// <summary>指定した provider sequence まで反映されるのを短時間だけ待つ。読み取り専用で lease を取らない。</summary>
    internal async Task<bool> WaitForProviderSequenceAsync(ulong providerSequence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            lock (_sync)
                if (_lastProviderSequence >= providerSequence || _closed) return _lastProviderSequence >= providerSequence;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<DebuggerObservation> Snapshot()
    {
        lock (_sync)
        {
            PruneExpired(_utcNow());
            return new ReadOnlyCollection<DebuggerObservation>(_history.ToArray());
        }
    }

    internal DebuggerObservation Publish(
        string? debugSessionId,
        string? launchOperationId,
        DebuggerObservedState state,
        ulong providerSequence = 0)
    {
        if (debugSessionId is not null && !ProviderEpochIsValid(debugSessionId))
            throw new ArgumentException("Debug session ID must be a lowercase UUID-N v4 value.", nameof(debugSessionId));
        if (launchOperationId is not null && !ProviderEpochIsValid(launchOperationId))
            throw new ArgumentException("Launch operation ID must be a lowercase UUID-N v4 value.", nameof(launchOperationId));
        if (state == DebuggerObservedState.Unknown)
            throw new ArgumentOutOfRangeException(nameof(state), "Unknown observations are not transitions.");

        DebuggerObservation observation;
        Waiter[] waiters;
        lock (_sync)
        {
            var now = _utcNow();
            PruneExpired(now);
            observation = new DebuggerObservation(_providerEpoch, checked(++_sequence), debugSessionId,
                launchOperationId, state, now, providerSequence);
            if (providerSequence > _lastProviderSequence) _lastProviderSequence = providerSequence;
            _history.AddLast(observation);
            while (_history.Count > MaximumHistoryEntries) _history.RemoveFirst();
            waiters = _waiters.Values.ToArray();
        }

        foreach (var waiter in waiters)
        {
            var result = EvaluateObservation(waiter.Request, observation);
            if (result is not null) waiter.Completion.TrySetResult(result);
        }
        return observation;
    }

    internal async Task<DebuggerObservationWaitResult> WaitAsync(
        DebuggerWaitDesiredState desiredState,
        long afterSequence,
        string? expectedDebugSessionId,
        string? launchOperationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(desiredState))
            throw new ArgumentOutOfRangeException(nameof(desiredState));
        if (afterSequence < 0) return new(DebuggerObservationWaitStatus.InvalidCursor);
        if ((expectedDebugSessionId is null) == (launchOperationId is null))
            throw new ArgumentException("Exactly one session ID or launch operation ID is required.");
        if (expectedDebugSessionId is not null && !ProviderEpochIsValid(expectedDebugSessionId))
            throw new ArgumentException("Debug session ID must be a lowercase UUID-N v4 value.", nameof(expectedDebugSessionId));
        if (launchOperationId is not null && !ProviderEpochIsValid(launchOperationId))
            throw new ArgumentException("Launch operation ID must be a lowercase UUID-N v4 value.", nameof(launchOperationId));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Observation waits must be between 1 ms and 30 seconds.");

        var request = new WaitRequest(desiredState, afterSequence, expectedDebugSessionId, launchOperationId);
        var waiter = new Waiter(request);
        DebuggerObservationWaitResult? immediate;
        lock (_sync)
        {
            PruneExpired(_utcNow());
            immediate = _closed ? new(DebuggerObservationWaitStatus.ProviderDisconnected) : EvaluateHistory(request);
            if (immediate is null && afterSequence > _sequence)
                immediate = new(DebuggerObservationWaitStatus.InvalidCursor);
            if (immediate is null && _waiters.Count >= MaximumWaiters)
                immediate = new(DebuggerObservationWaitStatus.WaiterLimit);
            if (immediate is null) _waiters.Add(waiter.Id, waiter);
        }
        if (immediate is not null) return immediate;

        try
        {
            var timeoutTask = _delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(waiter.Completion.Task, timeoutTask).ConfigureAwait(false);
            if (waiter.Completion.Task.IsCompleted)
                return await waiter.Completion.Task.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return new(DebuggerObservationWaitStatus.TimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DebuggerObservationWaitStatus.Cancelled);
        }
        finally
        {
            lock (_sync) _waiters.Remove(waiter.Id);
        }
    }

    private DebuggerObservationWaitResult? EvaluateHistory(WaitRequest request)
    {
        if (_history.First is { } first && request.AfterSequence < first.Value.Sequence - 1)
            return new(DebuggerObservationWaitStatus.EventHistoryExpired, OldestAvailableSequence: first.Value.Sequence);
        if (_history.Count == 0 && request.AfterSequence < _sequence)
            return new(DebuggerObservationWaitStatus.EventHistoryExpired, OldestAvailableSequence: _sequence + 1);

        // cursor より後に gap があれば、gap より前の一致だけを認め、gap 以降は欠落の可能性として失効を返す。
        var firstGap = _gaps.FirstOrDefault(gap => gap > request.AfterSequence);
        foreach (var observation in _history)
        {
            if (observation.Sequence <= request.AfterSequence) continue;
            if (firstGap != 0 && observation.Sequence > firstGap)
                return new(DebuggerObservationWaitStatus.EventHistoryExpired, OldestAvailableSequence: firstGap + 1);
            var result = EvaluateObservation(request, observation);
            if (result is not null) return result;
        }
        if (firstGap != 0)
            return new(DebuggerObservationWaitStatus.EventHistoryExpired, OldestAvailableSequence: firstGap + 1);

        if (request.DebugSessionId is not null && _history.Last is { } last &&
            last.Value.DebugSessionId is not null && last.Value.DebugSessionId != request.DebugSessionId)
            return new(DebuggerObservationWaitStatus.SessionChanged, last.Value);
        return null;
    }

    private static DebuggerObservationWaitResult? EvaluateObservation(WaitRequest request, DebuggerObservation observation)
    {
        if (observation.Sequence <= request.AfterSequence) return null;
        if (request.DebugSessionId is not null)
        {
            if (observation.DebugSessionId is null) return null;
            if (observation.DebugSessionId != request.DebugSessionId)
                return new(DebuggerObservationWaitStatus.SessionChanged, observation);
        }
        else if (observation.LaunchOperationId != request.LaunchOperationId)
        {
            return null;
        }

        return request.DesiredState == DebuggerWaitDesiredState.AnyChange ||
            request.DesiredState switch
            {
                DebuggerWaitDesiredState.Running => observation.State == DebuggerObservedState.Running,
                DebuggerWaitDesiredState.Break => observation.State == DebuggerObservedState.Break,
                DebuggerWaitDesiredState.Design => observation.State == DebuggerObservedState.Design,
                _ => false
            }
            ? new(DebuggerObservationWaitStatus.Matched, observation)
            : null;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        while (_history.First is { } first && now - first.Value.ObservedAtUtc > HistoryLifetime)
            _history.RemoveFirst();
    }

    private static bool ProviderEpochIsValid(string value)
    {
        return value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') && value[12] == '4' &&
            value[16] is '8' or '9' or 'a' or 'b' && Guid.TryParseExact(value, "N", out _);
    }

    private sealed record WaitRequest(
        DebuggerWaitDesiredState DesiredState,
        long AfterSequence,
        string? DebugSessionId,
        string? LaunchOperationId);

    private sealed class Waiter(WaitRequest request)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal WaitRequest Request { get; } = request;
        internal TaskCompletionSource<DebuggerObservationWaitResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
