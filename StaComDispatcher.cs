using System.Collections.Concurrent;

namespace VisualStudioMcpServer;

/// <summary>
/// Marshals Visual Studio COM calls to a dedicated STA thread.
/// </summary>
internal static class StaComDispatcher
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly object StartLock = new();
    private static Thread? _thread;
    private static int _threadId;

    /// <summary>
    /// Starts the COM worker thread once.
    /// </summary>
    public static void Start()
    {
        lock (StartLock)
        {
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Run(ready))
            {
                IsBackground = true,
                Name = "Visual Studio MCP COM STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The Visual Studio COM STA did not start in time.");
        }
    }

    /// <summary>
    /// Queues an operation on the STA thread and waits for a bounded time.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The operation to run on the STA thread.</param>
    /// <param name="timeout">The maximum wait time.</param>
    /// <returns>The operation result.</returns>
    public static T Invoke<T>(Func<T> operation, TimeSpan? timeout = null)
    {
        Start();
        if (Environment.CurrentManagedThreadId == _threadId) return operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { completion.TrySetResult(operation()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        if (!completion.Task.Wait(timeout ?? TimeSpan.FromSeconds(15)))
            throw new TimeoutException("The Visual Studio COM request exceeded its time limit.");
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void Run(ManualResetEventSlim ready)
    {
        _threadId = Environment.CurrentManagedThreadId;
        MessageFilter.Register();
        ready.Set();
        try
        {
            foreach (var action in Queue.GetConsumingEnumerable()) action();
        }
        finally
        {
            MessageFilter.Revoke();
        }
    }
}
