using VisualStudioMcpServer;

namespace VisualStudioMcpServer.Workspace;

/// <summary>Fails closed when a public Workspace build action cannot be bound to the active workspace.</summary>
internal static class OpenFolderBuildPolicy
{
    private const string UnsupportedMessage =
        "The active Open Folder workspace has no verified public build action and active-configuration binding. No build action was dispatched.";

    internal static BuildOperationResult? Check(ActiveWorkspaceSnapshot? snapshot, int processId, long startTicks,
        string? action, bool allowBuild, string? expectedWorkspaceRoot, int timeoutMilliseconds)
    {
        if (snapshot is null || !snapshot.IsActive || snapshot.WorkspaceKind != "openFolder" ||
            snapshot.VisualStudioPid != processId || snapshot.VisualStudioStartTicks != startTicks)
            return null;

        var boundedTimeout = Math.Clamp(timeoutMilliseconds, 1000, 30000);
        var normalizedAction = action?.ToLowerInvariant();

        if (action is not null && !allowBuild)
            return Result("build_policy_required", "Set allowBuild=true to authorize this build action.", false);

        if (string.IsNullOrWhiteSpace(expectedWorkspaceRoot))
            return Result("expected_workspace_required", "The expected Open Folder workspace root is required.", false);

        if (expectedWorkspaceRoot.Length > ActiveWorkspaceSnapshotStore.MaxRootLength ||
            !Path.IsPathFullyQualified(expectedWorkspaceRoot))
            return Result("invalid_argument", "The expected workspace root must be a bounded absolute path.", false);

        string expectedRoot;
        string activeRoot;
        try
        {
            expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedWorkspaceRoot));
            activeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.Root));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Result("invalid_argument", "The expected workspace root is not a valid path.", false);
        }

        if (!string.Equals(expectedRoot, activeRoot, StringComparison.OrdinalIgnoreCase))
            return Result("workspace_mismatch", "The active Open Folder workspace does not match the expected root.", false);

        return Result("unsupported", UnsupportedMessage, false);

        BuildOperationResult Result(string code, string message, bool success) => new(
            success, code, message, processId, null, normalizedAction, null, null, null,
            "idle", null, null, null, null, false, 0, boundedTimeout)
        {
            WorkspaceRoot = snapshot.Root
        };
    }
}
