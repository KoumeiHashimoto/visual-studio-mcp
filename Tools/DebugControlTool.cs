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
    [Description("Legacy entry point. Returns requires_vsix_provider without dispatch until the provider-bound execution coordinator is available.")]
    public static string StartDebugging()
    {
        return VisualStudioConnector.StartDebugging();
    }

    [McpServerTool]
    [Description("Legacy entry point. Returns requires_vsix_provider without dispatch until the provider-bound execution coordinator is available.")]
    public static string StartWithoutDebugging()
    {
        return VisualStudioConnector.StartWithoutDebugging();
    }

    [McpServerTool]
    [Description("Legacy entry point. Returns requires_vsix_provider without dispatch until an exact-session provider-bound stop is available.")]
    public static string StopDebugging()
    {
        return VisualStudioConnector.StopDebugging();
    }

    [McpServerTool]
    [Description("Gets debugger state only from an explicitly bound Visual Studio process and matching absolute solution path. With the VSIX provider, JSON adds session, process-set, startup snapshot and EventSequence fields; operationId returns only that retained launch/stop operation.")]
    public static Task<string> GetDebuggerState(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text",
        [Description("Required absolute path of the solution expected in the bound Visual Studio instance.")]
        string? expectedSolutionPath = null,
        [Description("Optional operation ID from start_debugging, start_without_debugging, or stop_debugging. Unknown IDs are never replaced by another operation.")]
        string? operationId = null,
        CancellationToken cancellationToken = default) =>
        ExecutionToolImplementation.GetDebuggerStateAsync(format, expectedSolutionPath, operationId, cancellationToken);

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
