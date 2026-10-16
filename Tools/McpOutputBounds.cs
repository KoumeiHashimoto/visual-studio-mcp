namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Keeps MCP responses within a fixed size limit.
/// </summary>
internal static class McpOutputBounds
{
    private const int MaximumCharacters = 65536;

    /// <summary>
    /// Limits response strings to 64 Ki characters.
    /// </summary>
    /// <param name="value">The MCP response.</param>
    /// <returns>The bounded response.</returns>
    public static string Limit(string value)
    {
        if (value.Length <= MaximumCharacters) return value;
        return value[..MaximumCharacters] + "\n[Response truncated at 64 Ki characters]";
    }
}
