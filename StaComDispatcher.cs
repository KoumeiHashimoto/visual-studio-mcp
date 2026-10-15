using System.Collections.Concurrent;

using System.Runtime.InteropServices;

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
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Visual Studio COM automation requires Windows.");
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

    /// <summary>Queues cleanup that must run on the STA even after a caller stops waiting.</summary>
    public static Task Post(Action operation)
    {
        Start();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try { operation(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
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
        var state = 0;
        Queue.Add(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { completion.TrySetResult(operation()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        try
        {
            return completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        finally
        {
            Interlocked.CompareExchange(ref state, 2, 0);
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        _threadId = Environment.CurrentManagedThreadId;
        MessageFilter.Register();
        ready.Set();
        try
        {
            while (!Queue.IsCompleted)
            {
                if (Queue.TryTake(out var action, 20)) action();
                for (var index = 0; index < 64 && PeekMessage(out var message, IntPtr.Zero, 0, 0, 1); index++)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
        }
        finally
        {
            MessageFilter.Revoke();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
