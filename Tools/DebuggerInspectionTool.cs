using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Provides bounded inspection and selection of paused debugger state.
/// </summary>
[McpServerToolType]
public static class DebuggerInspectionTool
{
    /// <summary>Lists threads in the bound paused Visual Studio process.</summary>
    [McpServerTool]
    [Description("Lists up to 64 debugger threads in the currently paused process. Returns structured JSON.")]
    public static string ListDebuggerThreads() => Invoke(VisualStudioConnector.ListDebuggerThreads);

    /// <summary>Lists stack frames in the bound paused debugger.</summary>
    public static string GetDebuggerStack() => Invoke(VisualStudioConnector.GetDebuggerStack);

    /// <summary>Selects a thread by its current operating system thread ID.</summary>
    [McpServerTool]
    [Description("Selects a thread from the latest list_debugger_threads response. The request is revalidated against the current paused process.")]
    public static string SelectDebuggerThread(
        [Description("Thread ID from the latest list_debugger_threads response.")] int threadId) =>
        Invoke(() => VisualStudioConnector.SelectDebuggerThread(threadId));

    /// <summary>Selects a frame after checking its current function name.</summary>
    [McpServerTool]
    [Description("Selects a stack frame from the latest get_call_stack response. The frame function is checked again to reject a stale index.")]
    public static string SelectDebuggerFrame(
        [Description("One-based frame index from the latest get_call_stack response.")] int index,
        [Description("Expected function name from that frame.")] string expectedFunction,
        [Description("Expected module name from that frame; pass an empty string when the debugger reports no module.")] string expectedModule,
        [Description("TotalCount from the latest get_call_stack response.")] int totalFrameCount) =>
        Invoke(() => VisualStudioConnector.SelectDebuggerFrame(index, expectedFunction, expectedModule, totalFrameCount));

    /// <summary>Reads locals from a current, snapshot-validated stack frame.</summary>
    [McpServerTool]
    [Description("Reads locals for a frame from the latest get_call_stack response. Results are bounded to 40 values, depth 3, and 256 characters per field. Member expansion is disabled by default because Natvis or data members can invoke debugger evaluation; opt in with allowMemberExpansion=true. Returns structured JSON.")]
    public static string GetDebuggerLocals(
        [Description("One-based frame index from the latest get_call_stack response.")] int frameIndex,
        [Description("Expected function name from that frame.")] string expectedFunction,
        [Description("Expected module name from that frame; pass an empty string when the debugger reports no module.")] string expectedModule,
        [Description("TotalCount from the latest get_call_stack response.")] int totalFrameCount,
        [Description("CurrentThreadId from the latest get_call_stack response.")] int threadId,
        [Description("Maximum nested member depth, clamped to 0 through 3.")] int maxDepth = 1,
        [Description("Maximum total locals and member values, clamped to 1 through 40.")] int maxItems = 40,
        [Description("Explicit permission to expand member values. Defaults to false.")] bool allowMemberExpansion = false) =>
        Invoke(() => VisualStudioConnector.GetDebuggerLocals(frameIndex, expectedFunction, expectedModule,
            totalFrameCount, threadId, maxDepth, maxItems, allowMemberExpansion));

    private static string Invoke(Func<DebuggerInspectionResponse> operation)
    {
        try
        {
            return McpOutputBounds.Limit(JsonConvert.SerializeObject(StaComDispatcher.Invoke(operation), Formatting.None));
        }
        catch (TimeoutException)
        {
            return SerializeError("com_timeout", "The Visual Studio COM operation exceeded its 15 second limit.");
        }
        catch (COMException ex)
        {
            return SerializeError("com_error", $"Visual Studio rejected the debugger inspection request (HRESULT 0x{ex.HResult:X8}).");
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024];
            return SerializeError("operation_failed", message);
        }
    }

    private static string SerializeError(string code, string message) =>
        JsonConvert.SerializeObject(new DebuggerInspectionResponse
        {
            Success = false,
            Code = code,
            Message = message
        }, Formatting.None);
}
