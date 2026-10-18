using System.Diagnostics;

namespace VisualStudioMcpServer.Coordination;

/// <summary>Holds the single companion ownership lease for one Visual Studio process identity.</summary>
internal sealed class TargetMutationOwner : IDisposable
{
    private static readonly object Sync = new();
    private static TargetMutationOwner? _current;
    private readonly string _name;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _shutdown = new(false);
    private readonly Thread _ownerThread;
    private bool _ownsMutationLease;
    private bool _quarantined;

    private TargetMutationOwner(string name)
    {
        _name = name;
        _ownerThread = new Thread(RunLeaseThread)
        {
            IsBackground = true,
            Name = "VisualStudioDebugMcp mutation owner"
        };
        _ownerThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)))
            throw new TimeoutException("The target mutation owner lease did not initialize in time.");
    }

    internal static bool IsCurrentOwner
    {
        get { lock (Sync) return _current?._ownsMutationLease == true; }
    }

    internal bool IsOwner => _ownsMutationLease;

    internal static void Initialize(int processId, long startTicks)
    {
        TargetMutationOwner? acquired = null;
        try { acquired = TryAcquire(processId, startTicks); }
        catch (TimeoutException) { }
        lock (Sync)
        {
            _current?.ReleaseLease();
            _current = acquired;
        }
    }

    internal static TargetMutationOwner? TryAcquire(int processId, long startTicks)
    {
        if (processId <= 0 || startTicks <= 0) return null;
        try
        {
            using var target = Process.GetProcessById(processId);
            if (target.StartTime.ToUniversalTime().Ticks != startTicks) return null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }

        var name = GetMutexName(processId, startTicks);
        try
        {
            var owner = new TargetMutationOwner(name);
            return owner._ownsMutationLease || owner._quarantined ? owner : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string GetMutexName(int processId, long startTicks) =>
        "Local\\VisualStudioDebugMcp-MutationOwner-" +
        processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
        startTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static void Shutdown()
    {
        TargetMutationOwner? current;
        lock (Sync)
        {
            current = _current;
            _current = null;
        }
        current?.ReleaseLease();
    }

    private void RunLeaseThread()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, _name);
            try { _ownsMutationLease = mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                _quarantined = true;
                _ownsMutationLease = false;
            }
            _ready.Set();
            if (_quarantined)
            {
                // Keep the abandoned lease unavailable until this process exits; no timer clears uncertainty.
                Thread.Sleep(Timeout.Infinite);
                return;
            }
            if (!_ownsMutationLease) return;
            _shutdown.Wait();
            mutex.ReleaseMutex();
        }
        catch (UnauthorizedAccessException)
        {
            _ownsMutationLease = false;
            _ready.Set();
        }
        finally
        {
            if (!_quarantined) mutex?.Dispose();
        }
    }

    private void ReleaseLease()
    {
        if (_quarantined) return;
        _shutdown.Set();
        if (Thread.CurrentThread != _ownerThread) _ownerThread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        _shutdown.Dispose();
    }

    public void Dispose() => ReleaseLease();
}
