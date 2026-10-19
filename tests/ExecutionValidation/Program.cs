using VisualStudioMcpServer.Execution;

var epoch = Guid.NewGuid();
var solution = Path.GetFullPath("fixture.sln");
var target = new ExecutionTargetSnapshot(41, 1001, "server-instance", epoch, solution, "Debug", "Any CPU",
    "startup-snapshot", "debug-session", "process-set", "design", true, false, true);
var checks = 0;
ExecutionOperationCoordinator Coordinator(IExecutionAdapter adapter, Func<bool>? owner = null, Func<DateTimeOffset>? clock = null) =>
    new(adapter, owner ?? (() => true), clock, () => new FakeLease());
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
    checks++;
}
ExecutionRequest Request(string id, ExecutionAction action = ExecutionAction.StartDebugging) => new(
    id, action, 41, 1001, "server-instance", epoch, solution,
    action == ExecutionAction.StopDebugging ? null : "startup-snapshot",
    action == ExecutionAction.StopDebugging ? "debug-session" : null,
    action == ExecutionAction.StopDebugging ? "process-set" : null,
    AllowDebuggerControl: action != ExecutionAction.StartWithoutDebugging,
    AllowLaunch: action == ExecutionAction.StartWithoutDebugging);

var deniedCases = new (string Name, ExecutionRequest Request, ExecutionTargetSnapshot Snapshot, bool Owner, string Code)[]
{
    ("missing debugger permission", Request("deny-policy") with { AllowDebuggerControl = false }, target, true, "debugger_policy_required"),
    ("missing launch permission", Request("deny-launch", ExecutionAction.StartWithoutDebugging) with { AllowLaunch = false }, target, true, "launch_policy_required"),
    ("stale target start time", Request("deny-stale"), target with { TargetProcessStartUtcTicks = 1002 }, true, "stale_target"),
    ("wrong server generation", Request("deny-generation"), target with { ServerEpoch = Guid.NewGuid() }, true, "stale_target"),
    ("wrong solution", Request("deny-solution"), target with { SolutionPath = Path.GetFullPath("other.sln") }, true, "solution_mismatch"),
    ("missing provider", Request("deny-provider"), target with { ProviderAvailable = false }, true, "requires_vsix_provider"),
    ("stale startup snapshot", Request("deny-startup"), target with { StartupSnapshotId = "new-snapshot" }, true, "stale_startup_snapshot"),
    ("paused start does not continue", Request("deny-paused"), target with { State = "break" }, true, "invalid_debugger_state"),
    ("build permission is separate", Request("deny-build"), target with { BuildMayOccur = true }, true, "build_policy_required"),
    ("mutation owner unavailable", Request("deny-owner"), target, false, "target_owner_busy"),
    ("missing stop binding", Request("deny-stop", ExecutionAction.StopDebugging) with { ExpectedProcessSetSnapshotId = null }, target, true, "debug_session_required"),
    ("wrong debug session", Request("deny-stop-session", ExecutionAction.StopDebugging) with { ExpectedDebugSessionId = "other-session" }, target with { State = "break" }, true, "debug_session_mismatch"),
    ("pre-existing session requires explicit permission", Request("deny-existing", ExecutionAction.StopDebugging), target with { State = "break", SessionAgentOwned = false }, true, "existing_session_policy_required"),
    ("unsupported startup is refused before dispatch", Request("deny-unsupported"), target with { LaunchBlockedCode = "unsupported", LaunchBlockedReason = "multiple_startup_projects" }, true, "unsupported"),
    ("dirty documents require user action before no-debug launch", Request("deny-dirty", ExecutionAction.StartWithoutDebugging), target with { LaunchBlockedCode = "user_action_required", LaunchBlockedReason = "dirty_documents" }, true, "user_action_required"),
    ("missing provider is reported before solution identity", Request("deny-provider-first"), target with { ProviderAvailable = false, SolutionPath = null }, true, "requires_vsix_provider")
};
foreach (var item in deniedCases)
{
    var adapter = new FakeAdapter(item.Snapshot);
    var result = await Coordinator(adapter, () => item.Owner).ExecuteAsync(item.Request);
    Check(result.Code == item.Code && result.OperationId is null && adapter.DispatchCount == 0,
        $"{item.Name} has zero dispatch");
}

{
    var adapter = new FakeAdapter(target);
    var coordinator = Coordinator(adapter);
    var result = await coordinator.ExecuteAsync(Request("bad request\n"));
    Check(result.Code == "invalid_request_id" && adapter.SnapshotCount == 0 && adapter.DispatchCount == 0,
        "request id rejects non-printable characters before inspection");
}

{
    var now = DateTimeOffset.UtcNow;
    var adapter = new FakeAdapter(target);
    var coordinator = Coordinator(adapter, clock: () => now);
    var completed = await coordinator.ExecuteAsync(Request("expired-operation"));
    now = now.AddMinutes(6);
    Check(coordinator.Query(completed.OperationId!, 41, 1001, "server-instance", epoch).Code == "operation_not_found",
        "completed operation expires after the retained TTL");
}

{
    var now = DateTimeOffset.UtcNow;
    var adapter = new FakeAdapter(target);
    var result = await Coordinator(adapter, clock: () => now).ExecuteAsync(
        Request("deadline") with { ExecutionDeadlineUtc = now.AddMilliseconds(-1) });
    Check(result.Code == "deadline_expired" && adapter.DispatchCount == 0, "expired execution deadline has zero dispatch");
}

{
    var requestA = Request("root-fingerprint") with { ExpectedSolutionPath = Path.GetPathRoot(Environment.CurrentDirectory)! };
    var requestB = requestA with { ExpectedSolutionPath = requestA.ExpectedSolutionPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
    Check(ExecutionRequestIdentity.Fingerprint(requestA) == ExecutionRequestIdentity.Fingerprint(requestB),
        "filesystem root and alternate separator normalize without becoming drive-relative");
}

{
    var snapshotReady = new TaskCompletionSource<ExecutionTargetSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var adapter = new FakeAdapter(target) { SnapshotTask = snapshotReady.Task };
    var coordinator = Coordinator(adapter);
    var request = Request("wait-budget") with { CallerWaitTimeoutMilliseconds = 10 };
    var waiting = await coordinator.ExecuteAsync(request);
    Check(waiting.Code == "execution_pending" && waiting.OperationId is not null && adapter.DispatchCount == 0,
        "caller wait budget returns a queryable pending operation without cancelling execution");
    var premature = coordinator.ReportObservation(waiting.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "premature", true));
    Check(premature.Code == "operation_not_dispatched", "provider cannot settle an operation before dispatch starts");
    snapshotReady.SetResult(target);
    var eventual = await coordinator.ExecuteAsync(request with { CallerWaitTimeoutMilliseconds = 1000 });
    Check(eventual.State == "observed" && adapter.DispatchCount == 1,
        "same request id can query completion after the caller's shorter wait expires");
}

{
    var dispatchReady = new TaskCompletionSource<ExecutionDispatchObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
    var released = 0;
    var adapter = new FakeAdapter(target) { DispatchTask = dispatchReady.Task };
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, acquireWorkspaceLease: () => new FakeLease(() => released++));
    var request = Request("observe-before-cleanup") with { CallerWaitTimeoutMilliseconds = 10 };
    var pending = await coordinator.ExecuteAsync(request);
    var early = coordinator.ReportObservation(pending.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "Target changed.", true, "session-launched", "set-launched"));
    Check(early.State == "uncertain" && released == 0,
        "early target observation stays uncertain and holds the lease until dispatch cleanup settles");
    dispatchReady.SetResult(new ExecutionDispatchObservation(ExecutionState.Accepted, "accepted", "Dispatch returned.", true));
    await Task.Delay(20);
    var settled = coordinator.Query(pending.OperationId!, 41, 1001, "server-instance", epoch);
    Check(settled.State == "observed" && released == 1,
        "pre-cleanup observation settles only after dispatch returns");
}

{
    var adapter = new FakeAdapter(target) { Observation = new ExecutionDispatchObservation(ExecutionState.Accepted, "accepted", "Launch accepted.", true, "session-new", "set-new") };
    var coordinator = Coordinator(adapter);
    var request = Request("deduplicate");
    var first = await coordinator.ExecuteAsync(request);
    var duplicate = await coordinator.ExecuteAsync(request with { CallerWaitTimeoutMilliseconds = 5000 });
    var conflict = await coordinator.ExecuteAsync(request with { AllowBuild = true });
    Check(first.OperationId == duplicate.OperationId && adapter.DispatchCount == 1,
        "same request id and normalized payload reuses one operation");
    Check(conflict.Code == "request_id_conflict" && conflict.OperationId is null && adapter.DispatchCount == 1,
        "same request id with changed policy conflicts before dispatch");
    Check(coordinator.Query(first.OperationId!, 41, 1001, "server-instance", epoch).DebugSessionId == "session-new",
        "query returns operation-bound observation");
    Check(coordinator.Query(first.OperationId!, 42, 1001, "server-instance", epoch).Code == "operation_not_found" &&
        coordinator.Query(first.OperationId!, 41, 1001, "server-instance", Guid.NewGuid()).Code == "operation_not_found",
        "query rejects another target or server generation");
    var settled = coordinator.ReportObservation(first.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "Observed terminal state.", true, "session-new", "set-new"));
    Check(settled.State == "observed", "accepted operation can settle with an exact observation");
    Check(coordinator.ReportObservation(first.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "duplicate terminal event", true)).Code == "operation_already_settled",
        "duplicate or stale terminal observation cannot rewrite a completed operation");
}

{
    var adapter = new FakeAdapter(target) { DispatchTask = Task.FromException<ExecutionDispatchObservation>(new ExecutionDispatchException("reply lost", true)) };
    var coordinator = Coordinator(adapter);
    var request = Request("lost-reply");
    var first = await coordinator.ExecuteAsync(request);
    var retry = await coordinator.ExecuteAsync(request);
    Check(first.Code == "result_unknown" && first.State == "uncertain" && first.MayHaveChanged &&
        first.OperationId == retry.OperationId && adapter.DispatchCount == 1,
        "uncertain reply retains same operation and never replays");
}

{
    var adapter = new FakeAdapter(target);
    var coordinator = Coordinator(adapter);
    var query = coordinator.Query(Guid.NewGuid().ToString("N"), 41, 1001, "server-instance", epoch);
    Check(query.Code == "operation_not_found", "unknown operation has an explicit result");
}

{
    var adapter = new FakeAdapter(target);
    var coordinator = Coordinator(adapter);
    string? firstOperation = null;
    ExecutionOperationResult? latest = null;
    for (var i = 0; i < 65; i++)
    {
        latest = await coordinator.ExecuteAsync(Request($"retained-{i}"));
        if (i == 0) firstOperation = latest.OperationId;
    }
    Check(firstOperation is not null && coordinator.Query(firstOperation, 41, 1001, "server-instance", epoch).Code == "operation_not_found" &&
        latest?.OperationId is not null && coordinator.Query(latest.OperationId, 41, 1001, "server-instance", epoch).Code == "ok",
        "only the newest 64 completed operations remain queryable");
}

{
    var released = 0;
    var adapter = new FakeAdapter(target)
    {
        DispatchTask = Task.FromException<ExecutionDispatchObservation>(new ExecutionDispatchException("cleanup uncertain", true))
    };
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, acquireWorkspaceLease: () => new FakeLease(() => released++));
    var operation = await coordinator.ExecuteAsync(Request("lease-uncertain"));
    Check(operation.State == "uncertain" && released == 0,
        "uncertain dispatch retains workspace mutation lease");
    coordinator.ReportObservation(operation.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "Dispatch cleanup settled.", true));
    Check(released == 1, "settled observation releases retained mutation lease");
}

{
    var hold = new TaskCompletionSource<ExecutionDispatchObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
    var adapter = new FakeAdapter(target) { DispatchTask = hold.Task };
    var coordinator = Coordinator(adapter);
    var active = Enumerable.Range(0, 8).Select(i => coordinator.ExecuteAsync(Request($"active-{i}"))).ToArray();
    while (adapter.DispatchCount < 8) await Task.Delay(10);
    var overflow = await coordinator.ExecuteAsync(Request("overflow"));
    Check(overflow.Code == "operation_store_full" && overflow.OperationId is null && adapter.DispatchCount == 8,
        "ninth active operation is rejected before dispatch");
    hold.SetResult(new ExecutionDispatchObservation(ExecutionState.Observed, "ok", "completed", true));
    await Task.WhenAll(active);
}

{
    var adapter = new FakeAdapter(target with { LaunchBlockedCode = "unsupported", LaunchBlockedReason = "launch_profile_unidentified" });
    var result = await Coordinator(adapter).ExecuteAsync(Request("blocked-reason"));
    Check(result.Message.Contains("launch_profile_unidentified", StringComparison.Ordinal) && adapter.DispatchCount == 0,
        "blocked launch reports the provider reason without dispatch");
    var stop = await Coordinator(new FakeAdapter(target with { State = "break", LaunchBlockedCode = "unsupported" }))
        .ExecuteAsync(Request("blocked-stop", ExecutionAction.StopDebugging));
    Check(stop.State == "observed", "launch blockers do not block an exact-session stop");
}

// 観測されない accepted / uncertain の操作は実行期限で確定し、workspace lease を解放する（#61 実機不具合の回帰）。
{
    var released = 0;
    var clock = DateTimeOffset.UtcNow;
    var adapter = new FakeAdapter(target)
    {
        DispatchTask = Task.FromResult(new ExecutionDispatchObservation(ExecutionState.Accepted, "launch_accepted", "accepted", true))
    };
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, () => clock, () => new FakeLease(() => released++));
    var accepted = await coordinator.ExecuteAsync(Request("accepted-never-observed"));
    Check(accepted.State == "accepted" && released == 0, "accepted launch keeps the lease before its execution deadline");
    clock = clock.AddSeconds(29);
    Check(coordinator.Query(accepted.OperationId!, 41, 1001, "server-instance", epoch).State == "accepted" && released == 0,
        "accepted launch is not settled before its execution deadline");
    clock = clock.AddSeconds(2);
    var settled = coordinator.Query(accepted.OperationId!, 41, 1001, "server-instance", epoch);
    Check(settled.Code == "result_unknown" && settled.State == "uncertain" && settled.MayHaveChanged && released == 1 && adapter.DispatchCount == 1,
        "unobserved accepted launch settles as result_unknown at the execution deadline and releases the lease");
    var retry = await coordinator.ExecuteAsync(Request("accepted-never-observed"));
    Check(retry.OperationId == accepted.OperationId && adapter.DispatchCount == 1, "a settled operation is never replayed for the same requestId");
    var next = await coordinator.ExecuteAsync(Request("after-deadline"));
    Check(next.OperationId != accepted.OperationId && adapter.DispatchCount == 2, "a new operation can acquire the lease after deadline settlement");
    var late = coordinator.ReportObservation(accepted.OperationId!, 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "debug_session_started", "observed late", true));
    Check(late.State == "observed" && late.Code == "debug_session_started", "a late exact observation still updates the settled record");
}

{
    var released = 0;
    var clock = DateTimeOffset.UtcNow;
    var adapter = new FakeAdapter(target)
    {
        DispatchTask = Task.FromException<ExecutionDispatchObservation>(new ExecutionDispatchException("cleanup uncertain", true))
    };
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, () => clock, () => new FakeLease(() => released++));
    var uncertain = await coordinator.ExecuteAsync(Request("uncertain-deadline"));
    clock = clock.AddSeconds(31);
    var settled = coordinator.Query(uncertain.OperationId!, 41, 1001, "server-instance", epoch);
    Check(uncertain.State == "uncertain" && settled.Code == "result_unknown" && released == 1,
        "uncertain dispatch releases the lease at the execution deadline");
}

{
    var adapter = new FakeAdapter(target)
    {
        DispatchTask = Task.FromResult(new ExecutionDispatchObservation(ExecutionState.Failed, "build_failed", "The pre-launch build failed.", false))
    };
    var released = 0;
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, acquireWorkspaceLease: () => new FakeLease(() => released++));
    var failed = await coordinator.ExecuteAsync(Request("build-failed"));
    Check(failed.State == "failed" && failed.Code == "build_failed" && released == 1,
        "provider-reported build failure settles immediately and releases the lease");
}

{
    // #61: stop は debugger の終了後に process ごとの結果（exited / detached / unknown）で確定し、確定を待つ呼び出し元へ返す。
    var stopTarget = target with { State = "break", SessionAgentOwned = true };
    var adapter = new FakeAdapter(stopTarget)
    {
        Observation = new ExecutionDispatchObservation(ExecutionState.Accepted, "stop_requested", "Stop requested.", true, "debug-session", "process-set")
    };
    var released = 0;
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, acquireWorkspaceLease: () => new FakeLease(() => released++));
    var accepted = await coordinator.ExecuteAsync(Request("stop-outcomes", ExecutionAction.StopDebugging));
    Check(accepted.State == "accepted" && accepted.ProcessOutcomes is null && released == 0,
        "accepted stop carries no per-process claim before the session end is observed");
    var notYet = await coordinator.WaitForSettlementAsync(accepted.OperationId!, TimeSpan.FromMilliseconds(50), CancellationToken.None);
    Check(notYet?.State == "accepted" && notYet.ProcessOutcomes is null, "bounded settlement wait returns the accepted result without cancelling");
    Check(await coordinator.WaitForSettlementAsync("ffffffffffffffffffffffffffffffff", TimeSpan.FromMilliseconds(10), CancellationToken.None) is null,
        "settlement wait never substitutes an unknown operation");
    var waiter = coordinator.WaitForSettlementAsync(accepted.OperationId!, TimeSpan.FromSeconds(10), CancellationToken.None);
    var observed = new ExecutionDispatchObservation(ExecutionState.Observed, "debug_session_ended", "Session ended.", true,
        "debug-session", "process-set",
        [new ExecutionProcessOutcome(10, 5, "exited", -1), new ExecutionProcessOutcome(11, 6, "detached", null),
            new ExecutionProcessOutcome(12, null, "unknown", null)]);
    coordinator.ReportObservation(accepted.OperationId!, 41, 1001, "server-instance", epoch, observed);
    var settled = await waiter;
    Check(settled is { State: "observed", Code: "debug_session_ended", ProcessOutcomes.Count: 3 } && released == 1 &&
        settled.ProcessOutcomes[0] == new ExecutionProcessOutcome(10, 5, "exited", -1) &&
        settled.ProcessOutcomes[1] == new ExecutionProcessOutcome(11, 6, "detached", null) &&
        settled.ProcessOutcomes[2] == new ExecutionProcessOutcome(12, null, "unknown", null),
        "observed stop settles with exact per-process exited/detached/unknown outcomes and releases the lease");
    Check(coordinator.Query(accepted.OperationId!, 41, 1001, "server-instance", epoch).ProcessOutcomes?.Count == 3,
        "operation query returns the same per-process outcomes");
}

{
    // 独立レビュー: 期限で結果不明と確定した記録は、保持期間や完了件数の上限で追い出さず、再 dispatch も起こさない。
    var clock = DateTimeOffset.UtcNow;
    var adapter = new FakeAdapter(target)
    {
        DispatchTask = Task.FromResult(new ExecutionDispatchObservation(ExecutionState.Accepted, "launch_accepted", "accepted", true))
    };
    var coordinator = new ExecutionOperationCoordinator(adapter, () => true, () => clock, () => new FakeLease());
    var first = await coordinator.ExecuteAsync(Request("retained-uncertain"));
    Check(adapter.LastExpectedSolutionPath == solution, "the coordinator passes the expected solution path to the target read");
    clock = clock.AddSeconds(31);
    Check(coordinator.Query(first.OperationId!, 41, 1001, "server-instance", epoch).Code == "result_unknown", "deadline settles the unobserved operation as uncertain");
    clock = clock.AddMinutes(10);
    var after = coordinator.Query(first.OperationId!, 41, 1001, "server-instance", epoch);
    var repeat = await coordinator.ExecuteAsync(Request("retained-uncertain"));
    Check(after.Code == "result_unknown" && repeat.OperationId == first.OperationId && adapter.DispatchCount == 1,
        "an uncertain operation survives the retention period and its requestId is never dispatched again");
    var okAdapter = new FakeAdapter(target);
    var busy = new ExecutionOperationCoordinator(okAdapter, () => true, () => clock, () => new FakeLease());
    var keep = new FakeAdapter(target)
    {
        DispatchTask = Task.FromResult(new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown", "uncertain", true))
    };
    var pile = new ExecutionOperationCoordinator(keep, () => true, () => clock, () => new FakeLease());
    var ids = new List<string>();
    for (var index = 0; index < 8; index++)
    {
        var made = await pile.ExecuteAsync(Request("pile-" + index));
        ids.Add(made.OperationId!);
        clock = clock.AddSeconds(31);
        pile.Query(made.OperationId!, 41, 1001, "server-instance", epoch);
    }
    clock = clock.AddMinutes(10);
    var ninth = await pile.ExecuteAsync(Request("pile-8"));
    Check(ninth.Code == "operation_store_full" && keep.DispatchCount == 8 &&
        ids.All(id => pile.Query(id, 41, 1001, "server-instance", epoch).Code == "result_unknown"),
        "eight retained uncertain operations occupy the bounded slots, stay queryable and block a ninth dispatch");
    var exact = pile.ReportObservation(ids[0], 41, 1001, "server-instance", epoch,
        new ExecutionDispatchObservation(ExecutionState.Observed, "debug_session_ended", "observed", true));
    var tenth = await pile.ExecuteAsync(Request("pile-9"));
    Check(exact.State == "observed" && tenth.Code != "operation_store_full", "an exact late observation frees a retained uncertain slot");
}

Console.WriteLine($"{checks} execution checks passed.");

sealed class FakeAdapter(ExecutionTargetSnapshot snapshot) : IExecutionAdapter
{
    public int SnapshotCount { get; private set; }
    public string? LastExpectedSolutionPath { get; private set; }
    public int DispatchCount { get; private set; }
    public ExecutionDispatchObservation Observation { get; init; } = new(ExecutionState.Observed, "ok", "Observed.", true);
    public Task<ExecutionTargetSnapshot>? SnapshotTask { get; init; }
    public Task<ExecutionDispatchObservation>? DispatchTask { get; init; }

    public Task<ExecutionTargetSnapshot> ReadTargetSnapshotAsync(string expectedSolutionPath, TimeSpan remaining, CancellationToken cancellationToken)
    {
        SnapshotCount++;
        LastExpectedSolutionPath = expectedSolutionPath;
        return SnapshotTask ?? Task.FromResult(snapshot);
    }

    public Task<ExecutionDispatchObservation> DispatchAsync(ExecutionRequest request, string operationId, TimeSpan remaining, CancellationToken cancellationToken)
    {
        DispatchCount++;
        return DispatchTask ?? Task.FromResult(Observation);
    }
}

sealed class FakeLease(Action? onDispose = null) : IExecutionMutationLease
{
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose?.Invoke();
    }
}
