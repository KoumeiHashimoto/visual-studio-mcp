using System.Text.Json;
using VisualStudioMcpServer;

using VisualStudioMcpServer.Workspace;

var validTarget = new BuildTargetSnapshot(41, 1001, 41, 1001, true, @"C:\repo\app.sln", "Debug", "Any CPU", true, false, false);
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
    checks++;
}

{
    var ownership = new BuildEventOwnership();
    var now = DateTimeOffset.UtcNow;
    Check(!ownership.OnBuildBegin(false, true, now), "manual build begin before dispatch is foreign");
    ownership.BeginDispatch(1000, now);
    Check(ownership.OnBuildDone(true, true, now), "fast done without a begin is owned only inside dispatch");
    Check(!ownership.OnBuildDone(false, true, now), "late done without an owned begin is not adopted");
    ownership.BeginDispatch(1000, now);
    Check(ownership.OnBuildBegin(true, true, now) && ownership.OnBuildDone(false, true, now), "ordinary begin and delayed done preserve ownership");
    ownership.BeginDispatch(1000, now);
    Check(ownership.OnBuildBegin(false, true, now.AddMilliseconds(250)), "matching begin after dispatch returns is correlated within its window");
    Check(ownership.OnBuildDone(false, true, now.AddMilliseconds(500)), "delayed done preserves asynchronously correlated ownership");
    ownership.BeginDispatch(1000, now);
    Check(!ownership.OnBuildBegin(false, false, now.AddMilliseconds(1)), "mismatched action is not adopted");
    Check(!ownership.OnBuildDone(false, true, now.AddMilliseconds(2)), "mismatched event clears pending correlation");
    ownership.BeginDispatch(1000, now);
    Check(!ownership.OnBuildBegin(false, true, now.AddMilliseconds(1001)), "expired correlation window does not adopt a manual build");
}

{
    var workspace = new ActiveWorkspaceSnapshot(true, 41, 1001, 1, @"C:\repo\folder", "openFolder",
        "unsupported", "CMake metadata is not exposed by a documented public API.");
    var dispatchCount = 0;
    var unsupported = OpenFolderBuildPolicy.Check(workspace, 41, 1001, "build", true, @"C:\repo\folder", 10000);
    if (unsupported is null) dispatchCount++;
    Check(unsupported is { Code: "unsupported", WorkspaceRoot: @"C:\repo\folder" } && dispatchCount == 0,
        "Open Folder without a public action returns unsupported before dispatch");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, "rebuild", false, @"C:\repo\folder", 10000)?.Code == "build_policy_required",
        "Open Folder rebuild still requires explicit build permission");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, "clean", true, null, 10000)?.Code == "expected_workspace_required",
        "Open Folder clean requires an expected workspace root");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, "build", true, @"C:\repo\other", 10000)?.Code == "workspace_mismatch",
        "Open Folder build rejects a different workspace root");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, "build", true, "relative", 10000)?.Code == "invalid_argument",
        "Open Folder build rejects a relative workspace root");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, null, true, null, 10000)?.Code == "expected_workspace_required",
        "Open Folder status requires an expected workspace root");
    Check(OpenFolderBuildPolicy.Check(workspace, 41, 1001, null, true, @"C:\repo\folder", 10000) is { Code: "unsupported", Action: null },
        "Open Folder status reports unsupported without claiming an operation");
    Check(OpenFolderBuildPolicy.Check(workspace, 42, 1001, "build", true, @"C:\repo\folder", 10000) is null,
        "Open Folder metadata cannot bind to a different Visual Studio process");
}

var guards = new (string Code, BuildTargetSnapshot Target, string? ExpectedPath, int? Pid, long? Ticks, bool Allowed)[]
{
    ("build_policy_required", validTarget, validTarget.SolutionPath, 41, 1001, false),
    ("expected_solution_required", validTarget, "", 41, 1001, true),
    ("invalid_argument", validTarget, "relative.sln", 41, 1001, true),
    ("target_identity_required", validTarget, validTarget.SolutionPath, null, 1001, true),
    ("stale_target", validTarget with { TargetProcessStartUtcTicks = 1002 }, validTarget.SolutionPath, 41, 1001, true),
    ("target_not_registered", validTarget with { RegisteredProcessId = 42 }, validTarget.SolutionPath, 41, 1001, true),
    ("dte_disconnected", validTarget with { DteConnected = false }, validTarget.SolutionPath, 41, 1001, true),
    ("no_solution", validTarget with { SolutionPath = null }, validTarget.SolutionPath, 41, 1001, true),
    ("solution_mismatch", validTarget, @"C:\repo\other.sln", 41, 1001, true),
    ("invalid_debugger_state", validTarget with { IsDesignMode = false }, validTarget.SolutionPath, 41, 1001, true),
    ("build_busy", validTarget with { IsBuildInProgress = true }, validTarget.SolutionPath, 41, 1001, true),
    ("build_busy", validTarget with { HasForeignOrUncertainBuild = true }, validTarget.SolutionPath, 41, 1001, true),
    ("invalid_argument", validTarget with { Configuration = null }, validTarget.SolutionPath, 41, 1001, true)
};
foreach (var guard in guards)
{
    var adapter = new FakeAdapter(guard.Target);
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Build, guard.ExpectedPath, guard.Pid, guard.Ticks, guard.Allowed);
    Check(result.Code == guard.Code && adapter.DispatchCount == 0, $"guard {guard.Code} dispatches nothing");
}

{
    var adapter = new FakeAdapter(validTarget) { BeginOnSubscribe = true };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true);
    Check(result.Code == "build_busy" && adapter.DispatchCount == 0 && adapter.DetachCount == 1, "pre-dispatch build event is foreign and action is not dispatched");
}

foreach (var action in Enum.GetValues<BuildAction>())
{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(action, validTarget.SolutionPath, 41, 1001, true);
    Check(adapter.DispatchCount == 1 && adapter.LastAction == action, $"{action} dispatches exactly once");
    Check(adapter.SubscribedBeforeDispatch, $"{action} subscribes before dispatch");
    Check(result.State == "completed" && result.Outcome == "succeeded", $"fast {action} completion retained");
    Check(adapter.DetachCount == 1, $"{action} subscription detached");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, EventSolutionPath = @"C:\other\other.sln" };
    var coordinator = new BuildOperationCoordinator(adapter);
    var result = await coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(result.Code == "result_unknown" && result.Outcome == "unknown", "foreign completion cannot complete owned operation");
    Check(coordinator.GetStatus(result.OperationId).Code == "result_unknown", "uncertain operation remains visible");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, BeginDuringDispatch = false };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true);
    Check(result.Outcome == "succeeded" && result.State == "completed", "fast completion without a begin event is retained");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, FailedCount = null };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true);
    Check(result.BuildSucceeded is null && result.Outcome == "succeeded" && result.FailedProjectCount is null, "clean completion does not reuse stale build failure counts");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, FailedCount = null, DoneStillInProgress = true };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true);
    Check(result.BuildSucceeded is null && result.Outcome == "unknown", "clean with an uncertain terminal state remains unknown");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, TerminalState = false };
    var coordinator = new BuildOperationCoordinator(adapter);
    var result = await coordinator.ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(result.Code == "build_wait_timeout" && result.Outcome is null, "nonterminal Done snapshot cannot claim clean success");
    Check(coordinator.GetStatus(result.OperationId).State == "unknown", "nonterminal Done snapshot preserves unknown status");
}

{
    var adapter = new FakeAdapter(validTarget) { CompleteDuringDispatch = true, HasBuildResult = false };
    var result = await new BuildOperationCoordinator(adapter).ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true);
    Check(result.Code == "result_unknown" && result.Outcome == "unknown", "NotStarted cannot reuse LastBuildInfo or claim clean success");
}

{
    var gate = new WorkspaceMutationGate();
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    var first = await coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(!first.Success && first.Code == "build_wait_timeout" && first.State == "starting", "timeout preserves the last observed pending state");
    Check(coordinator.GetStatus(first.OperationId).Code == "build_wait_timeout", "status reflects caller wait timeout while operation remains active");
    var blocked = await coordinator.ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(blocked.Code == "build_busy", "concurrent workspace mutation is rejected");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(coordinator.GetStatus(first.OperationId).Outcome == "succeeded", "late completion releases and records operation");
    Check(gate.TryAcquire(), "gate released only after terminal event");
    gate.Release();
}

{
    var gate = new WorkspaceMutationGate();
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    var initial = await coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(initial.Code == "build_wait_timeout" && initial.State == "starting", "delayed build event starts from pending status");
    adapter.RaiseBegin(new BuildEventSnapshot(validTarget.SolutionPath, true, false, null, true));
    Check(coordinator.GetStatus(initial.OperationId).State == "running", "owned begin delivered after dispatch is accepted");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(coordinator.GetStatus(initial.OperationId).Outcome == "succeeded", "delayed begin and completion finish the correlated operation");
    Check(gate.TryAcquire(), "delayed correlated completion releases the gate");
    gate.Release();
}

{
    var gate = new WorkspaceMutationGate();
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    using var cts = new CancellationTokenSource();
    var operation = coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 5000, cancellationToken: cts.Token);
    await adapter.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    var cancelled = await operation;
    Check(cancelled.Code == "cancelled" && !cancelled.Success, "caller cancellation stops only the wait");
    Check(!gate.TryAcquire(), "cancellation after dispatch retains the workspace gate");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(coordinator.GetStatus(cancelled.OperationId).Outcome == "succeeded", "operation still completes after caller cancellation");
    Check(gate.TryAcquire(), "terminal event releases the gate after caller cancellation");
    gate.Release();
}

foreach (var action in Enum.GetValues<BuildAction>())
{
    var gate = new WorkspaceMutationGate();
    Check(gate.TryAcquire(), $"project registration acquires shared gate before {action}");
    var adapter = new FakeAdapter(validTarget);
    var result = await new BuildOperationCoordinator(adapter, gate).ExecuteAsync(
        action, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(result.Code == "build_busy" && adapter.DispatchCount == 0,
        $"{action} is blocked while a project registration holds the shared gate");
    gate.Release();
}

foreach (var action in Enum.GetValues<BuildAction>())
{
    var gate = new WorkspaceMutationGate();
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    var pending = await coordinator.ExecuteAsync(action, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(pending.Code == "build_wait_timeout" && !gate.TryAcquire(),
        $"{action} keeps project mutations blocked while build outcome is pending");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(gate.TryAcquire(), $"{action} releases project mutations after correlated completion");
    gate.Release();
}

{
    var gate = new WorkspaceMutationGate();
    var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Check(gate.TryAcquire(), "project mutation acquires the gate before uncertain cleanup");
    var release = gate.ReleaseAfter(cleanup.Task);
    Check(!gate.TryAcquire(), "uncertain project mutation retains the gate until queued cleanup settles");
    cleanup.SetResult();
    await release.WaitAsync(TimeSpan.FromSeconds(1));
    Check(gate.TryAcquire(), "successful queued cleanup releases the project mutation gate");
    gate.Release();
}

{
    var gate = new WorkspaceMutationGate();
    var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Check(gate.TryAcquire(), "project mutation acquires the gate before failed cleanup");
    var release = gate.ReleaseAfter(cleanup.Task);
    cleanup.SetException(new InvalidOperationException("cleanup marker failed"));
    await release.WaitAsync(TimeSpan.FromSeconds(1));
    Check(!gate.TryAcquire(), "failed cleanup keeps the shared gate closed fail-safe");
}

{
    var coordinator = new BuildOperationCoordinator(new FakeAdapter(validTarget) { CompleteDuringDispatch = true });
    var ids = new List<string>();
    for (var i = 0; i < 35; i++)
    {
        var result = await coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true);
        ids.Add(result.OperationId!);
    }
    Check(coordinator.GetStatus(ids[0]).Code == "operation_not_found", "retains only the newest 32 terminal snapshots");
    Check(coordinator.GetStatus(ids[^1]).Outcome == "succeeded", "latest snapshot is queryable");
    var json = BuildOperationResult.ToBoundedJson(coordinator.GetStatus(ids[^1]));
    Check(JsonDocument.Parse(json).RootElement.GetProperty("OperationId").GetString() == ids[^1], "response is bounded valid JSON");
}

Console.WriteLine($"Passed {checks} BuildValidation checks.");

sealed class FakeAdapter(BuildTargetSnapshot snapshot) : IBuildOperationAdapter
{
    private Action<BuildEventSnapshot>? _begin;
    private Action<BuildEventSnapshot>? _done;
    public bool CompleteDuringDispatch { get; init; }
    public int? FailedCount { get; init; } = 0;
    public bool DoneStillInProgress { get; init; }
    public bool TerminalState { get; init; } = true;
    public bool HasBuildResult { get; init; } = true;
    public bool BeginOnSubscribe { get; init; }
    public bool BeginDuringDispatch { get; init; } = true;
    public string? EventSolutionPath { get; init; }
    public int DispatchCount { get; private set; }
    public int DetachCount { get; private set; }
    public BuildAction? LastAction { get; private set; }
    public bool SubscribedBeforeDispatch { get; private set; }
    public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BuildTargetSnapshot ReadTargetSnapshot() => snapshot;
    public IDisposable SubscribeBuildEvents(Action<BuildEventSnapshot> onBegin, Action<BuildEventSnapshot> onDone)
    {
        _begin = onBegin;
        _done = onDone;
        if (BeginOnSubscribe) _begin(new BuildEventSnapshot(snapshot.SolutionPath, true, false, null, false));
        return new Detach(this);
    }
    public void Dispatch(BuildAction action, int correlationWindowMilliseconds)
    {
        DispatchCount++;
        SubscribedBeforeDispatch = _begin is not null && _done is not null;
        Dispatched.TrySetResult();
        LastAction = action;
        if (!CompleteDuringDispatch) return;
        var path = EventSolutionPath ?? snapshot.SolutionPath;
        if (BeginDuringDispatch) _begin?.Invoke(new BuildEventSnapshot(path, true, false, null, true));
        _done?.Invoke(new BuildEventSnapshot(path, DoneStillInProgress, false, FailedCount, true) { IsTerminalState = TerminalState, HasBuildResult = HasBuildResult });
    }
    public void DisposeSubscription(IDisposable subscription) => subscription.Dispose();
    public void RaiseBegin(BuildEventSnapshot evt) => _begin?.Invoke(evt);
    public void RaiseDone(BuildEventSnapshot evt) => _done?.Invoke(evt);
    private sealed class Detach(FakeAdapter adapter) : IDisposable
    {
        public void Dispose() { adapter.DetachCount++; adapter._begin = null; adapter._done = null; }
    }
}
