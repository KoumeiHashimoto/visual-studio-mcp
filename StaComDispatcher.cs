using System.Collections.Concurrent;

namespace VisualStudioMcpServer;

/// <summary>
/// Visual Studio COM呼び出しを専用STAスレッドへ集約します。
/// </summary>
internal static class StaComDispatcher
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly object StartLock = new();
    private static Thread? _thread;
    private static int _threadId;

    /// <summary>
    /// COM実行用スレッドを一度だけ開始します。
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
    /// 処理をSTAスレッドへ送り、上限時間内に結果を返します。
    /// </summary>
    /// <typeparam name="T">処理結果の型。</typeparam>
    /// <param name="operation">STA上で実行する処理。</param>
    /// <param name="timeout">待機上限。</param>
    /// <returns>処理結果。</returns>
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
