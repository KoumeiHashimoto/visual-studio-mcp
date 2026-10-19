using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Lists all available Output Window panes in Visual Studio.
/// </summary>
[McpServerToolType]
public static class ListOutputPanesTool
{
    [McpServerTool]
    [Description("Returns available Output Window panes. Use format='json' to include stable pane GUIDs for exact selection.")]
    public static string ListOutputPanes(
        [Description("Output format: 'text' preserves the legacy list; 'json' includes pane GUID, display name, and availability. Defaults to 'text'.")]
        string format = "text")
    {
        return ReadOnlyToolInvocation.Invoke("list_output_panes", () =>
        {
            var panes = VisualStudioConnector.GetOutputPanes();

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonConvert.SerializeObject(panes);
                return json.Length <= 64 * 1024
                    ? json
                    : "{\"Success\":false,\"Code\":\"response_too_large\",\"Message\":\"Output pane metadata exceeded the response limit.\"}";
            }

            if (panes.Count == 0)
            {
                return "No Visual Studio instance found or no output panes available.";
            }

            return McpOutputBounds.Limit($"Available Output Panes ({panes.Count}):\n" + string.Join("\n", panes.Select(static pane => $"  - {pane.Name}")));
        });
    }
}
