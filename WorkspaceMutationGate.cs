namespace VisualStudioMcpServer;

/// <summary>Process-wide serialization for build and solution file mutations.</summary>
internal static class WorkspaceMutationGate
{
    internal static readonly SemaphoreSlim Shared = new(1, 1);
}
