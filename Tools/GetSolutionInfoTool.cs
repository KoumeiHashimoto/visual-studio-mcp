using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Gets information about the currently open solution.
/// Helps Claude confirm it's looking at the right project.
/// </summary>
[McpServerToolType]
public static class GetSolutionInfoTool
{
    [McpServerTool]
    [Description("Returns information about the currently open Visual Studio solution, including its path and list of projects.")]
    public static string GetSolutionInfo(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        return ReadOnlyToolInvocation.Invoke("get_solution_info", () =>
        {
            var info = VisualStudioConnector.GetSolutionInfo();

            if (info == null)
            {
                return ReadOnlyToolInvocation.Error("no_solution", "No solution is currently open in the target Visual Studio instance. Open a solution or folder and retry.");
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return McpOutputBounds.Limit(JsonConvert.SerializeObject(info, Formatting.Indented));
            }

            // Text format
            var lines = new List<string>
            {
                "Current Solution:",
                $"  Name: {info.SolutionName}",
                $"  Path: {info.SolutionPath}",
                "",
                $"Projects ({info.Projects.Count}):"
            };

            foreach (var project in info.Projects)
            {
                lines.Add($"  - {project}");
            }

            return McpOutputBounds.Limit(string.Join("\n", lines));
        });
    }
}
