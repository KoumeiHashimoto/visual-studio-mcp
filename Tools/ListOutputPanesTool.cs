using ModelContextProtocol.Server;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Lists all available Output Window panes in Visual Studio.
/// </summary>
[McpServerToolType]
public static class ListOutputPanesTool
{
    [McpServerTool]
    [Description("Returns a list of all available Output Window panes in Visual Studio (e.g., Build, Debug, Xamarin Diagnostics).")]
    public static string ListOutputPanes()
    {
        var panes = StaComDispatcher.Invoke(VisualStudioConnector.GetOutputPaneNames);

        if (panes.Count == 0)
        {
            return "No Visual Studio instance found or no output panes available.";
        }

        return McpOutputBounds.Limit($"Available Output Panes ({panes.Count}):\n" + string.Join("\n", panes.Select(p => $"  - {p}")));
    }
}
