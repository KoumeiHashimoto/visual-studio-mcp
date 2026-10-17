using EnvDTE;
using EnvDTE80;
using System.Runtime.InteropServices;

namespace VisualStudioMcpServer;

/// <summary>Adapts build operations to the single Visual Studio instance bound to this server.</summary>
internal sealed class DteBuildOperationAdapter : IBuildOperationAdapter
{
    private readonly int _targetPid;
    private readonly long _targetStartTicks;
    private readonly BuildEventOwnership _eventOwnership = new();
    private DTE2? _dte;
    private string? _solutionPath;
    private bool _dispatching;
    private BuildAction? _requestedAction;
    private int _eventSequence;

    public DteBuildOperationAdapter(int targetPid, long targetStartTicks)
    {
        _targetPid = targetPid;
        _targetStartTicks = targetStartTicks;
    }

    public BuildTargetSnapshot ReadTargetSnapshot(TimeSpan timeout) => StaComDispatcher.Invoke(() =>
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
        _solutionPath = string.IsNullOrWhiteSpace(fullName) ? null : fullName;
        var solutionBuild = solution?.SolutionBuild;
        var state = solutionBuild?.BuildState ?? vsBuildState.vsBuildStateDone;
        var configuration = solutionBuild?.ActiveConfiguration;
        var debuggerMode = dte.Debugger?.CurrentMode.ToString() ?? "Design";
        return new BuildTargetSnapshot(_targetPid, _targetStartTicks, _targetPid, _targetStartTicks,
            true, string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            configuration?.Name, null,
            debuggerMode.Contains("Design", StringComparison.OrdinalIgnoreCase),
            state == vsBuildState.vsBuildStateInProgress, false);
    }, timeout);

    public IDisposable SubscribeBuildEvents(Action<BuildEventSnapshot> onBegin, Action<BuildEventSnapshot> onDone, TimeSpan timeout) =>
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
        }, timeout);

    private BuildEvents? _buildEvents;
    private Action<BuildEventSnapshot>? _onBegin;
    private Action<BuildEventSnapshot>? _onDone;

    public void Dispatch(BuildAction action, int correlationWindowMilliseconds, TimeSpan timeout)
    {
        try
        {
            _ = StaComDispatcher.Invoke(() =>
            {
                var dte = _dte ?? VisualStudioConnector.GetActiveVS()
                    ?? throw new COMException("The registered Visual Studio automation object is disconnected.");
                _dispatching = true;
                _requestedAction = action;
                _eventOwnership.BeginDispatch(correlationWindowMilliseconds, DateTimeOffset.UtcNow);
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
            }, timeout);
        }
        catch (StaComInvocationTimeoutException ex)
        {
            throw new BuildDispatchException(ex.Message, ex.ExecutionStarted, ex);
        }
        catch (TimeoutException) { throw; }
        catch (BuildActionUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw new BuildDispatchException(ex.Message, true, ex);
        }
    }

    public Task DisposeSubscriptionAsync(IDisposable subscription) => StaComDispatcher.Post(subscription.Dispose);

    private void OnBuildBegin(vsBuildScope scope, vsBuildAction action)
    {
        if (scope != vsBuildScope.vsBuildScopeSolution)
        {
            if (!_eventOwnership.IsOwnedBuild) _onBegin?.Invoke(new BuildEventSnapshot(_solutionPath, true, false, null, false) { EventScope = scope.ToString(), EventAction = action.ToString() });
            return;
        }
        Interlocked.Increment(ref _eventSequence);
        var owned = _eventOwnership.OnBuildBegin(_dispatching, MatchesRequestedAction(action), DateTimeOffset.UtcNow);
        _onBegin?.Invoke(new BuildEventSnapshot(_solutionPath, true, false, null, owned) { EventScope = scope.ToString(), EventAction = action.ToString() });
    }

    private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
    {
        if (scope != vsBuildScope.vsBuildScopeSolution)
        {
            if (!_eventOwnership.IsOwnedBuild) _onDone?.Invoke(new BuildEventSnapshot(_solutionPath, true, false, null, false) { IsTerminalState = false, EventScope = scope.ToString(), EventAction = action.ToString() });
            return;
        }
        var sequence = Interlocked.Increment(ref _eventSequence);
        var matchesAction = MatchesRequestedAction(action);
        var owned = _eventOwnership.OnBuildDone(_dispatching, matchesAction, DateTimeOffset.UtcNow);
        var onDone = _onDone;
        var solutionPath = _solutionPath;
        _ = StaComDispatcher.Post(() =>
        {
            BuildEventSnapshot snapshot;
            try { snapshot = ReadEventSnapshot(owned, sequence, scope, action); }
            catch (COMException)
            {
                snapshot = new BuildEventSnapshot(solutionPath, true, false, null, owned)
                {
                    IsTerminalState = false,
                    EventScope = scope.ToString(),
                    EventAction = action.ToString()
                };
            }
            catch (InvalidComObjectException)
            {
                snapshot = new BuildEventSnapshot(solutionPath, true, false, null, owned)
                {
                    IsTerminalState = false,
                    EventScope = scope.ToString(),
                    EventAction = action.ToString()
                };
            }
            catch (Exception)
            {
                snapshot = new BuildEventSnapshot(solutionPath, true, false, null, owned)
                {
                    IsTerminalState = false,
                    EventScope = scope.ToString(),
                    EventAction = action.ToString()
                };
            }
            onDone?.Invoke(snapshot);
        });
    }

    private BuildEventSnapshot ReadEventSnapshot(bool owned, int sequence, vsBuildScope scope, vsBuildAction action)
    {
        var dte = _dte;
        if (dte is null) return new BuildEventSnapshot(null, true, false, null, false);
        var solutionBuild = dte.Solution.SolutionBuild;
        var state = solutionBuild.BuildState;
        var inProgress = state == vsBuildState.vsBuildStateInProgress;
        var terminal = state is vsBuildState.vsBuildStateDone or vsBuildState.vsBuildStateNotStarted;
        var hasBuildResult = state == vsBuildState.vsBuildStateDone;
        int? failedProjects = null;
        if (hasBuildResult && _requestedAction != BuildAction.Clean)
        {
            try { failedProjects = solutionBuild.LastBuildInfo; }
            catch (COMException) { }
        }
        var path = dte.Solution.FullName;
        if (Interlocked.CompareExchange(ref _eventSequence, 0, 0) != sequence) terminal = false;
        return new BuildEventSnapshot(path, inProgress,
            false, failedProjects, owned)
        {
            IsTerminalState = terminal,
            HasBuildResult = hasBuildResult,
            EventScope = scope.ToString(),
            EventAction = action.ToString()
        };
    }

    private bool MatchesRequestedAction(vsBuildAction action) => _requestedAction switch
    {
        BuildAction.Build => action == vsBuildAction.vsBuildActionBuild,
        BuildAction.Rebuild => action == vsBuildAction.vsBuildActionRebuildAll,
        BuildAction.Clean => action == vsBuildAction.vsBuildActionClean,
        _ => false
    };

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
            current._eventOwnership.Reset();
        }
    }
}
