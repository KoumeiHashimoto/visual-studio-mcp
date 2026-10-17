namespace VisualStudioMcpServer.Workspace;

/// <summary>
/// Bounded metadata published by the owning VSIX from the active public Workspace API.
/// </summary>
public sealed record ActiveWorkspaceSnapshot(
    bool IsActive,
    int VisualStudioPid,
    long VisualStudioStartTicks,
    long Generation,
    string Root,
    string WorkspaceKind,
    string CMakeMetadataStatus,
    string CMakeMetadataReason);

/// <summary>
/// Validates and retains the active workspace metadata supplied by this server's owning VSIX.
/// </summary>
public sealed class ActiveWorkspaceSnapshotStore
{
    public const int MaxRootLength = 2048;
    public const int MaxReasonLength = 512;

    private readonly int _visualStudioPid;
    private readonly long _visualStudioStartTicks;
    private readonly object _gate = new();
    private ActiveWorkspaceSnapshot? _current;
    private long _lastGeneration;

    public ActiveWorkspaceSnapshotStore(int visualStudioPid, long visualStudioStartTicks)
    {
        if (visualStudioPid <= 0) throw new ArgumentOutOfRangeException(nameof(visualStudioPid));
        if (visualStudioStartTicks <= 0) throw new ArgumentOutOfRangeException(nameof(visualStudioStartTicks));
        _visualStudioPid = visualStudioPid;
        _visualStudioStartTicks = visualStudioStartTicks;
    }

    public bool TryUpdate(ActiveWorkspaceSnapshot? update)
    {
        if (update is null || update.VisualStudioPid != _visualStudioPid ||
            update.VisualStudioStartTicks != _visualStudioStartTicks || update.Generation <= 0)
            return false;

        if (update.IsActive && (string.IsNullOrWhiteSpace(update.Root) || update.Root.Length > MaxRootLength ||
            !Path.IsPathFullyQualified(update.Root) || update.WorkspaceKind != "openFolder" ||
            update.CMakeMetadataStatus != "unsupported" || string.IsNullOrWhiteSpace(update.CMakeMetadataReason) ||
            update.CMakeMetadataReason.Length > MaxReasonLength)) return false;

        if (!update.IsActive && (update.Root is null || update.Root.Length != 0 ||
            update.WorkspaceKind is null || update.WorkspaceKind.Length != 0)) return false;

        lock (_gate)
        {
            if (update.Generation < _lastGeneration) return false;
            if (update.Generation == _lastGeneration)
                return update.IsActive ? update == _current : _current is null;
            _lastGeneration = update.Generation;
            _current = update.IsActive ? update : null;
        }
        return true;
    }

    public ActiveWorkspaceSnapshot? GetCurrent()
    {
        lock (_gate) return _current;
    }
}

/// <summary>Provides the current HTTP-host workspace snapshot to the MCP tool layer.</summary>
public static class ActiveWorkspaceSnapshotStoreAccessor
{
    private static ActiveWorkspaceSnapshotStore? _store;

    public static void Set(ActiveWorkspaceSnapshotStore store) => Volatile.Write(ref _store, store);
    public static ActiveWorkspaceSnapshotStore? Get() => Volatile.Read(ref _store);
    public static void Clear(ActiveWorkspaceSnapshotStore store) => Interlocked.CompareExchange(ref _store, null, store);
}
