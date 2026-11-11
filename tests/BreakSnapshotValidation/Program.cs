using VisualStudioMcpServer.Observation;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Tools;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

var now = DateTimeOffset.UtcNow;
var epoch = Guid.NewGuid().ToString("N");
var session = Guid.NewGuid().ToString("N");
DebuggerObservationStore NewStore() => new(epoch, () => now, static (_, _) => Task.CompletedTask);

DebuggerInspectionResponse Stack(int frameCount = 20, int thread = 7, string function = "App.Main") => new()
{
    Success = true, Code = "ok", DebuggerMode = "dbgBreakMode", TargetProcessId = 11, CurrentThreadId = thread,
    TotalCount = frameCount,
    Frames = Enumerable.Range(1, Math.Min(frameCount, 128)).Select(i => new DebuggerFrameInfo { Index = i, Function = i == 1 ? function : "F" + i, Module = "app.dll" }).ToList()
};
DebuggerInspectionResponse Locals(int count = 3) => new()
{
    Success = true, Code = "ok", TotalCount = count,
    Locals = Enumerable.Range(1, count).Select(i => new DebuggerVariableInfo { Name = "v" + i, Type = "int", Value = i.ToString() }).ToList()
};
var noDelay = (TimeSpan _) => Task.CompletedTask;

// ---- 世代の照合 ----
var store = NewStore();
store.Publish(session, null, DebuggerObservedState.Running, 1);
var breakObservation = store.Publish(session, null, DebuggerObservedState.Break, 2, "breakpoint");
Check(BreakGeneration.Guard(null, store) is null && BreakGeneration.Guard(null, null) is null, "an omitted expectedEventSequence is never checked");
Check(BreakGeneration.Guard(breakObservation.Sequence, store) is null, "the current generation is accepted");
Check(BreakGeneration.Guard(breakObservation.Sequence - 1, store) is { Code: "stale_break_generation" }, "an old generation is refused as stale");
store.Publish(session, null, DebuggerObservedState.Break, 3, "step");
Check(BreakGeneration.Guard(breakObservation.Sequence, store) is { Code: "stale_break_generation" }, "a step makes the previous generation stale");
Check(BreakGeneration.Guard(5, null) is { Code: "generation_unavailable" }, "a supplied generation without a provider is unavailable");
var closed = NewStore();
closed.Close();
Check(BreakGeneration.Guard(0, closed) is { Code: "generation_unavailable" }, "a closed provider channel is unavailable");

// ---- 現在の世代（break の記録待ち）----
var settling = NewStore();
settling.Publish(session, null, DebuggerObservedState.Running, 1);
var delays = 0;
var (seq, obs) = BreakGeneration.Current(settling, settle: true, _ =>
{
    // 3 回待った後に break の通知が届く。
    if (++delays == 3) settling.Publish(session, null, DebuggerObservedState.Break, 2, "user_break");
    return Task.CompletedTask;
});
Check(delays == 3 && obs?.StopReason == "user_break" && seq == settling.CurrentSequence, "the generation waits for the break observation to be recorded");
var neverBreaks = NewStore();
neverBreaks.Publish(session, null, DebuggerObservedState.Running, 1);
var waited = 0;
var (seq2, obs2) = BreakGeneration.Current(neverBreaks, true, _ => { waited++; return Task.CompletedTask; });
Check(obs2 is null && seq2 == 1 && waited == (int)(BreakGeneration.SettleWait.TotalMilliseconds / 25), "the wait is bounded and falls back to the latest sequence");
Check(BreakGeneration.Current(null, true).Sequence is null, "no store means no generation");

// ---- snapshot ----
var exception = new ExceptionInfoBody { Kind = "unhandled", Category = "Managed", TypeName = "T", Code = 5, Description = "secret text" };
var exceptionStore = NewStore();
exceptionStore.Publish(session, null, DebuggerObservedState.Running, 1);
exceptionStore.Publish(session, null, DebuggerObservedState.Break, 2, "exception_unhandled", exception);
var exceptionSnapshot = BreakGeneration.Build(16, true, 40, false, exceptionStore, () => Stack(), (_, _, _, _) => Locals(), noDelay);
Check(exceptionSnapshot is { Success: true, Code: "ok", StopReason: "exception_unhandled", EventSequence: 2, GenerationAvailability: "available", DebugSessionId: var sid } &&
    sid == session && exceptionSnapshot.Exception is { Description: null, DescriptionAvailability: "requires_allowSensitiveRead", TypeName: "T" },
    "the snapshot carries generation, session, stop reason and a withheld exception description");
Check(!System.Text.Json.JsonSerializer.Serialize(exceptionSnapshot).Contains("secret text"), "the snapshot never contains the description without allowSensitiveRead");
var shown = BreakGeneration.Build(16, true, 40, true, exceptionStore, () => Stack(), (_, _, _, _) => Locals(), noDelay);
Check(shown.Exception?.Description == "secret text", "allowSensitiveRead returns the bounded description");
Check(exceptionSnapshot.Frames!.Count == 16 && exceptionSnapshot.TotalFrameCount == 20 && exceptionSnapshot.Truncated, "frames are limited to 16 and marked truncated");
Check(exceptionSnapshot.Locals!.Count == 3 && exceptionSnapshot.LocalsAvailability == "available", "locals of frame 1 are included");
Check(BreakGeneration.Build(100, true, 40, false, exceptionStore, () => Stack(), (_, _, _, _) => Locals(), noDelay).Frames!.Count == 16, "maxFrames above 16 is clamped");
Check(BreakGeneration.Build(0, true, 40, false, exceptionStore, () => Stack(), (_, _, _, _) => Locals(), noDelay).Frames!.Count == 1, "maxFrames below 1 is clamped to 1");
var localsRequests = new List<int>();
BreakGeneration.Build(16, true, 99, false, exceptionStore, () => Stack(), (_, _, _, max) => { localsRequests.Add(max); return Locals(); }, noDelay);
Check(localsRequests.SequenceEqual(new[] { 40 }), "maxLocals above 40 is clamped");
var noLocals = BreakGeneration.Build(16, false, 40, false, exceptionStore, () => Stack(), (_, _, _, _) => throw new InvalidOperationException("locals must not be read"), noDelay);
Check(noLocals.Locals is null && noLocals.LocalsAvailability == "not_requested", "includeLocals=false reads no locals");
var zeroLocals = BreakGeneration.Build(16, true, 0, false, exceptionStore, () => Stack(), (_, _, _, _) => throw new InvalidOperationException("locals must not be read"), noDelay);
Check(zeroLocals.Locals is null, "maxLocals=0 reads no locals");
var unnamed = BreakGeneration.Build(16, true, 40, false, exceptionStore, () => Stack(function: ""), (_, _, _, _) => throw new InvalidOperationException("locals must not be read"), noDelay);
Check(unnamed.Success && unnamed.Locals is null && unnamed.LocalsAvailability == "unavailable", "a frame without a function name has no locals but the snapshot succeeds");
DebuggerFrameInfo? requestedFrame = null;
int requestedThread = 0;
BreakGeneration.Build(16, true, 40, false, exceptionStore, () => Stack(thread: 42), (frame, _, thread, _) => { requestedFrame = frame; requestedThread = thread; return Locals(); }, noDelay);
Check(requestedFrame?.Index == 1 && requestedThread == 42, "locals are read for frame 1 on the current thread");

// 読み取り中に世代が進む。
var moving = NewStore();
moving.Publish(session, null, DebuggerObservedState.Running, 1);
moving.Publish(session, null, DebuggerObservedState.Break, 2, "breakpoint");
var changed = BreakGeneration.Build(16, true, 40, false, moving, () => { moving.Publish(session, null, DebuggerObservedState.Running, 3); return Stack(); },
    (_, _, _, _) => Locals(), noDelay);
Check(changed is { Success: true, Changed: true, Partial: true, Frames: null, Locals: null, EventSequence: 2 }, "a generation change during the read returns no frames or locals");

// provider なし。
var withoutProvider = BreakGeneration.Build(16, true, 40, false, null, () => Stack(), (_, _, _, _) => Locals(), noDelay);
Check(withoutProvider is { Success: true, GenerationAvailability: "unavailable", EventSequence: null, StopReason: null, Changed: false } && withoutProvider.Frames!.Count == 16,
    "without a provider the snapshot still reads frames and locals and reports no generation");

// break ではない。
var notPaused = BreakGeneration.Build(16, true, 40, false, exceptionStore,
    () => new DebuggerInspectionResponse { Success = false, Code = "not_paused", Message = "The debugger is not paused.", DebuggerMode = "dbgRunMode" },
    (_, _, _, _) => Locals(), noDelay);
Check(notPaused is { Success: false, Code: "not_paused" } && notPaused.Frames is null, "a debugger that is not paused returns not_paused");

// 世代の状態が break を記録していなくても IDE が break なら理由なしで返す。
var late = NewStore();
late.Publish(session, null, DebuggerObservedState.Running, 1);
var lateSnapshot = BreakGeneration.Build(16, true, 40, false, late, () => Stack(), (_, _, _, _) => Locals(), noDelay);
Check(lateSnapshot.Success && lateSnapshot.StopReason is null && lateSnapshot.EventSequence == 1, "an unrecorded break is returned without a stop reason");

Console.WriteLine($"{passed} break snapshot checks passed.");
