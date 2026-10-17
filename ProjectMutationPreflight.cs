namespace VisualStudioMcpServer;

internal static class ProjectMutationPreflight
{
    internal static string? GetError(IEnumerable<bool?> savedStates)
    {
        foreach (var saved in savedStates)
        {
            if (saved is null) return "unsupported_state";
            if (!saved.Value) return "document_dirty";
        }
        return null;
    }
}
