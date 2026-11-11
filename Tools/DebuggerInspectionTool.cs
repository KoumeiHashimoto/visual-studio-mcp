using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ComponentModel.DataAnnotations;
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
    public static string GetDebuggerStack(int? threadId = null) => Invoke(() =>
    {
        var response = VisualStudioConnector.GetDebuggerStack(threadId);
        if (response.Success)
        {
            // v1.4: 後続の選択・locals・式評価が照合できるよう、読み取った break の世代を返す。
            var (sequence, observation) = BreakGeneration.Current(Providers.ProviderRuntime.ObservationStore, settle: true);
            response.EventSequence = sequence;
            response.DebugSessionId = observation?.DebugSessionId;
        }
        return response;
    });

    /// <summary>Reads the break state, top frames and locals of frame 1 in one call.</summary>
    [McpServerTool]
    [Description("Reads the paused break in one call: state, stop reason and exception (description only with allowSensitiveRead=true), top stack frames of the current thread and the locals of frame 1. Frame and local shapes match get_call_stack and get_debugger_locals; EventSequence is the break generation to pass as expectedEventSequence to select_debugger_frame, get_debugger_locals and evaluate_expression. If the debugger moves during the read, Changed and Partial are true and no frames are returned. Never evaluates expressions or changes the selection.")]
    public static string GetBreakSnapshot(
        [Description("Maximum stack frames, clamped to 1 through 16. Defaults to 16.")] int maxFrames = 16,
        [Description("Whether to read locals of frame 1 (member expansion is disabled). Defaults to true.")] bool includeLocals = true,
        [Description("Maximum locals, clamped to 0 through 40. Defaults to 40.")] int maxLocals = 40,
        [Description("When true, an exception stop may include its bounded description (sensitive). Defaults to false.")] bool allowSensitiveRead = false)
    {
        try
        {
            var snapshot = StaComDispatcher.Invoke(() => BreakGeneration.Build(maxFrames, includeLocals, maxLocals, allowSensitiveRead,
                Providers.ProviderRuntime.ObservationStore, () => VisualStudioConnector.GetDebuggerStack(null),
                (frame, total, thread, max) => VisualStudioConnector.GetDebuggerLocals(1, frame.Function, frame.Module, total, thread, 0, max, false)));
            return McpOutputBounds.Limit(JsonConvert.SerializeObject(snapshot, Formatting.None));
        }
        catch (TimeoutException) { return SerializeSnapshotError("com_timeout", "The Visual Studio COM operation exceeded its 15 second limit."); }
        catch (COMException ex) { return SerializeSnapshotError("com_error", $"Visual Studio rejected the break snapshot request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return SerializeSnapshotError("operation_failed", ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024]); }
    }

    private static string SerializeSnapshotError(string code, string message) =>
        JsonConvert.SerializeObject(new BreakSnapshotResponse { Success = false, Code = code, Message = message }, Formatting.None);

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
        [Description("One-based frame index from the latest get_call_stack response (1-128).")]
        [Range(1, 128)] int index,
        [Description("Expected function name from that frame.")] string expectedFunction,
        [Description("Expected module name from that frame; pass an empty string when the debugger reports no module.")] string expectedModule,
        [Description("TotalCount from the latest get_call_stack response.")] int totalFrameCount,
        [Description("Optional EventSequence from get_call_stack or get_break_snapshot. When supplied, the call is refused with stale_break_generation if the debugger moved, or generation_unavailable without the VSIX provider.")] long? expectedEventSequence = null) =>
        Guarded(expectedEventSequence) ?? Invoke(() => VisualStudioConnector.SelectDebuggerFrame(index, expectedFunction, expectedModule, totalFrameCount));

    /// <summary>Reads locals from a current, snapshot-validated stack frame.</summary>
    [McpServerTool]
    [Description("Reads locals for a frame from the latest get_call_stack response. Results are bounded to 40 values, depth 3, and 256 characters per field. Member expansion is disabled by default because Natvis or data members can invoke debugger evaluation; opt in with allowMemberExpansion=true. Returns structured JSON.")]
    public static string GetDebuggerLocals(
        [Description("One-based frame index from the latest get_call_stack response (1-128).")]
        [Range(1, 128)] int frameIndex,
        [Description("Expected function name from that frame.")] string expectedFunction,
        [Description("Expected module name from that frame; pass an empty string when the debugger reports no module.")] string expectedModule,
        [Description("TotalCount from the latest get_call_stack response.")] int totalFrameCount,
        [Description("CurrentThreadId from the latest get_call_stack response.")] int threadId,
        [Description("Maximum nested member depth, clamped to 0 through 3.")] int maxDepth = 1,
        [Description("Maximum total locals and member values, clamped to 1 through 40.")] int maxItems = 40,
        [Description("Explicit permission to expand member values. Defaults to false.")] bool allowMemberExpansion = false,
        [Description("Optional EventSequence from get_call_stack or get_break_snapshot. When supplied, the call is refused with stale_break_generation if the debugger moved, or generation_unavailable without the VSIX provider.")] long? expectedEventSequence = null) =>
        Guarded(expectedEventSequence) ?? Invoke(() => VisualStudioConnector.GetDebuggerLocals(frameIndex, expectedFunction, expectedModule,
            totalFrameCount, threadId, maxDepth, maxItems, allowMemberExpansion));

    /// <summary>v1.4: expectedEventSequence の照合。通れば null、拒否なら直列化した失敗応答。</summary>
    private static string? Guarded(long? expectedEventSequence) =>
        BreakGeneration.Guard(expectedEventSequence, Providers.ProviderRuntime.ObservationStore) is { } refusal
            ? SerializeError(refusal.Code, refusal.Message) : null;

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
