using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Gets information about the currently open solution or supported workspace.
/// Helps Claude confirm it is looking at the right project.
/// </summary>
[McpServerToolType]
public static class GetSolutionInfoTool
{
    [McpServerTool]
    [Description("Returns information about the currently open Visual Studio solution or supported Open Folder workspace, including its path and list of projects.")]
    public static string GetSolutionInfo(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        return ReadOnlyToolInvocation.Invoke("get_solution_info", () =>
        {
            var info = VisualStudioConnector.GetSolutionInfo();
            if (info is null)
            {
                return ReadOnlyToolInvocation.Error("no_solution", "No solution is currently open in the target Visual Studio instance. Open a solution or folder and retry.");
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return McpOutputBounds.Limit(JsonConvert.SerializeObject(info, Formatting.Indented));
            }

            var lines = new List<string>
            {
                info.WorkspaceKind is null ? "Current Solution:" : "Current Open Folder Workspace:",
                $"  Name: {info.SolutionName}",
                $"  Path: {info.SolutionPath}",
                "",
                $"Projects ({info.Projects.Count}):"
            };

            if (info.WorkspaceKind is not null)
            {
                lines.Add($"  Workspace kind: {info.WorkspaceKind}");
                lines.Add($"  Workspace root: {info.WorkspaceRoot}");
                lines.Add($"  CMake metadata: {info.CMakeMetadataStatus} ({info.CMakeMetadataReason})");
                lines.Add($"  Active configuration: {info.ActiveConfigurationStatus} ({info.ActiveConfigurationReason})");
                lines.Add($"  Startup target: {info.StartupTargetStatus} ({info.StartupTargetReason})");
            }

            foreach (var project in info.Projects)
            {
                lines.Add($"  - {project}");
            }

            return McpOutputBounds.Limit(string.Join("\n", lines));
        });
    }
}
