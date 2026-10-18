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
    DateTimeOffset ObservedAtUtc);

internal enum DebuggerObservationWaitStatus
{
    Matched,
    EventHistoryExpired,
    SessionChanged,
    InvalidCursor,
    WaiterLimit,
    TimedOut,
    Cancelled
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
    private long _sequence;

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
        DebuggerObservedState state)
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
                launchOperationId, state, now);
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
            immediate = EvaluateHistory(request);
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

        foreach (var observation in _history)
        {
            if (observation.Sequence <= request.AfterSequence) continue;
            var result = EvaluateObservation(request, observation);
            if (result is not null) return result;
        }

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
