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
