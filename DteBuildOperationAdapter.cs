using EnvDTE;
using EnvDTE80;
using System.Runtime.InteropServices;

namespace VisualStudioMcpServer;

/// <summary>Adapts build operations to the single Visual Studio instance bound to this server.</summary>
internal sealed class DteBuildOperationAdapter : IBuildOperationAdapter
{
    private readonly int _targetPid;
    private readonly long _targetStartTicks;
    private DTE2? _dte;
    private bool _dispatching;
    private bool _ownedBuild;

    public DteBuildOperationAdapter(int targetPid, long targetStartTicks)
    {
        _targetPid = targetPid;
        _targetStartTicks = targetStartTicks;
    }

    public BuildTargetSnapshot ReadTargetSnapshot() => StaComDispatcher.Invoke(() =>
    {
        var alive = false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_targetPid);
            alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == _targetStartTicks;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        if (!alive)
            return new BuildTargetSnapshot(_targetPid, _targetStartTicks, _targetPid, _targetStartTicks,
                false, null, null, null, false, false, false);

        var dte = VisualStudioConnector.GetActiveVS();
        if (dte is null)
            return new BuildTargetSnapshot(_targetPid, _targetStartTicks, _targetPid, _targetStartTicks,
                false, null, null, null, false, false, false);
        _dte = dte;

        var solution = dte.Solution;
        var fullName = solution?.FullName;
        var solutionBuild = solution?.SolutionBuild;
        var state = solutionBuild?.BuildState ?? vsBuildState.vsBuildStateDone;
        var configuration = solutionBuild?.ActiveConfiguration;
        var debuggerMode = dte.Debugger?.CurrentMode.ToString() ?? "Design";
        return new BuildTargetSnapshot(_targetPid, _targetStartTicks, _targetPid, _targetStartTicks,
            true, string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            configuration?.Name, null,
            debuggerMode.Contains("Design", StringComparison.OrdinalIgnoreCase),
            state == vsBuildState.vsBuildStateInProgress, false);
    }, TimeSpan.FromSeconds(5));

    public IDisposable SubscribeBuildEvents(Action<BuildEventSnapshot> onBegin, Action<BuildEventSnapshot> onDone) =>
        StaComDispatcher.Invoke(() =>
        {
            var dte = _dte ?? VisualStudioConnector.GetActiveVS()
                ?? throw new COMException("The registered Visual Studio automation object is disconnected.");
            var events = dte.Events.BuildEvents;
            _dispatching = false;
            _onBegin = onBegin;
            _onDone = onDone;
            _buildEvents = events;
            _buildEvents.OnBuildBegin += OnBuildBegin;
            _buildEvents.OnBuildDone += OnBuildDone;
            return (IDisposable)new Subscription(this);
        }, TimeSpan.FromSeconds(5));

    private BuildEvents? _buildEvents;
    private Action<BuildEventSnapshot>? _onBegin;
    private Action<BuildEventSnapshot>? _onDone;

    public void Dispatch(BuildAction action) => StaComDispatcher.Invoke(() =>
    {
        var dte = _dte ?? VisualStudioConnector.GetActiveVS()
            ?? throw new COMException("The registered Visual Studio automation object is disconnected.");
        _dispatching = true;
        try
        {
            switch (action)
            {
                case BuildAction.Build:
                    dte.Solution.SolutionBuild.Build(false);
                    break;
                case BuildAction.Rebuild:
                    dte.ExecuteCommand("Build.RebuildSolution");
                    break;
                case BuildAction.Clean:
                    dte.Solution.SolutionBuild.Clean(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
            return true;
        }
        finally { _dispatching = false; }
    }, TimeSpan.FromSeconds(10));

    public void DisposeSubscription(IDisposable subscription) => _ = StaComDispatcher.Invoke(() =>
    {
        subscription.Dispose();
        return true;
    }, TimeSpan.FromSeconds(5));

    private void OnBuildBegin(vsBuildScope scope, vsBuildAction action)
    {
        var owned = _dispatching;
        if (owned) _ownedBuild = true;
        _onBegin?.Invoke(ReadEventSnapshot(owned));
    }

    private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
    {
        var owned = _ownedBuild;
        _onDone?.Invoke(ReadEventSnapshot(owned));
        _ownedBuild = false;
    }

    private BuildEventSnapshot ReadEventSnapshot(bool owned)
    {
        var dte = _dte;
        if (dte is null) return new BuildEventSnapshot(null, true, false, null, false);
        var solutionBuild = dte.Solution.SolutionBuild;
        var inProgress = solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress;
        int? failedProjects = null;
        if (!inProgress)
        {
            try { failedProjects = solutionBuild.LastBuildInfo; }
            catch (COMException) { }
        }
        return new BuildEventSnapshot(dte.Solution.FullName, inProgress,
            false, failedProjects, owned);
    }

    private sealed class Subscription(DteBuildOperationAdapter owner) : IDisposable
    {
        private DteBuildOperationAdapter? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            var events = current._buildEvents;
            if (events is not null)
            {
                events.OnBuildBegin -= current.OnBuildBegin;
                events.OnBuildDone -= current.OnBuildDone;
            }
            current._onBegin = null;
            current._onDone = null;
            current._buildEvents = null;
            current._dte = null;
            current._dispatching = false;
            current._ownedBuild = false;
        }
    }
}
