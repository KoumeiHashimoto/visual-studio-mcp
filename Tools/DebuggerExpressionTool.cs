using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Provides explicit, bounded evaluation of a debugger expression.
/// </summary>
[McpServerToolType]
public static class DebuggerExpressionTool
{
    /// <summary>Evaluates an expression in a validated paused frame.</summary>
    [McpServerTool]
    [Description("Evaluates an expression on a frame from the latest get_call_stack response. Requires allowEvaluation=true. Automatic property/function evaluation is disabled unless allowSideEffects=true. Output and evaluation time are bounded; returns structured JSON.")]
    public static string EvaluateExpression(
        [Description("Expression text, limited to 1024 characters.")] string expression,
        [Description("One-based frame index from the latest get_call_stack response (1-128).")]
        [Range(1, 128)] int frameIndex,
        [Description("Expected function name from that frame.")] string expectedFunction,
        [Description("Expected module name from that frame; pass an empty string when the debugger reports no module.")] string expectedModule,
        [Description("TotalCount from the latest get_call_stack response.")] int totalFrameCount,
        [Description("CurrentThreadId from the latest get_call_stack response.")] int threadId,
        [Description("Explicit authorization for expression evaluation. Defaults to false.")] bool allowEvaluation = false,
        [Description("Explicit permission for automatic property and function evaluation. Defaults to false.")] bool allowSideEffects = false) =>
        Invoke(() => VisualStudioConnector.EvaluateDebuggerExpression(expression, frameIndex, expectedFunction,
            expectedModule, totalFrameCount, threadId, allowEvaluation, allowSideEffects));

    private static string Invoke(Func<DebuggerExpressionResponse> operation)
    {
        try
        {
            return McpOutputBounds.Limit(JsonConvert.SerializeObject(StaComDispatcher.Invoke(operation), Formatting.None));
        }
        catch (TimeoutException)
        {
            return SerializeError("com_timeout", "The Visual Studio COM request exceeded its 15 second dispatcher limit.");
        }
        catch (COMException ex)
        {
            return SerializeError("com_error", $"Visual Studio rejected the debugger expression request (HRESULT 0x{ex.HResult:X8}).");
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024];
            return SerializeError("operation_failed", message);
        }
    }

    private static string SerializeError(string code, string message) =>
        JsonConvert.SerializeObject(new DebuggerExpressionResponse
        {
            Success = false,
            Code = code,
            Message = message
        }, Formatting.None);
}
