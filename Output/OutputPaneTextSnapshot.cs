namespace VisualStudioMcpServer.Output;

/// <summary>
/// Bounded text returned from one exact Output Window pane identity.
/// </summary>
internal sealed record OutputPaneTextSnapshot(string PaneGuid, string Text, int ReturnedLines, int OmittedLines, bool Truncated);
