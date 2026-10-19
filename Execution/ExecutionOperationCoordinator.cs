namespace VisualStudioMcpServer.Execution;

internal interface IExecutionAdapter
{
    /// <summary>expectedSolutionPath は provider が IDE 側で workspace を照合するために使う（#54 レビュー）。</summary>
    Task<ExecutionTargetSnapshot> ReadTargetSnapshotAsync(string expectedSolutionPath, TimeSpan remaining, CancellationToken cancellationToken);
    /// <summary>1 回だけ dispatch する。remaining は coordinator がこの dispatch を待つ残り時間で、adapter はこれより前に確定させる。</summary>
    Task<ExecutionDispatchObservation> DispatchAsync(ExecutionRequest request, string operationId, TimeSpan remaining, CancellationToken cancellationToken);
}

internal interface IExecutionMutationLease : IDisposable { }

/// <summary>Common, bounded execution contract. Production dispatch remains unavailable until a provider adapter is proven.</summary>
internal sealed class ExecutionOperationCoordinator
{
    private const int MaxActiveOperations = 8;
    private const int MaxRetainedCompleted = 64;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private readonly IExecutionAdapter _adapter;
    private readonly Func<bool> _hasMutationOwner;
    private readonly Func<IExecutionMutationLease?> _acquireWorkspaceLease;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, OperationRecord> _byRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> _byOperationId = new(StringComparer.Ordinal);

    public ExecutionOperationCoordinator(
        IExecutionAdapter adapter,
        Func<bool> hasMutationOwner,
        Func<DateTimeOffset>? utcNow = null,
        Func<IExecutionMutationLease?>? acquireWorkspaceLease = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _hasMutationOwner = hasMutationOwner ?? throw new ArgumentNullException(nameof(hasMutationOwner));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _acquireWorkspaceLease = acquireWorkspaceLease ?? TryAcquireWorkspaceLease;
    }

    public async Task<ExecutionOperationResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var invalid = ValidateRequestShape(request);
        if (invalid is not null) return Denied(invalid, Message(invalid), request?.TargetProcessId, request?.ServerInstanceId, request?.ServerEpoch);
        var fingerprint = ExecutionRequestIdentity.Fingerprint(request);
        OperationRecord record;
        var startCore = false;
        lock (_sync)
        {
            SettleOverdueLocked();
            PruneCompletedLocked();
            if (_byRequestId.TryGetValue(request.RequestId, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return Denied("request_id_conflict", Message("request_id_conflict"), request.TargetProcessId, request.ServerInstanceId, request.ServerEpoch);
                record = existing;
            }
            else
            {
                if (ActiveCountLocked() >= MaxActiveOperations)
                    return Denied("operation_store_full", Message("operation_store_full"), request.TargetProcessId, request.ServerInstanceId, request.ServerEpoch);
                var now = _utcNow();
                var executionDeadline = request.ExecutionDeadlineUtc is { } requestedDeadline && requestedDeadline < now + DefaultExecutionTimeout
                    ? requestedDeadline
                    : now + DefaultExecutionTimeout;
                record = new OperationRecord(request.RequestId, fingerprint,
                    Guid.NewGuid().ToString("N"), now, request.TargetProcessId, request.TargetProcessStartUtcTicks, request.ServerInstanceId, request.ServerEpoch,
                    executionDeadline);
                _byRequestId.Add(record.RequestId, record);
                _byOperationId.Add(record.OperationId, record);
                startCore = true;
            }
        }

        if (startCore) _ = ExecuteCoreAsync(record, request, cancellationToken);
        return await WaitForCallerAsync(record, request.CallerWaitTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteCoreAsync(OperationRecord record, ExecutionRequest request, CancellationToken preDispatchCancellation)
    {
        try
        {
            var code = ValidatePermissionAndBinding(request);
            if (code is not null) { RemoveDenied(record, code, request); return; }
            if (preDispatchCancellation.IsCancellationRequested) { RemoveDenied(record, "cancelled_before_dispatch", request); return; }
            if (!_hasMutationOwner()) { RemoveDenied(record, "target_owner_busy", request); return; }
            var lease = _acquireWorkspaceLease();
            if (lease is null) { RemoveDenied(record, "workspace_busy", request); return; }
            record.WorkspaceLease = lease;

            var remaining = Remaining(record);
            if (remaining <= TimeSpan.Zero) { RemoveDenied(record, "deadline_expired", request); return; }

            ExecutionTargetSnapshot snapshot;
            try
            {
                snapshot = await _adapter.ReadTargetSnapshotAsync(request.ExpectedSolutionPath, remaining, preDispatchCancellation)
                    .WaitAsync(remaining, preDispatchCancellation).ConfigureAwait(false);
            }
            catch (TimeoutException) { RemoveDenied(record, "com_timeout", request); return; }
            catch (Exception) { RemoveDenied(record, "target_unavailable", request); return; }

            code = ValidateSnapshot(request, snapshot);
            if (code is not null)
            {
                // provider が起動前提を満たさないと判断した場合は、その理由を message に含めて dispatch 前に拒否する。
                var blocked = code == snapshot.LaunchBlockedCode && snapshot.LaunchBlockedReason is { } reason
                    ? $"{Message(code)} Reason: {reason}." : null;
                RemoveDenied(record, code, request, blocked);
                return;
            }
            remaining = Remaining(record);
            if (remaining <= TimeSpan.Zero) { RemoveDenied(record, "deadline_expired", request); return; }
            if (preDispatchCancellation.IsCancellationRequested) { RemoveDenied(record, "cancelled_before_dispatch", request); return; }
            if (!_hasMutationOwner()) { RemoveDenied(record, "target_owner_busy", request); return; }

            lock (_sync) record.DispatchStarted = true;

            ExecutionDispatchObservation observation;
            Task<ExecutionDispatchObservation>? dispatchTask = null;
            try
            {
                dispatchTask = _adapter.DispatchAsync(request, record.OperationId, remaining, CancellationToken.None);
                observation = await dispatchTask.WaitAsync(remaining).ConfigureAwait(false);
                lock (_sync) record.DispatchSettled = true;
            }
            catch (ExecutionDispatchException ex) when (ex.ExecutionStarted)
            {
                lock (_sync) record.DispatchSettled = true;
                observation = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The action may have started; query this operation without replaying it.", true);
            }
            catch (TimeoutException)
            {
                lock (_sync) record.DispatchSettled = dispatchTask?.IsCompleted == true;
                observation = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The action may have started; query this operation without replaying it.", true);
            }
            catch (OperationCanceledException)
            {
                lock (_sync) record.DispatchSettled = dispatchTask?.IsCompleted == true;
                observation = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The dispatch was cancelled after it may have started; query this operation without replaying it.", true);
            }
            catch (ExecutionDispatchException ex)
            {
                lock (_sync) record.DispatchSettled = true;
                observation = new ExecutionDispatchObservation(ExecutionState.Failed, "dispatch_failed", Limit(ex.Message), false);
            }
            catch (Exception)
            {
                lock (_sync) record.DispatchSettled = dispatchTask?.IsCompleted == true;
                observation = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The action outcome is unknown; query this operation without replaying it.", true);
            }

            if (observation.State == ExecutionState.Failed && observation.MayHaveChanged)
                observation = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The action may have changed the target; its outcome remains uncertain.", true,
                    observation.DebugSessionId, observation.ProcessSetSnapshotId);
            var result = ToResult(record, observation);
            var settleLater = false;
            lock (_sync)
            {
                record.DispatchReturned = true;
                if (record.DispatchSettled && record.PendingObservation is { } pending)
                {
                    observation = pending;
                    result = ToResult(record, observation);
                    record.PendingObservation = null;
                }
                if (!IsTerminal(record.State))
                {
                    record.Current = result;
                    record.State = observation.State;
                    if (IsTerminal(record.State)) record.CompletedAt = _utcNow();
                    if (IsTerminal(record.State)) ReleaseLeaseLocked(record);
                }
                // accepted / uncertain のまま観測されない操作は実行期限で確定させ、lease を保持し続けない。
                SettleOverdueLocked();
                settleLater = !IsSettled(record);
                result = record.Current;
                PruneCompletedLocked();
            }
            record.Ready.TrySetResult(result);
            if (settleLater) _ = SettleAtDeadlineAsync(record);
        }
        catch (OperationCanceledException) when (preDispatchCancellation.IsCancellationRequested)
        {
            RemoveDenied(record, "cancelled_before_dispatch", request);
        }
        catch
        {
            RemoveDenied(record, "operation_failed", request);
        }
    }

    private async Task<ExecutionOperationResult> WaitForCallerAsync(OperationRecord record, int waitMilliseconds, CancellationToken cancellationToken)
    {
        try { return await record.Ready.Task.WaitAsync(TimeSpan.FromMilliseconds(waitMilliseconds), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return ResultOrPending(record); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return ResultOrPending(record); }
    }

    private ExecutionOperationResult ResultOrPending(OperationRecord record)
    {
        if (record.Ready.Task.IsCompleted) return record.Ready.Task.GetAwaiter().GetResult();
        return PendingResult(record);
    }

    private ExecutionOperationResult PendingResult(OperationRecord record)
    {
        lock (_sync)
        {
            return record.Current with
            {
                Success = false,
                Code = "execution_pending",
                Message = "The caller wait budget expired. Query this operation ID to retrieve its eventual result.",
                OperationId = record.OperationId,
                State = StateText(record.State),
                MayHaveChanged = record.State is ExecutionState.Accepted or ExecutionState.Uncertain
            };
        }
    }

    public ExecutionOperationResult Query(string operationId, int targetProcessId, long targetProcessStartUtcTicks,
        string serverInstanceId, Guid serverEpoch)
    {
        lock (_sync)
        {
            SettleOverdueLocked();
            PruneCompletedLocked();
            if (!_byOperationId.TryGetValue(operationId, out var record) ||
                record.TargetProcessId != targetProcessId || record.ServerInstanceId != serverInstanceId ||
                record.ServerEpoch != serverEpoch)
                return Denied("operation_not_found", Message("operation_not_found"), targetProcessId, serverInstanceId, serverEpoch);
            // Start ticks are kept explicitly in the request fingerprint; the record also binds queries to the same IDE identity.
            if (record.TargetProcessStartUtcTicks != targetProcessStartUtcTicks)
                return Denied("operation_not_found", Message("operation_not_found"), targetProcessId, serverInstanceId, serverEpoch);
            return record.Current;
        }
    }

    /// <summary>
    /// 保持中の operation が確定する（終端、実行期限による結果不明、または dispatch 前の拒否）まで最大 wait だけ待ち、
    /// その時点の結果を返す。待機は操作を取り消さず再送もしない。不明な operation ID は null を返す。
    /// </summary>
    public async Task<ExecutionOperationResult?> WaitForSettlementAsync(string operationId, TimeSpan wait, CancellationToken cancellationToken)
    {
        OperationRecord? record;
        lock (_sync) _byOperationId.TryGetValue(operationId, out record);
        if (record is null) return null;
        if (wait > TimeSpan.Zero)
        {
            try { await record.Settled.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        lock (_sync)
        {
            SettleOverdueLocked();
            return record.Current;
        }
    }

    public ExecutionOperationResult ReportObservation(string operationId, int targetProcessId, long targetProcessStartUtcTicks,
        string serverInstanceId, Guid serverEpoch, ExecutionDispatchObservation observation)
    {
        lock (_sync)
        {
            SettleOverdueLocked();
            if (!_byOperationId.TryGetValue(operationId, out var record) || record.TargetProcessId != targetProcessId ||
                record.TargetProcessStartUtcTicks != targetProcessStartUtcTicks || record.ServerInstanceId != serverInstanceId || record.ServerEpoch != serverEpoch)
                return Denied("operation_not_found", Message("operation_not_found"), targetProcessId, serverInstanceId, serverEpoch);
            if (!record.DispatchStarted) return Denied("operation_not_dispatched", "The operation has not entered dispatch.", targetProcessId, serverInstanceId, serverEpoch);
            if (IsTerminal(record.State)) return Denied("operation_already_settled", "The operation is already settled.", targetProcessId, serverInstanceId, serverEpoch);
            var next = observation;
            if (next.State is ExecutionState.Validating || (record.State == ExecutionState.Uncertain && next.State == ExecutionState.Accepted))
                return Denied("invalid_operation_transition", "The observation would move the operation backwards.", targetProcessId, serverInstanceId, serverEpoch);
            if (observation.State == ExecutionState.Failed && observation.MayHaveChanged)
                next = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The action may have changed the target; its outcome remains uncertain.", true,
                    observation.DebugSessionId, observation.ProcessSetSnapshotId);
            if (IsTerminal(next.State) && !record.DispatchSettled)
            {
                record.PendingObservation = next;
                next = new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                    "The target state was observed, but dispatch cleanup has not settled.", true,
                    next.DebugSessionId, next.ProcessSetSnapshotId);
            }
            record.Current = ToResult(record, next);
            record.State = next.State;
            if (IsTerminal(next.State))
            {
                record.CompletedAt = _utcNow();
                ReleaseLeaseLocked(record);
            }
            record.Ready.TrySetResult(record.Current);
            PruneCompletedLocked();
            return record.Current;
        }
    }

    private ExecutionOperationResult RemoveDenied(OperationRecord record, string code, ExecutionRequest request, string? message = null)
    {
        var denied = Denied(code, Limit(message ?? Message(code)), request.TargetProcessId, request.ServerInstanceId, request.ServerEpoch);
        lock (_sync)
        {
            _byRequestId.Remove(record.RequestId);
            _byOperationId.Remove(record.OperationId);
            record.Current = denied;
            record.State = ExecutionState.Failed;
            record.CompletedAt = _utcNow();
            ReleaseLeaseLocked(record);
        }
        record.Ready.TrySetResult(denied);
        return denied;
    }

    private static ExecutionOperationResult ToResult(OperationRecord record, ExecutionDispatchObservation observation) =>
        new(observation.State is ExecutionState.Accepted or ExecutionState.Observed,
            observation.Code, Limit(observation.Message), record.OperationId, StateText(observation.State),
            observation.MayHaveChanged, record.TargetProcessId, record.ServerInstanceId, record.ServerEpoch,
            observation.DebugSessionId, observation.ProcessSetSnapshotId, observation.ProcessOutcomes);

    private ExecutionOperationResult Denied(string code, string message, int? pid, string? serverInstanceId, Guid? epoch) =>
        new(false, code, message, null, "not_started", false, pid, serverInstanceId, epoch, null, null);

    private static string? ValidateRequestShape(ExecutionRequest? request)
    {
        if (request is null || !ExecutionRequestIdentity.IsValidRequestId(request.RequestId)) return "invalid_request_id";
        if (request.TargetProcessId <= 0 || request.TargetProcessStartUtcTicks <= 0 || string.IsNullOrWhiteSpace(request.ServerInstanceId) || request.ServerEpoch == Guid.Empty)
            return "target_identity_required";
        if (string.IsNullOrWhiteSpace(request.ExpectedSolutionPath) || !Path.IsPathFullyQualified(request.ExpectedSolutionPath)) return "expected_solution_required";
        if (request.CallerWaitTimeoutMilliseconds is < 1 or > 30_000) return "invalid_argument";
        if (request.ExecutionDeadlineUtc is { } deadline && deadline <= DateTimeOffset.MinValue) return "invalid_argument";
        return null;
    }

    private static string? ValidatePermissionAndBinding(ExecutionRequest request)
    {
        return request.Action switch
        {
            ExecutionAction.StartDebugging when !request.AllowDebuggerControl => "debugger_policy_required",
            ExecutionAction.StartWithoutDebugging when !request.AllowLaunch => "launch_policy_required",
            ExecutionAction.StopDebugging when !request.AllowDebuggerControl => "debugger_policy_required",
            ExecutionAction.StartDebugging or ExecutionAction.StartWithoutDebugging when string.IsNullOrWhiteSpace(request.ExpectedStartupSnapshotId) => "startup_snapshot_required",
            ExecutionAction.StopDebugging when string.IsNullOrWhiteSpace(request.ExpectedDebugSessionId) || string.IsNullOrWhiteSpace(request.ExpectedProcessSetSnapshotId) => "debug_session_required",
            _ => null
        };
    }

    private static string? ValidateSnapshot(ExecutionRequest request, ExecutionTargetSnapshot snapshot)
    {
        if (snapshot.TargetProcessId != request.TargetProcessId || snapshot.TargetProcessStartUtcTicks != request.TargetProcessStartUtcTicks ||
            snapshot.ServerInstanceId != request.ServerInstanceId || snapshot.ServerEpoch != request.ServerEpoch) return "stale_target";
        // provider がなければ solution を確認できないため、solution より先に判定する。
        if (!snapshot.ProviderAvailable) return "requires_vsix_provider";
        if (!PathEquals(snapshot.SolutionPath, request.ExpectedSolutionPath)) return "solution_mismatch";
        if (request.Action is not ExecutionAction.StopDebugging && snapshot.BuildMayOccur && !request.AllowBuild) return "build_policy_required";
        return request.Action switch
        {
            ExecutionAction.StartDebugging when snapshot.State != "design" => "invalid_debugger_state",
            ExecutionAction.StartWithoutDebugging when snapshot.State != "design" => "invalid_debugger_state",
            ExecutionAction.StartDebugging or ExecutionAction.StartWithoutDebugging when snapshot.StartupSnapshotId != request.ExpectedStartupSnapshotId => "stale_startup_snapshot",
            ExecutionAction.StartDebugging or ExecutionAction.StartWithoutDebugging when snapshot.LaunchBlockedCode is { } blocked => blocked,
            ExecutionAction.StopDebugging when snapshot.State is not "running" and not "break" => "invalid_debugger_state",
            ExecutionAction.StopDebugging when snapshot.DebugSessionId != request.ExpectedDebugSessionId || snapshot.ProcessSetSnapshotId != request.ExpectedProcessSetSnapshotId => "debug_session_mismatch",
            ExecutionAction.StopDebugging when !snapshot.SessionAgentOwned && !request.AllowStopExistingSession => "existing_session_policy_required",
            _ => null
        };
    }

    private static bool PathEquals(string? left, string right)
    {
        if (left is null) return false;
        try { return string.Equals(NormalizePath(left), NormalizePath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch { return false; }
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && full.Length <= root.Length) return root;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private TimeSpan Remaining(OperationRecord record) => record.ExecutionDeadlineUtc - _utcNow();

    /// <summary>
    /// 実行中の操作と、期限で結果不明として確定した操作を数える。後者は lease を解放済みだが、結果が分からないため
    /// store から追い出さず（追い出すと同じ requestId の再 dispatch や照会の operation_not_found を招く）、枠を保持する。
    /// </summary>
    private int ActiveCountLocked() => _byOperationId.Values.Count(r => !IsSettled(r) || IsRetainedUncertain(r));

    /// <summary>dispatch が戻った後も観測されない操作を実行期限で確定させる（期限は最大 30 秒）。</summary>
    private async Task SettleAtDeadlineAsync(OperationRecord record)
    {
        try
        {
            var delay = Remaining(record);
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        }
        catch (Exception) { /* 遅延の失敗は次の照会時の確定に任せる。 */ }
        lock (_sync) SettleOverdueLocked();
    }

    /// <summary>
    /// 実行期限を過ぎた accepted / uncertain の操作を result_unknown として確定し、workspace lease を解放する。
    /// 記録は照会のために残し、後から届いた正確な観測はその記録だけを更新できる。再送はしない。
    /// </summary>
    private void SettleOverdueLocked()
    {
        var now = _utcNow();
        foreach (var record in _byOperationId.Values)
        {
            if (IsSettled(record) || !record.DispatchReturned || now < record.ExecutionDeadlineUtc) continue;
            record.State = ExecutionState.Uncertain;
            record.DeadlineSettled = true;
            record.Current = record.Current with
            {
                Success = false,
                Code = "result_unknown",
                Message = "The action was not observed before its execution deadline. The workspace lease was released; inspect Visual Studio before retrying. The operation is not replayed.",
                State = StateText(ExecutionState.Uncertain),
                MayHaveChanged = true
            };
            record.CompletedAt = now;
            ReleaseLeaseLocked(record);
            record.Ready.TrySetResult(record.Current);
        }
    }

    private static bool IsSettled(OperationRecord record) => IsTerminal(record.State) || record.DeadlineSettled;

    /// <summary>期限で結果不明として確定したまま、正確な観測で終端していない記録。再送を防ぐため保持し続ける。</summary>
    private static bool IsRetainedUncertain(OperationRecord record) => record.DeadlineSettled && !IsTerminal(record.State);

    private static IExecutionMutationLease? TryAcquireWorkspaceLease()
    {
        if (!WorkspaceMutationGate.Shared.TryAcquire()) return null;
        return new WorkspaceLease(WorkspaceMutationGate.Shared);
    }

    /// <summary>操作の確定時に lease を解放し、確定を待つ呼び出し元（WaitForSettlementAsync）へ知らせる。</summary>
    private static void ReleaseLeaseLocked(OperationRecord record)
    {
        var lease = record.WorkspaceLease;
        record.WorkspaceLease = null;
        lease?.Dispose();
        record.Settled.TrySetResult(true);
    }

    private void PruneCompletedLocked()
    {
        var cutoff = _utcNow() - CompletedRetention;
        foreach (var record in _byOperationId.Values.Where(r => IsSettled(r) && !IsRetainedUncertain(r) && r.CompletedAt <= cutoff).ToArray())
            RemoveLocked(record);
        var completed = _byOperationId.Values.Where(r => IsSettled(r) && !IsRetainedUncertain(r)).OrderBy(r => r.CompletedAt).ToArray();
        foreach (var record in completed.Take(Math.Max(0, completed.Length - MaxRetainedCompleted))) RemoveLocked(record);
    }

    private void RemoveLocked(OperationRecord record)
    {
        _byOperationId.Remove(record.OperationId);
        _byRequestId.Remove(record.RequestId);
    }

    private static bool IsTerminal(ExecutionState state) => state is ExecutionState.Observed or ExecutionState.Failed;
    private static string StateText(ExecutionState state) => state switch
    {
        ExecutionState.Accepted => "accepted",
        ExecutionState.Observed => "observed",
        ExecutionState.Failed => "failed",
        ExecutionState.Uncertain => "uncertain",
        _ => "validating"
    };
    private static string Limit(string value) => value.Length <= 1024 ? value : value[..1024];
    private static string Message(string code) => code switch
    {
        "invalid_request_id" => "requestId must contain 1 through 128 printable ASCII characters.",
        "target_identity_required" => "The bound Visual Studio process and server identity are required.",
        "expected_solution_required" => "An absolute expected solution path is required.",
        "debugger_policy_required" => "allowDebuggerControl=true is required.",
        "launch_policy_required" => "allowLaunch=true is required.",
        "build_policy_required" => "allowBuild=true is required when this launch may build.",
        "startup_snapshot_required" => "A current startup snapshot identity is required before launch.",
        "debug_session_required" => "The expected debugger session and process-set snapshot are required.",
        "target_owner_busy" => "The Visual Studio mutation owner is unavailable.",
        "workspace_busy" => "Another workspace mutation or build is active.",
        "deadline_expired" => "The execution deadline expired before dispatch.",
        "com_timeout" => "The target snapshot exceeded its bounded inspection time.",
        "target_unavailable" => "The bound Visual Studio target is unavailable.",
        "requires_vsix_provider" => "This operation requires a verified VSIX provider.",
        "invalid_debugger_state" => "The debugger state does not permit this action.",
        "stale_startup_snapshot" => "The startup snapshot changed; capture a fresh snapshot.",
        "solution_mismatch" => "The active solution does not match expectedSolutionPath.",
        "debug_session_mismatch" => "The active debugger session or process set changed.",
        "existing_session_policy_required" => "Stopping a pre-existing session requires allowStopExistingSession=true.",
        "operation_store_full" => "The bounded operation store is full; no action was dispatched.",
        "request_id_conflict" => "requestId is already retained for a different normalized payload.",
        "operation_not_found" => "The operation is unknown, expired, or belongs to another server generation.",
        "operation_not_dispatched" => "The operation has not entered dispatch and cannot yet be observed.",
        "operation_already_settled" => "The operation is already settled.",
        "invalid_operation_transition" => "The operation observation would move its state backwards.",
        "cancelled_before_dispatch" => "The caller cancelled before dispatch.",
        "operation_failed" => "The execution request failed before dispatch.",
        "unsupported" => "The startup configuration is not supported for a guarded launch.",
        "user_action_required" => "The startup configuration needs user action (for example saving documents) before a guarded launch.",
        _ => "The execution request was rejected before dispatch."
    };

    private sealed class OperationRecord(string requestId, string fingerprint, string operationId, DateTimeOffset createdAt,
        int targetProcessId, long targetProcessStartUtcTicks, string serverInstanceId, Guid serverEpoch, DateTimeOffset executionDeadlineUtc)
    {
        public string RequestId { get; } = requestId;
        public string Fingerprint { get; } = fingerprint;
        public string OperationId { get; } = operationId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int TargetProcessId { get; } = targetProcessId;
        public long TargetProcessStartUtcTicks { get; } = targetProcessStartUtcTicks;
        public string ServerInstanceId { get; } = serverInstanceId;
        public Guid ServerEpoch { get; } = serverEpoch;
        public DateTimeOffset ExecutionDeadlineUtc { get; } = executionDeadlineUtc;
        public ExecutionState State { get; set; } = ExecutionState.Validating;
        public bool DispatchStarted { get; set; }
        public bool DispatchSettled { get; set; }
        /// <summary>dispatch の待機が戻り、状態が設定された。</summary>
        public bool DispatchReturned { get; set; }
        /// <summary>観測されないまま実行期限で確定した。lease は解放済み。</summary>
        public bool DeadlineSettled { get; set; }
        public ExecutionDispatchObservation? PendingObservation { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public ExecutionOperationResult Current { get; set; } = new(false, "execution_pending", "The operation is being validated.", operationId, "validating", false, targetProcessId, serverInstanceId, serverEpoch, null, null);
        public TaskCompletionSource<ExecutionOperationResult> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>終端・期限確定・拒否のいずれかで確定した。</summary>
        public TaskCompletionSource<bool> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IExecutionMutationLease? WorkspaceLease { get; set; }
    }

    private sealed class WorkspaceLease(WorkspaceMutationGate gate) : IExecutionMutationLease
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
