using EnvDTE;

namespace VisualStudioMcpServer;

/// <summary>
/// Retains debugger event connections on the managed COM STA for one control request.
/// </summary>
internal sealed class DebuggerControlEvents : IDisposable
{
    private readonly Events _eventRoot;
    private readonly DebuggerEvents _events;
    private bool _breakAttached;
    private bool _runAttached;
    private bool _designAttached;
    private int _sawBreak;
    private int _sawRun;
    private int _sawDesign;

    /// <summary>Subscribes before execution is requested so fast transitions are retained.</summary>
    internal DebuggerControlEvents(DTE dte)
    {
        _eventRoot = dte.Events;
        _events = _eventRoot.DebuggerEvents;
        try
        {
            _events.OnEnterBreakMode += OnBreak;
            _breakAttached = true;
            _events.OnEnterRunMode += OnRun;
            _runAttached = true;
            _events.OnEnterDesignMode += OnDesign;
            _designAttached = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool SawBreak => Volatile.Read(ref _sawBreak) != 0;
    internal bool SawRun => Volatile.Read(ref _sawRun) != 0;
    internal bool SawDesign => Volatile.Read(ref _sawDesign) != 0;

    private void OnBreak(dbgEventReason reason, ref dbgExecutionAction executionAction) => Interlocked.Exchange(ref _sawBreak, 1);
    private void OnRun(dbgEventReason reason) => Interlocked.Exchange(ref _sawRun, 1);
    private void OnDesign(dbgEventReason reason) => Interlocked.Exchange(ref _sawDesign, 1);

    /// <summary>Disconnects all subscribed callbacks on the same COM STA.</summary>
    public void Dispose()
    {
        try
        {
            if (_breakAttached) { _breakAttached = false; _events.OnEnterBreakMode -= OnBreak; }
        }
        finally
        {
            try
            {
                if (_runAttached) { _runAttached = false; _events.OnEnterRunMode -= OnRun; }
            }
            finally
            {
                if (_designAttached) { _designAttached = false; _events.OnEnterDesignMode -= OnDesign; }
                GC.KeepAlive(_eventRoot);
            }
        }
    }
}
