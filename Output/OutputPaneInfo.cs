namespace VisualStudioMcpServer.Output;

/// <summary>
/// Identifies an Output Window pane without relying on its localized display name.
/// </summary>
internal sealed record OutputPaneInfo(string? Guid, string Name, string Availability);
