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
    private readonly ManualResetEventSlim _quarantineRelease = new(false);
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

    internal bool IsQuarantined => _quarantined;

    /// <summary>mutex 取得時に作る owner generation（UUID-N）。Hello と execution.dispatch の ownerFence に使う。</summary>
    internal string OwnerGeneration { get; } = Guid.NewGuid().ToString("N");

    internal static bool IsCurrentQuarantined
    {
        get { lock (Sync) return _current?._quarantined == true; }
    }

    internal static string? CurrentOwnerFence
    {
        get { lock (Sync) return _current?.OwnerGeneration; }
    }

    /// <summary>ownership.reconcile が解除条件をすべて確認した場合だけ呼ぶ。timer/retry/再接続からは呼ばない。</summary>
    internal static bool TryReleaseCurrentQuarantine()
    {
        lock (Sync) return _current?.ReleaseQuarantine() == true;
    }

    /// <summary>quarantine 中に保持している abandoned mutex を通常の所有へ移す。</summary>
    internal bool ReleaseQuarantine()
    {
        if (!_quarantined) return false;
        _quarantineRelease.Set();
        // lease thread が所有状態へ移るまで待つ（mutex は同じ thread が保持し続ける）。
        return _ready.Wait(TimeSpan.FromSeconds(2)) && SpinWait.SpinUntil(() => Volatile.Read(ref _ownsMutationLease), TimeSpan.FromSeconds(2));
    }

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
                // Keep the abandoned lease unavailable until reconciliation proves it safe; no timer clears uncertainty.
                _quarantineRelease.Wait();
                _quarantined = false;
                Volatile.Write(ref _ownsMutationLease, true);
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
