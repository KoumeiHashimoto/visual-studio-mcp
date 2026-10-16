using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualStudioMcpServer.Tools;

/// <summary>Shared build operations used by both stdio and authenticated HTTP transports.</summary>
[McpServerToolType]
public class BuildOperationTools
{
    private static readonly object Sync = new();
    private static BuildOperationCoordinator? _coordinator;
    private static readonly SemaphoreSlim WorkspaceGate = WorkspaceMutationGate.Shared;

    [McpServerTool]
    [Description("Builds the solution for this registered Visual Studio instance. Explicitly set allowBuild=true and expectedSolutionPath.")]
    public static Task<string> BuildSolution(
        [Description("Wait for the operation to finish before returning.")] bool waitForBuild = true,
        [Description("Output format: text or json.")] string format = "text",
        [Description("Must be true to authorize a build.")] bool allowBuild = false,
        [Description("Expected absolute path of the currently open solution.")] string? expectedSolutionPath = null,
        [Description("Maximum wait in milliseconds (1000-30000).")] int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BuildAction.Build, waitForBuild, format, allowBuild, expectedSolutionPath, timeoutMilliseconds, cancellationToken);

    [McpServerTool]
    [Description("Rebuilds the solution for this registered Visual Studio instance. Explicitly set allowBuild=true and expectedSolutionPath.")]
    public static Task<string> RebuildSolution(
        bool waitForBuild = true, string format = "text", bool allowBuild = false,
        string? expectedSolutionPath = null, int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BuildAction.Rebuild, waitForBuild, format, allowBuild, expectedSolutionPath, timeoutMilliseconds, cancellationToken);

    [McpServerTool]
    [Description("Cleans the solution for this registered Visual Studio instance. Explicitly set allowBuild=true and expectedSolutionPath.")]
    public static Task<string> CleanSolution(
        bool waitForBuild = true, string format = "text", bool allowBuild = false,
        string? expectedSolutionPath = null, int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(BuildAction.Clean, waitForBuild, format, allowBuild, expectedSolutionPath, timeoutMilliseconds, cancellationToken);

    [McpServerTool]
    [Description("Returns the active or most recently retained build operation. An unknown operationId returns operation_not_found.")]
    public static string GetBuildStatus(string? operationId = null, string format = "json")
    {
        if (!ValidFormat(format)) return Format(new BuildOperationResult(false, "invalid_format", "Format must be json or text.", null, operationId, null, null, null, null, "idle", null, null, null, null, null, 0, 10000), format);
        if (!VisualStudioConnector.TryGetTargetProcessIdentity(out var processId, out _))
            return Format(new BuildOperationResult(false, "target_identity_required", "The registered Visual Studio process identity is required.", null, operationId, null, null, null, null, "idle", null, null, null, null, null, 0, 10000), format);
        BuildOperationResult result;
        lock (Sync) result = _coordinator?.GetStatus(operationId) ?? Idle() with { TargetProcessId = processId };
        return Format(result, format);
    }

    private static async Task<string> ExecuteAsync(BuildAction action, bool waitForBuild, string format,
        bool allowBuild, string? expectedSolutionPath, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (!ValidFormat(format)) return Format(new BuildOperationResult(false, "invalid_format", "Format must be json or text.", null, null, action.ToString().ToLowerInvariant(), null, null, null, "idle", null, null, null, null, null, 0, Math.Clamp(timeoutMilliseconds, 1000, 30000)), format);
        if (!VisualStudioConnector.TryGetTargetProcessIdentity(out var processId, out var startTicks))
            return Format(new BuildOperationResult(false, "target_identity_required", "The registered Visual Studio process identity is required.", null, null, action.ToString().ToLowerInvariant(), null, null, null, "idle", null, null, null, null, null, 0, Math.Clamp(timeoutMilliseconds, 1000, 30000)), format);

        BuildOperationCoordinator coordinator;
        lock (Sync)
        {
            coordinator = _coordinator ??= new BuildOperationCoordinator(
                new DteBuildOperationAdapter(processId, startTicks), WorkspaceGate);
        }
        var result = await coordinator.ExecuteAsync(action, expectedSolutionPath, processId, startTicks,
            allowBuild, timeoutMilliseconds, format, cancellationToken, waitForBuild).ConfigureAwait(false);
        return Format(result, format);
    }

    private static bool ValidFormat(string format) =>
        string.Equals(format, "text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

    private static string Format(BuildOperationResult result, string format) =>
        string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? BuildOperationResult.ToBoundedJson(result)
            : $"{result.Code}: {result.Message} (state={result.State}, operationId={result.OperationId ?? "none"})";

    private static BuildOperationResult Idle() => new(true, "ok", "No build operation has been recorded.", null,
        null, null, null, null, null, "idle", null, null, null, null, false, 0, 10000);
}
