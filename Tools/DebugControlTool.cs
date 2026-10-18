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
    [Description("Gets debugger state only from an explicitly bound Visual Studio process and matching absolute solution path.")]
    public static string GetDebuggerState(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text",
        [Description("Required absolute path of the solution expected in the bound Visual Studio instance.")]
        string? expectedSolutionPath = null)
    {
        DebuggerState state;
        try { state = StaComDispatcher.Invoke(() => VisualStudioConnector.GetDebuggerState(expectedSolutionPath), TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { state = DebuggerState.Unavailable("com_timeout", "The bound Visual Studio debugger state did not respond within the inspection deadline."); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            state = DebuggerState.Unavailable("com_error", $"Visual Studio rejected debugger inspection (HRESULT 0x{ex.HResult:X8}).");
        }
        catch { state = DebuggerState.Unavailable("target_unavailable", "The bound Visual Studio debugger state is unavailable."); }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return SerializeDebuggerStateBounded(state);
        }

        return state.Message.Length <= 4096 ? state.Message : state.Message[..4096];
    }

    private static string SerializeDebuggerStateBounded(DebuggerState state)
    {
        const int maxBytes = 64 * 1024;
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;
        var bounded = DebuggerState.Unavailable("response_too_large", "Debugger state exceeded the response size limit.", state.TargetProcessId,
            state.SolutionPath is { Length: > 4096 } ? state.SolutionPath[..4096] : state.SolutionPath);
        bounded.Mode = state.Mode.Length <= 64 ? state.Mode : state.Mode[..64];
        bounded.State = state.State.Length <= 64 ? state.State : state.State[..64];
        json = JsonConvert.SerializeObject(bounded, Formatting.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
            throw new InvalidOperationException("Debugger state result schema exceeds response size limit.");
        return json;
    }

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
