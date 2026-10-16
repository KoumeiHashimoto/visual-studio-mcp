namespace VisualStudioMcpServer;

/// <summary>
/// Recognizes debugger transitions even when a run/break cycle finishes between polls.
/// </summary>
internal static class DebuggerControlCompletion
{
    /// <summary>Tests observed state and events without relying on unavailable source coordinates.</summary>
    internal static bool HasCompleted(string action, string mode, bool sawRun, bool sawBreak, bool sawDesign) => action switch
    {
        "pause" => mode == "dbgBreakMode" && sawBreak,
        "continue" => mode == "dbgRunMode" ||
            (mode == "dbgBreakMode" && sawRun && sawBreak) ||
            (mode == "dbgDesignMode" && sawDesign),
        _ => mode == "dbgBreakMode" && sawBreak
    };
}
