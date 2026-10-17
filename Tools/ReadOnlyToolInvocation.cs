using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace VisualStudioMcpServer.Tools;

/// <summary>Contains failures from read-only tools without exposing exception messages or stacks.</summary>
internal static class ReadOnlyToolInvocation
{
    /// <summary>Checks DTE availability and runs the complete tool operation on the bounded COM STA.</summary>
    public static string Invoke(string toolName, Func<string> operation)
    {
        try
        {
            return StaComDispatcher.Invoke(() =>
            {
                if (VisualStudioConnector.GetActiveVS() == null)
                    return Error("dte_unavailable", "The target Visual Studio DTE is unavailable. Verify that the intended Visual Studio instance is running and restart its MCP server if needed.");

                return operation();
            });
        }
        catch (Exception ex)
        {
            // Only fixed tool identifiers, exception types, and HRESULTs belong in diagnostics.
            try { Console.Error.WriteLine($"Read-only tool {toolName} failed: {ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}."); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            return ex switch
            {
                TimeoutException => Error("com_timeout", "The Visual Studio COM operation exceeded its 15 second limit. Check whether Visual Studio is busy and retry."),
                InvalidComObjectException => Error("dte_disconnected", "The target Visual Studio DTE connection was lost. Restart its MCP server and reconnect."),
                COMException when ex.HResult is unchecked((int)0x80010108) or unchecked((int)0x800401FD) or unchecked((int)0x800706BA) =>
                    Error("dte_disconnected", "The target Visual Studio DTE connection was lost. Restart its MCP server and reconnect."),
                COMException when ex.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A) =>
                    Error("com_busy", "Visual Studio rejected the read-only request after bounded COM retries. Dismiss any modal dialog or wait until Visual Studio is ready, then retry."),
                COMException => Error("com_error", $"The Visual Studio COM operation failed (HRESULT 0x{ex.HResult:X8}). Verify the target instance and retry."),
                _ => Error("operation_failed", "The read-only request failed unexpectedly. Check the server diagnostics for the tool name, exception type, and HRESULT, then retry.")
            };
        }
    }

    /// <summary>Returns a bounded, machine-readable failure using the debugger tool response conventions.</summary>
    public static string Error(string code, string message) =>
        McpOutputBounds.Limit(JsonConvert.SerializeObject(new { Success = false, Code = code, Message = message }, Formatting.None));
}
