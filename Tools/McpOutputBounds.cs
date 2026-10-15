namespace VisualStudioMcpServer.Tools;

/// <summary>
/// MCP応答が一定サイズを超えないよう制限します。
/// </summary>
internal static class McpOutputBounds
{
    private const int MaximumCharacters = 65536;

    /// <summary>
    /// 応答文字列を最大64Ki文字に制限します。
    /// </summary>
    /// <param name="value">MCP応答。</param>
    /// <returns>上限内の応答。</returns>
    public static string Limit(string value)
    {
        if (value.Length <= MaximumCharacters) return value;
        return value[..MaximumCharacters] + "\n[応答を64Ki文字で切り詰めました]";
    }
}
