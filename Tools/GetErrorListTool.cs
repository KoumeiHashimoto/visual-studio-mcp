using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tool: Gets all items from the Visual Studio Error List.
/// </summary>
[McpServerToolType]
public static class GetErrorListTool
{
    [McpServerTool]
    [Description("Returns all errors and warnings currently shown in the Visual Studio Error List window.")]
    public static string GetErrorList(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        var errors = StaComDispatcher.Invoke(VisualStudioConnector.GetErrorListItems);

        if (errors.Count == 0)
        {
            return "No errors or warnings in the Error List.";
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return McpOutputBounds.Limit(JsonConvert.SerializeObject(errors, Formatting.Indented));
        }

        // Text format
        var grouped = errors.GroupBy(e => e.Severity);
        var lines = new List<string> { $"Error List ({errors.Count} items):", "" };

        foreach (var group in grouped.OrderBy(g => g.Key))
        {
            lines.Add($"=== {group.Key}s ({group.Count()}) ===");
            foreach (var item in group)
            {
                lines.Add(item.ToString());
            }
            lines.Add("");
        }

        return McpOutputBounds.Limit(string.Join("\n", lines));
    }
}
