using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP Tools: call stack inspection. Debugger state, start, and stop tools live in ExecutionHttpTools so both transports share one guarded definition.
/// </summary>
[McpServerToolType]
public static class DebugControlTool
{
    [McpServerTool]
    [Description("Gets up to 128 stack frames for the current thread or a specified thread while paused in the bound Visual Studio instance. Supplying threadId is read-only and does not change Visual Studio selection. Locals and expression evaluation remain bound to CurrentThreadId and require selecting another thread first. Returns structured JSON.")]
    public static string GetCallStack(
        [Description("Optional thread ID from the latest list_debugger_threads response. Omit to inspect the current thread without changing its selection.")]
        int? threadId = null,
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text")
    {
        return McpOutputBounds.Limit(JsonConvert.SerializeObject(
            StaComDispatcher.Invoke(() => VisualStudioConnector.GetDebuggerStack(threadId)), Formatting.None));
    }
}
