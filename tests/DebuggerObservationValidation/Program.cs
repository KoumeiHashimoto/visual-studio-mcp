using VisualStudioMcpServer.Observation;

var passed = 0;
var now = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
var epoch = Guid.NewGuid().ToString("N");
var sessionA = Guid.NewGuid().ToString("N");
var sessionB = Guid.NewGuid().ToString("N");
var operation = Guid.NewGuid().ToString("N");
var store = new DebuggerObservationStore(epoch, () => now,
    static (_, _) => Task.CompletedTask);

var run = store.Publish(sessionA, null, DebuggerObservedState.Running);
var stop = store.Publish(sessionA, null, DebuggerObservedState.Break);
var exit = store.Publish(sessionA, null, DebuggerObservedState.Design);
Check(run.Sequence == 1 && stop.Sequence == 2 && exit.Sequence == 3, "sequence is monotonic within provider epoch");
var fastWait = await store.WaitAsync(DebuggerWaitDesiredState.Break, run.Sequence, sessionA, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(fastWait.Status == DebuggerObservationWaitStatus.Matched && fastWait.Observation == stop,
    "rapid run-break-design history preserves fresh break event");
var anyChange = await store.WaitAsync(DebuggerWaitDesiredState.AnyChange, run.Sequence, sessionA, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(anyChange.Status == DebuggerObservationWaitStatus.Matched && anyChange.Observation == stop,
    "any-change wait returns the first fresh transition");
var staleBreak = await store.WaitAsync(DebuggerWaitDesiredState.Break, stop.Sequence, sessionA, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(staleBreak.Status == DebuggerObservationWaitStatus.TimedOut,
    "existing break does not satisfy a fresh wait after its sequence");
var ended = await store.WaitAsync(DebuggerWaitDesiredState.Design, stop.Sequence, sessionA, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(ended.Status == DebuggerObservationWaitStatus.Matched && ended.Observation == exit,
    "terminal design event satisfies wait for the same session");

store.Publish(sessionB, null, DebuggerObservedState.Running);
var replaced = await store.WaitAsync(DebuggerWaitDesiredState.Break, exit.Sequence, sessionA, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(replaced.Status == DebuggerObservationWaitStatus.SessionChanged,
    "replacement session cannot satisfy an old session wait");

var launchStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now,
    static (_, _) => Task.Delay(Timeout.InfiniteTimeSpan));
launchStore.Publish(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), DebuggerObservedState.Running);
launchStore.Publish(Guid.NewGuid().ToString("N"), operation, DebuggerObservedState.Running);
var launched = await launchStore.WaitAsync(DebuggerWaitDesiredState.Running, 0, null, operation,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(launched.Status == DebuggerObservationWaitStatus.Matched && launched.Observation!.LaunchOperationId == operation,
    "launch wait binds to the exact accepted operation");

var bounded = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now);
for (var index = 0; index < DebuggerObservationStore.MaximumHistoryEntries + 1; index++)
    bounded.Publish(Guid.NewGuid().ToString("N"), null, DebuggerObservedState.Running);
Check(bounded.Snapshot().Count == DebuggerObservationStore.MaximumHistoryEntries,
    "history is bounded to 256 observations");
var gap = await bounded.WaitAsync(DebuggerWaitDesiredState.Break, 0, Guid.NewGuid().ToString("N"), null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(gap.Status == DebuggerObservationWaitStatus.EventHistoryExpired && gap.OldestAvailableSequence == 2,
    "overwritten history reports a gap instead of claiming no event");
now += DebuggerObservationStore.HistoryLifetime + TimeSpan.FromTicks(1);
var expired = await bounded.WaitAsync(DebuggerWaitDesiredState.Break, bounded.CurrentSequence - 1,
    Guid.NewGuid().ToString("N"), null, TimeSpan.FromSeconds(1), CancellationToken.None);
Check(expired.Status == DebuggerObservationWaitStatus.EventHistoryExpired,
    "expired history is explicit after the TTL");

var waitStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now,
    static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
var expectedSession = Guid.NewGuid().ToString("N");
var pending = Enumerable.Range(0, DebuggerObservationStore.MaximumWaiters)
    .Select(_ => waitStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, expectedSession, null,
        TimeSpan.FromSeconds(5), CancellationToken.None)).ToArray();
await Task.Yield();
var overflow = await waitStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, expectedSession, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(overflow.Status == DebuggerObservationWaitStatus.WaiterLimit, "ninth concurrent waiter is rejected");
waitStore.Publish(expectedSession, null, DebuggerObservedState.Break);
var completed = await Task.WhenAll(pending);
Check(completed.All(result => result.Status == DebuggerObservationWaitStatus.Matched),
    "all registered waiters observe the same matching event");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
var cancelledWait = await waitStore.WaitAsync(DebuggerWaitDesiredState.Design, waitStore.CurrentSequence,
    expectedSession, null, TimeSpan.FromSeconds(1), cancelled.Token);
Check(cancelledWait.Status == DebuggerObservationWaitStatus.Cancelled, "caller cancellation is distinct from observation timeout");
var timedOutStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now,
    static (_, _) => Task.CompletedTask);
var timedOut = await timedOutStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, Guid.NewGuid().ToString("N"), null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(timedOut.Status == DebuggerObservationWaitStatus.TimedOut, "observation timeout is reported without a transition");

var cursor = await waitStore.WaitAsync(DebuggerWaitDesiredState.Break, waitStore.CurrentSequence + 1,
    expectedSession, null, TimeSpan.FromSeconds(1), CancellationToken.None);
Check(cursor.Status == DebuggerObservationWaitStatus.InvalidCursor, "future sequence cursor is rejected");
Throws<ArgumentException>(() => new DebuggerObservationStore("not-a-uuid"), "invalid provider epoch rejected");
Throws<ArgumentException>(() => waitStore.Publish("not-a-uuid", null, DebuggerObservedState.Break), "invalid session identity rejected");
await ThrowsAsync<ArgumentException>(() => waitStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, expectedSession, operation,
    TimeSpan.FromSeconds(1), CancellationToken.None), "ambiguous session and launch binding rejected");

// ---- provider の event 欠落（gap）・channel 終端・provider sequence の対応 ----
var gapSession = Guid.NewGuid().ToString("N");
var gapStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now,
    static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
var gapRun = gapStore.Publish(gapSession, null, DebuggerObservedState.Running, 1);
var spanning = gapStore.WaitAsync(DebuggerWaitDesiredState.Break, gapRun.Sequence, gapSession, null,
    TimeSpan.FromSeconds(5), CancellationToken.None);
await Task.Yield();
var gapSequence = gapStore.MarkHistoryGap();
var spanningResult = await spanning.WaitAsync(TimeSpan.FromSeconds(5));
Check(spanningResult.Status == DebuggerObservationWaitStatus.EventHistoryExpired && gapSequence == gapRun.Sequence + 1 &&
    spanningResult.OldestAvailableSequence == gapSequence + 1, "registered waiter spanning a provider gap gets event_history_expired");
var gapBreak = gapStore.Publish(gapSession, null, DebuggerObservedState.Break, 5);
var acrossGap = await gapStore.WaitAsync(DebuggerWaitDesiredState.Break, gapRun.Sequence, gapSession, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(acrossGap.Status == DebuggerObservationWaitStatus.EventHistoryExpired,
    "cursor before a gap is expired even when a later event matches");
var afterGap = await gapStore.WaitAsync(DebuggerWaitDesiredState.Break, gapSequence, gapSession, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(afterGap.Status == DebuggerObservationWaitStatus.Matched && afterGap.Observation == gapBreak,
    "fresh cursor at the gap marker matches later events");
Check(gapStore.TryGetSequenceForProviderSequence(5, out var mapped) && mapped == gapBreak.Sequence && gapStore.LastProviderSequence == 5,
    "provider sequence maps to the public sequence of the same observation");
Check(!gapStore.TryGetSequenceForProviderSequence(3, out _), "a provider sequence lost in a gap is not mapped");
var beforeGapStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now, static (_, _) => Task.CompletedTask);
var beforeRun = beforeGapStore.Publish(gapSession, null, DebuggerObservedState.Running, 1);
var beforeBreak = beforeGapStore.Publish(gapSession, null, DebuggerObservedState.Break, 2);
beforeGapStore.MarkHistoryGap();
var beforeGap = await beforeGapStore.WaitAsync(DebuggerWaitDesiredState.Break, beforeRun.Sequence, gapSession, null,
    TimeSpan.FromSeconds(1), CancellationToken.None);
Check(beforeGap.Status == DebuggerObservationWaitStatus.Matched && beforeGap.Observation == beforeBreak,
    "a match recorded before a gap still satisfies the wait");
var emptyStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"));
Check(emptyStore.TryGetSequenceForProviderSequence(0, out var initial) && initial == 0,
    "provider sequence 0 maps to the initial public sequence");
Check(await gapStore.WaitForProviderSequenceAsync(5, TimeSpan.FromMilliseconds(100), CancellationToken.None) &&
    !await gapStore.WaitForProviderSequenceAsync(6, TimeSpan.FromMilliseconds(60), CancellationToken.None),
    "bounded catch-up wait reports whether a provider sequence has been applied");

var closingStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now,
    static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
var closingWait = closingStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, gapSession, null, TimeSpan.FromSeconds(5), CancellationToken.None);
await Task.Yield();
closingStore.Close();
Check((await closingWait.WaitAsync(TimeSpan.FromSeconds(5))).Status == DebuggerObservationWaitStatus.ProviderDisconnected &&
    (await closingStore.WaitAsync(DebuggerWaitDesiredState.Break, 0, gapSession, null, TimeSpan.FromSeconds(1), CancellationToken.None)).Status ==
    DebuggerObservationWaitStatus.ProviderDisconnected && closingStore.IsClosed,
    "channel loss ends current and later waits as provider_disconnected");

// ---- v1.4: 停止理由・例外・終了結果の観測 ----
{
    var stopStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now, static (_, _) => Task.CompletedTask);
    var sessionC = Guid.NewGuid().ToString("N");
    stopStore.Publish(sessionC, null, DebuggerObservedState.Running);
    var exception = new VisualStudioMcpServer.Providers.ExceptionInfoBody { Kind = "unhandled", Category = "Managed", TypeName = "T", Code = 5, Description = "secret" };
    var reasoned = stopStore.Publish(sessionC, null, DebuggerObservedState.Break, 2, "exception_unhandled", exception);
    Check(reasoned.StopReason == "exception_unhandled" && reasoned.Exception == exception, "stop reason and exception are kept on the break observation");
    Check(stopStore.FindBySequence(reasoned.Sequence) == reasoned && stopStore.FindBySequence(99) is null, "observations are found by public sequence");
    var resumed = stopStore.Publish(sessionC, null, DebuggerObservedState.Running, 3);
    Check(resumed.StopReason is null && resumed.Exception is null, "a run observation carries no stop reason");
    Check(stopStore.LastEndedSession is null, "no ended session before a design observation with outcomes");
    var outcomes = new[] { new VisualStudioMcpServer.Providers.ProcessOutcomeBody { ProcessId = 7, StartTicks = 9, Outcome = "exited", ExitCode = 3 } };
    stopStore.Publish(sessionC, null, DebuggerObservedState.Design, 4, null, null, outcomes);
    Check(stopStore.LastEndedSession is { DebugSessionId: var endedId } && endedId == sessionC, "the last ended session keeps its process outcomes");
    Check(stopStore.LastEndedSession!.ProcessOutcomes.Single().ExitCode == 3, "the exit code is retained");
    var sessionD = Guid.NewGuid().ToString("N");
    stopStore.Publish(sessionD, null, DebuggerObservedState.Running, 5);
    Check(stopStore.LastEndedSession is null, "outcomes are cleared when the next session is observed");
    stopStore.Publish(sessionD, null, DebuggerObservedState.Design, 6, null, null, outcomes);
    Check(stopStore.LastEndedSession is not null, "a natural end records its outcomes");
    var sessionE = Guid.NewGuid().ToString("N");
    stopStore.Publish(sessionE, null, DebuggerObservedState.Running, 7);
    stopStore.Publish(sessionE, null, DebuggerObservedState.Design, 8);
    Check(stopStore.LastEndedSession is null, "an end without outcomes (agent stop) does not keep an older session's outcomes");

    // 履歴の欠落をはさんだ refinement は、前の break に付けず新しい観測になる。
    var refGapStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now, static (_, _) => Task.CompletedTask);
    var refGapSession = Guid.NewGuid().ToString("N");
    refGapStore.Publish(refGapSession, null, DebuggerObservedState.Running, 1);
    var breakA = refGapStore.Publish(refGapSession, null, DebuggerObservedState.Break, 2, "step");
    refGapStore.MarkHistoryGap();
    var lateRefinement = refGapStore.Publish(refGapSession, null, DebuggerObservedState.Break, 3, "exception_unhandled", null, null, refinement: true);
    Check(lateRefinement.Sequence > breakA.Sequence && refGapStore.FindBySequence(breakA.Sequence)?.StopReason == "step",
        "a refinement after a history gap does not overwrite the earlier break");

    // refinement: 同じ break に理由と例外を後から補う。新しい sequence は作らず、provider sequence は同じ観測へ対応付く。
    var refStore = new DebuggerObservationStore(Guid.NewGuid().ToString("N"), () => now, static (_, _) => Task.CompletedTask);
    var refSession = Guid.NewGuid().ToString("N");
    refStore.Publish(refSession, null, DebuggerObservedState.Running, 1);
    var firstBreak = refStore.Publish(refSession, null, DebuggerObservedState.Break, 2);
    var refinedBreak = refStore.Publish(refSession, null, DebuggerObservedState.Break, 3, "exception_unhandled", null, null, refinement: true);
    Check(refinedBreak.Sequence == firstBreak.Sequence && refStore.CurrentSequence == firstBreak.Sequence, "a refinement does not create a new public sequence");
    Check(refStore.FindBySequence(firstBreak.Sequence)?.StopReason == "exception_unhandled", "the refinement fills the reason of the published break");
    Check(refStore.TryGetSequenceForProviderSequence(2, out var s2) && s2 == firstBreak.Sequence &&
          refStore.TryGetSequenceForProviderSequence(3, out var s3) && s3 == firstBreak.Sequence, "both provider sequences map to the refined break");
    Check(refStore.LastProviderSequence == 3, "the last provider sequence advances with the refinement");
    var withException = refStore.Publish(refSession, null, DebuggerObservedState.Break, 4, "exception_unhandled", exception, null, refinement: true);
    Check(withException.Exception == exception && withException.Sequence == firstBreak.Sequence && refStore.FindBySequence(firstBreak.Sequence)?.StopReason == "exception_unhandled",
        "a later refinement adds the exception and keeps the reason");
    var stepAgain = refStore.Publish(refSession, null, DebuggerObservedState.Break, 5, "step");
    Check(stepAgain.Sequence == firstBreak.Sequence + 1, "a break without the refinement flag is a new transition");
    var orphan = refStore.Publish(Guid.NewGuid().ToString("N"), null, DebuggerObservedState.Break, 6, "step", null, null, refinement: true);
    Check(orphan.Sequence == stepAgain.Sequence + 1, "a refinement of another session is a new transition");

    var hidden = StopInfoProjection.Exception(exception, false);
    Check(hidden is { Description: null, DescriptionAvailability: "requires_allowSensitiveRead", Code: 5, TypeName: "T" },
        "the description is withheld without allowSensitiveRead");
    var shown = StopInfoProjection.Exception(exception, true);
    Check(shown is { Description: "secret", DescriptionAvailability: "available" }, "the description is returned with allowSensitiveRead");
    var long1 = StopInfoProjection.Exception(exception with { Description = new string('x', 3000) }, true);
    Check(long1!.Description!.Length == 1024 && long1.Truncated, "a long description is bounded and marked truncated");
    Check(StopInfoProjection.Exception(exception with { Description = null }, true) is { DescriptionAvailability: "unavailable" },
        "a missing description is unavailable");
    Check(StopInfoProjection.Availability(false, "break", reasoned) == "unsupported", "no provider means unsupported");
    Check(StopInfoProjection.Availability(true, "break", reasoned) == "available", "a recorded reason is available");
    Check(StopInfoProjection.Availability(true, "break", null) == "unknown", "a provider-backed break without a reason is unknown");
    var view = StopInfoProjection.View(true, "break", reasoned.Sequence, sessionC, stopStore, false);
    Check(view.StopReason == "exception_unhandled" && view.Exception?.Description is null && view.Availability == "available",
        "get_debugger_state projects the stop reason and hides the description by default");
    var json = System.Text.Json.JsonSerializer.Serialize(view);
    Check(!json.Contains("secret"), "the serialized view never contains the description without allowSensitiveRead");
    var lastView = StopInfoProjection.View(true, "design", null, null, stopStore, false);
    Check(lastView.LastSessionId is null, "no ended session is reported before one has ended");
    Check(StopInfoProjection.View(false, "break", reasoned.Sequence, sessionC, stopStore, true).Availability == "unsupported",
        "the view is unsupported without a provider");
}
Console.WriteLine($"Debugger observation validation: {passed} checks passed.");

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
}

void Throws<TException>(Action action, string description) where TException : Exception
{
    try { action(); }
    catch (TException) { passed++; return; }
    throw new InvalidOperationException(description);
}

async Task ThrowsAsync<TException>(Func<Task> action, string description) where TException : Exception
{
    try { await action(); }
    catch (TException) { passed++; return; }
    throw new InvalidOperationException(description);
}
