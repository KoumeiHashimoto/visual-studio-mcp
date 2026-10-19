namespace VisualStudioMcpServer.Output;

/// <summary>
/// Bounded text returned from one exact Output Window pane identity.
/// </summary>
internal sealed record OutputPaneTextSnapshot(string PaneGuid, string Text, int ReturnedLines, int OmittedLines, bool Truncated);

/// <summary>DTE から読んだ末尾テキストを UTF-16 文字数の上限に収める。</summary>
internal static class OutputSnapshotText
{
    /// <summary>
    /// EnvDTE の CharLeft は CRLF を 1 文字として数えるため、取得したテキストが上限を超えることがある。
    /// 末尾を優先して先頭を切り、CRLF の途中や surrogate pair の途中から始めない。
    /// </summary>
    internal static string BoundTail(string text, int maxCharacters, out bool trimmed)
    {
        trimmed = false;
        if (maxCharacters < 0) maxCharacters = 0;
        if (text.Length > maxCharacters)
        {
            text = text[^maxCharacters..];
            trimmed = true;
        }
        if (text.Length > 0 && text[0] == '\n' && trimmed) text = text[1..];
        if (text.Length > 0 && char.IsLowSurrogate(text[0])) { text = text[1..]; trimmed = true; }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) { text = text[..^1]; trimmed = true; }
        return text;
    }
}
