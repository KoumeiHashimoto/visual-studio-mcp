using System.ComponentModel;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using ModelContextProtocol.Server;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Provides bounded breakpoint inspection and explicitly authorized breakpoint changes.
/// </summary>
[McpServerToolType]
public static class BreakpointManagementTool
{
    /// <summary>Lists breakpoints for the Visual Studio process bound to this server.</summary>
    [McpServerTool]
    [Description("Lists up to 25 breakpoints in the explicitly bound Visual Studio instance. IDs are one-based indexes from this snapshot and can change after edits. Returns structured JSON.")]
    public static string ListBreakpoints()
    {
        return Invoke(VisualStudioConnector.ListBreakpoints);
    }

    /// <summary>Adds a source or function breakpoint when mutation is explicitly authorized.</summary>
    [McpServerTool]
    [Description("Adds one breakpoint by source file and one-based line, or by function name. Set allowMutation=true to authorize the change. Conditions also require allowConditionEvaluation=true because the debugger may evaluate their expression. The result is structured JSON.")]
    public static string SetBreakpoint(
        [Description("Source file path. Supply this with a positive line, or use function instead.")] string? file = null,
        [Description("One-based source line, required with file. Use 0 for a function breakpoint.")] int line = 0,
        [Description("Function name for a function breakpoint. Supply this instead of file.")] string? function = null,
        [Description("Optional debugger condition expression. Requires allowConditionEvaluation=true.")] string? condition = null,
        [Description("Explicit authorization to add a breakpoint. Defaults to false.")] bool allowMutation = false,
        [Description("Explicit authorization for the debugger to evaluate a condition expression. Defaults to false.")] bool allowConditionEvaluation = false)
    {
        return Invoke(() => VisualStudioConnector.SetBreakpoint(file, line, function, condition, allowMutation, allowConditionEvaluation));
    }

    /// <summary>Removes a breakpoint after validating its current location against a fresh snapshot.</summary>
    [McpServerTool]
    [Description("Removes a breakpoint by its one-based ID from the latest list snapshot. Also supply its expectedFile and expectedLine or expectedFunction, expectedCondition, and allowMutation=true. Changed or stale entries fail safely. The result is structured JSON.")]
    public static string RemoveBreakpoint(
        [Description("One-based ID from the latest list_breakpoints response.")] int id,
        [Description("Expected source file from the latest list response, when removing a source breakpoint.")] string? expectedFile = null,
        [Description("Expected one-based source line from the latest list response.")] int expectedLine = 0,
        [Description("Expected function from the latest list response, when removing a function breakpoint.")] string? expectedFunction = null,
        [Description("Expected condition from the latest list response. Pass an empty string for no condition.")] string? expectedCondition = null,
        [Description("Explicit authorization to remove a breakpoint. Defaults to false.")] bool allowMutation = false)
    {
        return Invoke(() => VisualStudioConnector.RemoveBreakpoint(id, expectedFile, expectedLine, expectedFunction, expectedCondition, allowMutation));
    }

    private static string Invoke(Func<BreakpointToolResponse> operation)
    {
        try
        {
            return Serialize(StaComDispatcher.Invoke(operation));
        }
        catch (TimeoutException)
        {
            return Serialize(Error("com_timeout", "The Visual Studio COM operation exceeded its 15 second limit."));
        }
        catch (COMException ex)
        {
            return Serialize(Error("com_error", $"Visual Studio rejected the breakpoint operation (HRESULT 0x{ex.HResult:X8})."));
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024];
            return Serialize(Error("operation_failed", message));
        }
    }

    private static BreakpointToolResponse Error(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message
    };

    private static string Serialize(BreakpointToolResponse response) =>
        McpOutputBounds.Limit(JsonConvert.SerializeObject(response, Formatting.None));
}
