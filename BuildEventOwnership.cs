namespace VisualStudioMcpServer;

internal sealed class BuildEventOwnership
{
    private bool _ownedBuild;
    private bool _dispatchPending;
    private DateTimeOffset _correlationDeadline;
    public bool IsOwnedBuild => _ownedBuild;

    public void BeginDispatch(int correlationWindowMilliseconds, DateTimeOffset now)
    {
        _ownedBuild = false;
        _dispatchPending = true;
        _correlationDeadline = now.AddMilliseconds(Math.Clamp(correlationWindowMilliseconds, 1, 30000));
    }

    public bool OnBuildBegin(bool dispatching, bool matchesRequestedAction, DateTimeOffset now)
    {
        if (!matchesRequestedAction)
        {
            _dispatchPending = false;
            return false;
        }

        if (_ownedBuild) return true;
        if (dispatching || IsDispatchPending(now))
        {
            _ownedBuild = true;
            _dispatchPending = false;
            return true;
        }
        return false;
    }

    public bool OnBuildDone(bool dispatching, bool matchesRequestedAction, DateTimeOffset now)
    {
        var owned = matchesRequestedAction && (_ownedBuild || dispatching || IsDispatchPending(now));
        _ownedBuild = false;
        _dispatchPending = false;
        return owned;
    }

    public void Reset()
    {
        _ownedBuild = false;
        _dispatchPending = false;
        _correlationDeadline = default;
    }

    private bool IsDispatchPending(DateTimeOffset now)
    {
        if (_dispatchPending && now <= _correlationDeadline) return true;
        _dispatchPending = false;
        return false;
    }
}
