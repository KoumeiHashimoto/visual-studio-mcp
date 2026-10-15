using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tools: Start/Stop debugging in Visual Studio.
/// </summary>
[McpServerToolType]
public static class DebugControlTool
{
    [McpServerTool]
    [Description("Starts debugging the current project (equivalent to F5). If already at a breakpoint, continues execution.")]
    public static string StartDebugging()
    {
        return VisualStudioConnector.StartDebugging();
    }

    [McpServerTool]
    [Description("Starts the current project without debugging (equivalent to Ctrl+F5).")]
    public static string StartWithoutDebugging()
    {
        return VisualStudioConnector.StartWithoutDebugging();
    }

    [McpServerTool]
    [Description("Stops the current debugging session.")]
    public static string StopDebugging()
    {
        return VisualStudioConnector.StopDebugging();
    }

    [McpServerTool]
    [Description("Gets the current debugger state (running, at breakpoint, design mode) and current location if at breakpoint.")]
    public static string GetDebuggerState(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        var state = VisualStudioConnector.GetDebuggerState();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonConvert.SerializeObject(state, Formatting.Indented);
        }

        return state.Message;
    }

    [McpServerTool]
    [Description("Gets up to 128 stack frames for the current thread while paused in the bound Visual Studio instance. Returns structured JSON and fails when the target is stale or running.")]
    public static string GetCallStack(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        return McpOutputBounds.Limit(JsonConvert.SerializeObject(
            StaComDispatcher.Invoke(VisualStudioConnector.GetDebuggerStack), Formatting.None));
    }
}
