using ModelContextProtocol.Server;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Reads content from a specific Output Window pane.
/// Supports truncation to prevent token overload.
/// </summary>
[McpServerToolType]
public static class ReadOutputPaneTool
{
    [McpServerTool]
    [Description("Reads the content of a specific Output Window pane in Visual Studio. Use ListOutputPanes first to see available panes.")]
    public static string ReadOutputPane(
        [Description("The name (or partial name) of the Output Window pane to read (e.g., 'Build', 'Debug')")]
        string paneName,

        [Description("Maximum number of lines to return from the end of the pane. Defaults to 50 and is capped at 2000 lines.")]
        int maxLines = 50)
    {
        if (string.IsNullOrWhiteSpace(paneName))
        {
            return "Error: paneName is required. Use ListOutputPanes to see available panes.";
        }

        var content = StaComDispatcher.Invoke(() => VisualStudioConnector.ReadOutputPane(paneName, maxLines));

        if (content == null)
        {
            return $"Error: Could not find output pane matching '{paneName}'. Use ListOutputPanes to see available panes.";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return $"Output pane '{paneName}' is empty.";
        }

        return McpOutputBounds.Limit(content);
    }
}
