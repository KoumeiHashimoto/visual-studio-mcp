using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Provides explicitly authorized pause, resume, and stepping controls.
/// </summary>
[McpServerToolType]
public static class DebuggerControlTool
{
    /// <summary>Performs one bounded control operation on the explicitly bound Visual Studio debugger.</summary>
    [McpServerTool]
    [Description("Pauses, continues, or steps the debugger bound to the current Visual Studio process. Requires allowDebuggerControl=true. Completion waits are bounded and respect request cancellation; returns structured JSON.")]
    public static async Task<string> ControlDebugger(
        [Description("One of pause, continue, step_into, step_over, or step_out.")] string action,
        [Description("Explicit authorization for execution control. Defaults to false.")] bool allowDebuggerControl = false,
        [Description("Maximum time to observe completion, clamped to 1,000 through 10,000 ms.")] int timeoutMilliseconds = 5000,
        CancellationToken cancellationToken = default)
    {
        var result = await VisualStudioConnector.ControlDebuggerAsync(
            action, allowDebuggerControl, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        return McpOutputBounds.Limit(JsonConvert.SerializeObject(result, Formatting.None));
    }
}
