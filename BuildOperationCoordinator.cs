using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace VisualStudioMcpServer;

/// <summary>
/// Models and coordinates bounded Visual Studio solution build operations.
/// </summary>
internal sealed record BuildTargetSnapshot(
    int? TargetProcessId,
    long? TargetProcessStartUtcTicks,
    int? RegisteredProcessId,
    long? RegisteredProcessStartUtcTicks,
    bool DteConnected,
    string? SolutionPath,
    string? Configuration,
    string? Platform,
    bool IsDesignMode,
    bool IsBuildInProgress,
    bool HasForeignOrUncertainBuild);

internal sealed record BuildEventSnapshot(
    string? SolutionPath,
    bool IsBuildInProgress,
    bool IsCancelled,
    int? FailedProjectCount,
    bool IsOwnedOperation)
{
    public bool IsTerminalState { get; init; } = true;
    public bool HasBuildResult { get; init; } = true;
    public string? EventScope { get; init; }
    public string? EventAction { get; init; }
}

internal enum BuildAction { Build, Rebuild, Clean }

internal sealed class BuildActionUnavailableException(string message) : Exception(message);

internal sealed class BuildDispatchException(string message, bool executionStarted, Exception innerException) : Exception(message, innerException)
{
    public bool ExecutionStarted { get; } = executionStarted;
    public string FailureCode => InnerException switch
    {
        TimeoutException => "com_timeout",
        COMException => "com_error",
        BuildActionUnavailableException => "build_action_unavailable",
        _ => "operation_failed"
    };
}

internal interface IBuildOperationAdapter
{
    BuildTargetSnapshot ReadTargetSnapshot(TimeSpan timeout);
    IDisposable SubscribeBuildEvents(Action<BuildEventSnapshot> onBegin, Action<BuildEventSnapshot> onDone, TimeSpan timeout);
    void Dispatch(BuildAction action, int correlationWindowMilliseconds, TimeSpan timeout);
    Task DisposeSubscriptionAsync(IDisposable subscription);
}

internal sealed record BuildOperationResult(
    bool Success,
    string Code,
    string Message,
    int? TargetProcessId,
    string? OperationId,
    string? Action,
    string? SolutionPath,
    string? Configuration,
    string? Platform,
    string State,
    string? Outcome,
    bool? BuildSucceeded,
    int? FailedProjectCount,
    int? ErrorCount,
    bool? IsBuildInProgress,
    long ElapsedMilliseconds,
    int TimeoutMilliseconds)
{
    public string? WorkspaceRoot { get; init; }

    public static string ToBoundedJson(BuildOperationResult result)
    {
        const int maxBytes = 64 * 1024;
        var json = JsonSerializer.Serialize(result);
        if (System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;
        var bounded = result with { Message = "Result exceeded the response size limit." };
        json = JsonSerializer.Serialize(bounded);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
            throw new InvalidOperationException("Build result schema exceeds response size limit.");
        return json;
    }
}

/// <summary>
/// Serializes process-wide workspace mutations and retains immutable build snapshots.
/// </summary>
internal sealed class BuildOperationCoordinator
{
    private const int RetainedTerminalCount = 32;
    private const int MaxMessageLength = 1024;
    private readonly IBuildOperationAdapter _adapter;
    private readonly WorkspaceMutationGate _workspaceGate;
    private readonly object _sync = new();
    private readonly LinkedList<BuildOperationResult> _terminal = new();
    private ActiveOperation? _active;

    public BuildOperationCoordinator(IBuildOperationAdapter adapter, WorkspaceMutationGate? processWorkspaceGate = null)
    {
        _adapter = adapter;
        _workspaceGate = processWorkspaceGate ?? new WorkspaceMutationGate();
    }

    public async Task<BuildOperationResult> ExecuteAsync(
        BuildAction action,
        string? expectedSolutionPath,
        int? targetProcessId,
        long? targetProcessStartUtcTicks,
        bool allowBuild,
        int timeoutMilliseconds = 10000,
        string format = "json",
        CancellationToken cancellationToken = default,
        bool waitForBuild = true)
    {
        var timeout = Math.Clamp(timeoutMilliseconds, 1000, 30000);
        var started = Stopwatch.GetTimestamp();
        string? operationId = null;
        string? solutionPath = null;
        string? configuration = null;
        string? platform = null;
        if (!allowBuild) return Result(false, "build_policy_required", "Build permission is required.");
        if (string.IsNullOrWhiteSpace(expectedSolutionPath)) return Result(false, "expected_solution_required", "An expected solution path is required.");
        if (expectedSolutionPath.Length > 2048)
            return Result(false, "invalid_argument", "A build argument is outside its allowed bounds.");
        if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) && !string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
            return Result(false, "invalid_format", "Format must be json or text.");
        if (!targetProcessId.HasValue || targetProcessId <= 0 || !targetProcessStartUtcTicks.HasValue || targetProcessStartUtcTicks <= 0)
            return Result(false, "target_identity_required", "The Visual Studio process identity is required.");

        try
        {
            if (!await _workspaceGate.TryAcquireAsync(cancellationToken).ConfigureAwait(false))
                return Result(false, "build_busy", "Another workspace mutation or build is active.");
        }
        catch (OperationCanceledException) { return Result(false, "cancelled", "The caller cancelled before dispatch."); }

        IDisposable? subscription = null;
        ActiveOperation? active = null;
        try
        {
            BuildTargetSnapshot target;
            var targetTimeout = Remaining();
            if (targetTimeout <= TimeSpan.Zero) return Result(false, "com_timeout", "The build request deadline expired before target inspection.");
            try { target = _adapter.ReadTargetSnapshot(targetTimeout); }
            catch (TimeoutException ex) { return Result(false, "com_timeout", Limit(ex.Message)); }
            catch (COMException ex) { return Result(false, "com_error", $"Visual Studio rejected target inspection (HRESULT 0x{ex.HResult:X8})."); }
            catch (Exception ex) { return Result(false, "com_error", Limit(ex.Message)); }

            var guard = ValidateTarget(target, expectedSolutionPath, targetProcessId.Value, targetProcessStartUtcTicks.Value);
            if (guard is not null) return Result(false, guard, GuardMessage(guard));
            if (target.Configuration!.Length > 256 || (target.Platform?.Length ?? 0) > 256)
                return Result(false, "invalid_argument", "The active configuration or platform exceeds its limit.");
            solutionPath = target.SolutionPath;
            configuration = target.Configuration;
            platform = target.Platform;
            operationId = Guid.NewGuid().ToString("N");
            active = new ActiveOperation(operationId, action, solutionPath!, configuration!, platform,
                targetProcessId, targetProcessStartUtcTicks, timeout, started);
            lock (_sync)
            {
                if (_active is not null) return Result(false, "build_busy", "A build operation is already active.");
                _active = active;
            }

            try
            {
                var subscribeTimeout = Remaining();
                if (subscribeTimeout <= TimeSpan.Zero)
                {
                    Finish(active, "unknown", "com_timeout", "The build request deadline expired before event subscription.", null, null, false);
                    return active.Current;
                }
                subscription = _adapter.SubscribeBuildEvents(
                    begin => OnBuildBegin(active, begin),
                    done => OnBuildDone(active, done), subscribeTimeout);
                active.Subscription = subscription;
            }
            catch (Exception ex)
            {
                lock (_sync) active.SubscriptionCleanupUncertain = true;
                Finish(active, "unknown", ExceptionCode(ex), Limit(ex.Message), null, null, false);
                return active.Current;
            }

            // A begin event between subscription and dispatch belongs to an external operation.
            var dispatchTimeout = Remaining();
            if (dispatchTimeout <= TimeSpan.Zero)
            {
                Finish(active, "unknown", "com_timeout", "The build request deadline expired before dispatch.", null, null, false);
                return active.Current;
            }
            lock (_sync)
            {
                if (active.ForeignActivity)
                {
                    FinishLocked(active, "unknown", "build_busy", "A foreign or uncertain build is active.", null, null, false);
                    return active.Current;
                }
                active.Dispatching = true;
            }

            try { _adapter.Dispatch(action, timeout, dispatchTimeout); }
            catch (BuildActionUnavailableException ex)
            {
                Finish(active, "unknown", "build_action_unavailable", Limit(ex.Message), null, null, false);
                return active.Current;
            }
            catch (BuildDispatchException ex)
            {
                if (!ex.ExecutionStarted)
                {
                    Finish(active, "unknown", ex.FailureCode, Limit(ex.Message), null, null, false);
                    return active.Current;
                }

                lock (_sync)
                {
                    if (!active.Terminal)
                    {
                        active.Dispatched = true;
                        active.DispatchUncertain = true;
                        if (active.ForeignActivity)
                            active.Current = Snapshot(active, false,
                                "The dispatch call timed out after entering the STA; a foreign or uncertain build event prevents attribution.",
                                "unknown", "unknown", null, null, code: "result_unknown");
                        else if (active.DoneObserved) CompleteFromDoneLocked(active);
                        else active.Current = Snapshot(active, false,
                            "The dispatch call timed out after entering the STA; the Visual Studio action may still be running.",
                            "unknown", "unknown", null, null, code: "result_unknown");
                    }
                }
                return active.Current;
            }
            catch (TimeoutException ex)
            {
                Finish(active, "unknown", "com_timeout", Limit(ex.Message), null, null, false);
                return active.Current;
            }
            catch (COMException ex)
            {
                Finish(active, "unknown", "com_error", $"Visual Studio rejected the build action (HRESULT 0x{ex.HResult:X8}).", null, null, false);
                return active.Current;
            }
            catch (Exception ex)
            {
                Finish(active, "unknown", "operation_failed", Limit(ex.Message), null, null, false);
                return active.Current;
            }
            finally { lock (_sync) active.Dispatching = false; }

            lock (_sync)
            {
                active.Dispatched = true;
                if (active.ForeignActivity)
                {
                    active.Current = Snapshot(active, false, "A foreign or uncertain build event prevents reliable completion attribution.", "unknown", "unknown", null, null, code: "result_unknown");
                }
                else if (active.DoneObserved) CompleteFromDoneLocked(active);
                else if (active.BeginObserved) active.Current = Snapshot(active, true, "Build action accepted; operation is running.", "running", null, null, null, true);
                else active.Current = Snapshot(active, true, "Build action accepted; completion has not been observed.", "starting", null, null, null, true);
            }
            CleanupIfTerminal(active, subscription);
            StartTerminalRecheck(active);

            if (!waitForBuild && !active.Terminal) return active.Current;
            if (!active.Terminal)
            {
                var waitTimeout = Remaining();
                if (waitTimeout <= TimeSpan.Zero)
                    return RecordWaitResult(active, "build_wait_timeout", "The build did not reach a terminal event before the deadline.");
                try
                {
                    await active.Completed.Task.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return RecordWaitResult(active, "cancelled", "The caller stopped waiting; the build may still be running.");
                }
                catch (TimeoutException)
                {
                    return RecordWaitResult(active, "build_wait_timeout", "The build did not reach a terminal event before the deadline.");
                }
            }
            return active.Current;
        }
        finally
        {
            if (active is null) _workspaceGate.Release();
            else CleanupIfTerminal(active, active.Subscription);
            // Keep the event subscription and workspace gate while the outcome is pending or uncertain.
        }

        TimeSpan Remaining() => TimeSpan.FromMilliseconds(Math.Max(0, timeout - Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        BuildOperationResult Result(bool success, string code, string message) => new(success, code, Limit(message), targetProcessId, null, action.ToString().ToLowerInvariant(), null, null, null, "idle", null, null, null, null, null, Elapsed(started), timeout);
    }

    public BuildOperationResult GetStatus(string? requestedOperationId = null)
    {
        lock (_sync)
        {
            if (_active is not null && (requestedOperationId is null || _active.Id == requestedOperationId)) return _active.Current;
            if (requestedOperationId is null) return _terminal.First?.Value ?? IdleResult();
            return _terminal.FirstOrDefault(x => x.OperationId == requestedOperationId) ?? new BuildOperationResult(false, "operation_not_found", "The operation ID is not retained.", null, requestedOperationId, null, null, null, null, "idle", null, null, null, null, null, 0, 15000);
        }
    }

    public async Task<BuildOperationResult> GetStatusAsync(string? requestedOperationId = null)
    {
        ActiveOperation? uncertain;
        ActiveOperation? awaiting;
        lock (_sync)
        {
            uncertain = _active is not null && !_active.Terminal && _active.DispatchUncertain &&
                (requestedOperationId is null || _active.Id == requestedOperationId) ? _active : null;
            awaiting = uncertain is null && _active is not null && !_active.Terminal && _active.AwaitingTerminalState &&
                (requestedOperationId is null || _active.Id == requestedOperationId) ? _active : null;
            if (uncertain is null && awaiting is null) return GetStatus(requestedOperationId);
        }
        if (uncertain is null)
        {
            // 非終端の完了 event を受けた操作は、照会時にも対象が idle かを確認する。
            TryFinishIdleAwaiting(awaiting!, await ReadTargetOrNullAsync().ConfigureAwait(false));
            lock (_sync) return awaiting!.Current;
        }

        BuildTargetSnapshot target;
        try { target = await Task.Run(() => _adapter.ReadTargetSnapshot(TimeSpan.FromSeconds(5))).ConfigureAwait(false); }
        catch { return uncertain.Current; }

        lock (_sync)
        {
            if (!ReferenceEquals(_active, uncertain) || uncertain.Terminal || !uncertain.DispatchUncertain)
                return GetStatus(requestedOperationId);
            if (!IsSameTargetIdle(target, uncertain)) return uncertain.Current;
            FinishLocked(uncertain, "unknown", "result_unknown",
                "The same Visual Studio target is idle; the action result remains unknown, but no build is in progress.",
                null, null, false);
        }
        CleanupIfTerminal(uncertain, uncertain.Subscription);
        return uncertain.Current;
    }

    private void OnBuildBegin(ActiveOperation op, BuildEventSnapshot evt)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_active, op) || op.Terminal) return;
            var sameSolution = PathsEqual(evt.SolutionPath, op.SolutionPath);
            if (!evt.IsOwnedOperation || !sameSolution)
            {
                op.ForeignActivity = true;
                op.ForeignActivityReason ??= $"A foreign or uncertain build event was observed (scope={evt.EventScope ?? "unknown"}, action={evt.EventAction ?? "unknown"}).";
                op.Current = Snapshot(op, false, op.ForeignActivityReason, "unknown", "unknown", null, null, code: "build_busy");
                return;
            }
            op.BeginObserved = true;
            op.Current = Snapshot(op, true, "Build started.", "running", null, null, true, true);
        }
    }

    private void OnBuildDone(ActiveOperation op, BuildEventSnapshot evt)
    {
        var cleanup = false;
        lock (_sync)
        {
            if (!ReferenceEquals(_active, op) || op.Terminal) return;
            if (op.ForeignActivity || !evt.IsOwnedOperation || !PathsEqual(evt.SolutionPath, op.SolutionPath))
            {
                op.ForeignActivity = true;
                op.ForeignActivityReason ??= $"A foreign or uncertain build event was observed (scope={evt.EventScope ?? "unknown"}, action={evt.EventAction ?? "unknown"}).";
                if (evt.IsTerminalState && !evt.IsBuildInProgress)
                {
                    FinishLocked(op, "unknown", "result_unknown", Limit($"{op.ForeignActivityReason} Completion was observed (scope={evt.EventScope ?? "unknown"}, action={evt.EventAction ?? "unknown"}); the result cannot be attributed safely."), null, null, false);
                    cleanup = true;
                }
                else
                    op.Current = Snapshot(op, false, $"A foreign or uncertain build completion was observed (scope={evt.EventScope ?? "unknown"}, action={evt.EventAction ?? "unknown"}).", "unknown", "unknown", null, evt.IsBuildInProgress, code: "result_unknown");
            }
            else if (!evt.IsTerminalState)
            {
                op.DoneObserved = true;
                op.DoneEvent = evt;
                if (op.Dispatched) CompleteFromDoneLocked(op);
            }
            else if (!op.BeginObserved)
            {
                op.FastDone = true;
                op.DoneEvent = evt;
                op.DoneObserved = true;
                if (op.Dispatched) CompleteFromDoneLocked(op);
            }
            else
            {
                op.DoneObserved = true;
                op.DoneEvent = evt;
                if (op.Dispatched) CompleteFromDoneLocked(op);
            }
        }
        if (cleanup || op.Terminal) CleanupIfTerminal(op, op.Subscription);
        StartTerminalRecheck(op);
    }

    /// <summary>idle の再確認の間隔と回数（合計約 60 秒で打ち切り、以後は状態照会時に再確認する）。</summary>
    internal static TimeSpan TerminalRecheckDelay { get; set; } = TimeSpan.FromSeconds(1);
    private const int TerminalRecheckAttempts = 60;

    private void StartTerminalRecheck(ActiveOperation op)
    {
        lock (_sync)
        {
            if (!op.AwaitingTerminalState || op.TerminalRecheckScheduled || op.Terminal || !op.Dispatched) return;
            op.TerminalRecheckScheduled = true;
        }
        _ = RecheckTerminalAsync(op);
    }

    private async Task RecheckTerminalAsync(ActiveOperation op)
    {
        try
        {
            for (var attempt = 0; attempt < TerminalRecheckAttempts; attempt++)
            {
                await Task.Delay(TerminalRecheckDelay).ConfigureAwait(false);
                lock (_sync) if (!ReferenceEquals(_active, op) || op.Terminal) return;
                if (TryFinishIdleAwaiting(op, await ReadTargetOrNullAsync().ConfigureAwait(false))) return;
            }
        }
        catch (Exception) { /* 状態照会時の再確認に任せる。 */ }
        finally { lock (_sync) op.TerminalRecheckScheduled = false; }
    }

    private async Task<BuildTargetSnapshot?> ReadTargetOrNullAsync()
    {
        try { return await Task.Run(() => _adapter.ReadTargetSnapshot(TimeSpan.FromSeconds(5))).ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>非終端の完了 event を受けた操作を、同じ対象が idle なら結果不明で確定して所有を解放する。</summary>
    private bool TryFinishIdleAwaiting(ActiveOperation op, BuildTargetSnapshot? target)
    {
        if (target is null) return false;
        lock (_sync)
        {
            if (!ReferenceEquals(_active, op) || op.Terminal || !op.AwaitingTerminalState) return op.Terminal;
            if (!IsSameTargetIdle(target, op)) return false;
            FinishLocked(op, "unknown", "result_unknown",
                "Visual Studio later reported no build in progress, but it never reported this build's terminal result; the outcome is unknown and ownership was released.",
                null, op.DoneEvent?.FailedProjectCount, false);
        }
        CleanupIfTerminal(op, op.Subscription);
        return true;
    }

    private void CompleteFromDoneLocked(ActiveOperation op)
    {
        var evt = op.DoneEvent!;
        if (!evt.IsTerminalState)
        {
            // 完了 event 時点で build 状態がまだ終端でないことがある（実機 VS 2026 で 0.4 秒の build 後に発生）。
            // 同じ対象が idle と確認できるまで所有を保持し、確認後は結果不明で確定して lease を解放する。
            op.AwaitingTerminalState = true;
            op.Current = Snapshot(op, false, "Visual Studio did not report a terminal build state; operation ownership remains reserved until the target is idle.", "unknown", "unknown", null, evt.IsBuildInProgress, code: "result_unknown");
            return;
        }
        if (!evt.HasBuildResult)
        {
            FinishLocked(op, "unknown", "result_unknown", "Visual Studio reported no completed build result for the operation.", null, null, evt.IsBuildInProgress);
            return;
        }
        if (evt.IsCancelled) FinishLocked(op, "failed", "build_cancelled", "Visual Studio cancelled the build operation.", false, evt.FailedProjectCount, evt.IsBuildInProgress);
        else if (evt.IsBuildInProgress)
            FinishLocked(op, "unknown", "result_unknown", "Visual Studio did not provide a trustworthy terminal build result.", null, evt.FailedProjectCount, evt.IsBuildInProgress);
        else if (op.Action == BuildAction.Clean)
            FinishLocked(op, "succeeded", "ok", "Clean completed.", null, null, false);
        else if (evt.FailedProjectCount is null || evt.FailedProjectCount < 0)
            FinishLocked(op, "unknown", "result_unknown", "Visual Studio did not provide a trustworthy terminal build result.", null, evt.FailedProjectCount, false);
        else if (evt.FailedProjectCount == 0)
            FinishLocked(op, "succeeded", "ok", "Build completed successfully.", true, 0, false);
        else FinishLocked(op, "failed", "build_failed", "Build completed with failed projects.", false, evt.FailedProjectCount, false);
    }

    private void Finish(ActiveOperation op, string outcome, string code, string message, bool? buildSucceeded, int? failedProjectCount, bool? inProgress)
    {
        lock (_sync) FinishLocked(op, outcome, code, message, buildSucceeded, failedProjectCount, inProgress);
    }

    private BuildOperationResult RecordWaitResult(ActiveOperation op, string code, string message)
    {
        lock (_sync)
        {
            if (op.Terminal) return op.Current;
            var waitResult = Snapshot(op, false, message, op.Current.State, null, null, op.Current.IsBuildInProgress, code: code);
            if (!op.ForeignActivity) op.Current = waitResult;
            return waitResult;
        }
    }

    private void FinishLocked(ActiveOperation op, string outcome, string code, string message, bool? buildSucceeded, int? failedProjectCount, bool? inProgress)
    {
        op.Terminal = true;
        var state = outcome == "unknown" ? "unknown" : "completed";
        op.Current = Snapshot(op, code is "ok", message, state, outcome, buildSucceeded, inProgress, true, failedProjectCount, code);
        if (!_terminal.Any(item => item.OperationId == op.Id))
        {
            _terminal.AddFirst(op.Current);
            while (_terminal.Count > RetainedTerminalCount) _terminal.RemoveLast();
        }
        op.Completed.TrySetResult();
    }

    private void CleanupIfTerminal(ActiveOperation op, IDisposable? subscription)
    {
        lock (_sync)
        {
            if (!op.Terminal || op.GateReleased || op.CleanupScheduled) return;
            if (op.SubscriptionCleanupUncertain) return;
            op.CleanupScheduled = true;
        }

        Task cleanup;
        try
        {
            cleanup = subscription is null ? Task.CompletedTask : _adapter.DisposeSubscriptionAsync(subscription);
        }
        catch { return; }

        _ = ReleaseAfterCleanupAsync(op, cleanup);
    }

    private async Task ReleaseAfterCleanupAsync(ActiveOperation op, Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch { return; }

        lock (_sync)
        {
            if (!op.Terminal || op.GateReleased) return;
            op.GateReleased = true;
            if (ReferenceEquals(_active, op)) _active = null;
        }
        _workspaceGate.Release();
    }

    private static string? ValidateTarget(BuildTargetSnapshot target, string expectedPath, int pid, long ticks)
    {
        if (!target.TargetProcessId.HasValue || !target.TargetProcessStartUtcTicks.HasValue) return "target_identity_required";
        if (target.TargetProcessId != pid || target.TargetProcessStartUtcTicks != ticks) return "stale_target";
        if (target.RegisteredProcessId != pid || target.RegisteredProcessStartUtcTicks != ticks) return "target_not_registered";
        if (!target.DteConnected) return "dte_disconnected";
        if (string.IsNullOrWhiteSpace(target.SolutionPath)) return "no_solution";
        if (target.SolutionPath.Length > 2048) return "invalid_argument";
        if (!Path.IsPathFullyQualified(expectedPath) || !Path.IsPathFullyQualified(target.SolutionPath)) return "invalid_argument";
        if (!PathsEqual(target.SolutionPath, expectedPath)) return "solution_mismatch";
        if (!target.IsDesignMode) return "invalid_debugger_state";
        if (string.IsNullOrWhiteSpace(target.Configuration)) return "invalid_argument";
        if (target.IsBuildInProgress || target.HasForeignOrUncertainBuild) return "build_busy";
        return null;
    }

    private static bool IsSameTargetIdle(BuildTargetSnapshot target, ActiveOperation operation) =>
        target.TargetProcessId == operation.TargetProcessId &&
        target.TargetProcessStartUtcTicks == operation.TargetProcessStartUtcTicks &&
        target.RegisteredProcessId == operation.TargetProcessId &&
        target.RegisteredProcessStartUtcTicks == operation.TargetProcessStartUtcTicks &&
        target.DteConnected && PathsEqual(target.SolutionPath, operation.SolutionPath) &&
        !target.IsBuildInProgress && !target.HasForeignOrUncertainBuild;

    private static bool PathsEqual(string? a, string? b)
    {
        try { return a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
    private static string GuardMessage(string code) => code switch
    {
        "stale_target" => "The Visual Studio process identity is stale.", "target_not_registered" => "The requested Visual Studio process is not the registered target.", "dte_disconnected" => "The registered Visual Studio automation object is disconnected.", "no_solution" => "No solution is open.", "solution_mismatch" => "The active solution does not match the expected absolute path.", "invalid_debugger_state" => "Build operations are available only in Visual Studio design mode.", "build_busy" => "A manual, external, or uncertain build is active.", "invalid_argument" => "The solution or active configuration is invalid.", _ => "The target could not be validated."
    };
    private static string Limit(string value) => value.Length <= MaxMessageLength ? value : value[..MaxMessageLength];
    private static string ExceptionCode(Exception exception) => exception switch
    {
        TimeoutException => "com_timeout",
        COMException => "com_error",
        BuildActionUnavailableException => "build_action_unavailable",
        _ => "operation_failed"
    };
    private static long Elapsed(long start) => (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    private static BuildOperationResult IdleResult() => new(true, "ok", "No build operation has been recorded.", null, null, null, null, null, null, "idle", null, null, null, null, false, 0, 15000);
    private static BuildOperationResult Snapshot(ActiveOperation op, bool success, string message, string state, string? outcome, bool? buildSucceeded, bool? inProgress, bool includeFailedCount = false, int? failedProjectCount = null, string? code = null) =>
        new(success, code ?? (success ? "ok" : "operation_failed"), Limit(message), op.TargetProcessId, op.Id, op.Action.ToString().ToLowerInvariant(), op.SolutionPath, op.Configuration, op.Platform, state, outcome, buildSucceeded, includeFailedCount ? failedProjectCount : null, includeFailedCount ? failedProjectCount : null, inProgress, Elapsed(op.Started), op.TimeoutMilliseconds);

    private sealed class ActiveOperation(string id, BuildAction action, string solutionPath, string configuration, string? platform, int? targetProcessId, long? targetProcessStartUtcTicks, int timeoutMilliseconds, long started)
    {
        public string Id { get; } = id;
        public BuildAction Action { get; } = action;
        public string SolutionPath { get; } = solutionPath;
        public string Configuration { get; } = configuration;
        public string? Platform { get; } = platform;
        public int? TargetProcessId { get; } = targetProcessId;
        public long? TargetProcessStartUtcTicks { get; } = targetProcessStartUtcTicks;
        public int TimeoutMilliseconds { get; } = timeoutMilliseconds;
        public long Started { get; } = started;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BuildOperationResult Current { get; set; } = null!;
        public IDisposable? Subscription { get; set; }
        public bool Dispatching, Dispatched, DispatchUncertain, BeginObserved, DoneObserved, FastDone, ForeignActivity, Terminal, SubscriptionCleanupUncertain, CleanupScheduled, GateReleased;
        /// <summary>完了 event は届いたが、その時点の build 状態が終端でなかった。</summary>
        public bool AwaitingTerminalState, TerminalRecheckScheduled;
        public string? ForeignActivityReason { get; set; }
        public BuildEventSnapshot? DoneEvent { get; set; }
    }
}
