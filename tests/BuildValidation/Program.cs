using System.Text.Json;
using VisualStudioMcpServer;

var validTarget = new BuildTargetSnapshot(41, 1001, 41, 1001, true, @"C:\repo\app.sln", "Debug", "Any CPU", true, false, false);
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
    checks++;
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
    Check(result.Code == "build_wait_timeout" && result.Outcome is null, "foreign completion cannot complete owned operation");
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
    var gate = new SemaphoreSlim(1, 1);
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    var first = await coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(!first.Success && first.Code == "build_wait_timeout" && first.State == "unknown", "timeout leaves operation pending");
    Check(coordinator.GetStatus(first.OperationId).Code == "build_wait_timeout", "status reflects caller wait timeout while operation remains active");
    var blocked = await coordinator.ExecuteAsync(BuildAction.Clean, validTarget.SolutionPath, 41, 1001, true, 1000);
    Check(blocked.Code == "build_busy", "concurrent workspace mutation is rejected");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(coordinator.GetStatus(first.OperationId).Outcome == "succeeded", "late completion releases and records operation");
    Check(gate.Wait(0), "gate released only after terminal event");
    gate.Release();
}

{
    var gate = new SemaphoreSlim(1, 1);
    var adapter = new FakeAdapter(validTarget);
    var coordinator = new BuildOperationCoordinator(adapter, gate);
    using var cts = new CancellationTokenSource();
    var operation = coordinator.ExecuteAsync(BuildAction.Build, validTarget.SolutionPath, 41, 1001, true, 5000, cancellationToken: cts.Token);
    await adapter.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
    cts.Cancel();
    var cancelled = await operation;
    Check(cancelled.Code == "cancelled" && !cancelled.Success, "caller cancellation stops only the wait");
    Check(!gate.Wait(0), "cancellation after dispatch retains the workspace gate");
    adapter.RaiseDone(new BuildEventSnapshot(validTarget.SolutionPath, false, false, 0, true));
    Check(coordinator.GetStatus(cancelled.OperationId).Outcome == "succeeded", "operation still completes after caller cancellation");
    Check(gate.Wait(0), "terminal event releases the gate after caller cancellation");
    gate.Release();
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
    public void Dispatch(BuildAction action)
    {
        DispatchCount++;
        SubscribedBeforeDispatch = _begin is not null && _done is not null;
        Dispatched.TrySetResult();
        LastAction = action;
        if (!CompleteDuringDispatch) return;
        var path = EventSolutionPath ?? snapshot.SolutionPath;
        if (BeginDuringDispatch) _begin?.Invoke(new BuildEventSnapshot(path, true, false, null, true));
        _done?.Invoke(new BuildEventSnapshot(path, DoneStillInProgress, false, FailedCount, true));
    }
    public void DisposeSubscription(IDisposable subscription) => subscription.Dispose();
    public void RaiseDone(BuildEventSnapshot evt) => _done?.Invoke(evt);
    private sealed class Detach(FakeAdapter adapter) : IDisposable
    {
        public void Dispose() { adapter.DetachCount++; adapter._begin = null; adapter._done = null; }
    }
}
