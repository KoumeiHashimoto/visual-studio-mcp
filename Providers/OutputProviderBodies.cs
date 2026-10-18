namespace VisualStudioMcpServer.Providers;

// ---- P13-05b（#60）output.read の応答本文（wire は camelCase）----
// 親 repository の Providers/OutputReadCore.cs と同じ member 名・順序を保つ。未知 member は契約違反として拒否される。

internal sealed record OutputReadResponseBody
{
    public string PaneGuid { get; init; } = string.Empty;
    public string? PaneName { get; init; }
    public string Mode { get; init; } = string.Empty;
    public bool IncrementalSupported { get; init; }
    public string? ProviderCursor { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool Gap { get; init; }
    public bool Reset { get; init; }
    public bool Truncated { get; init; }
    public int OmittedCharacters { get; init; }
    public int OmittedLines { get; init; }
    public int ReturnedLines { get; init; }
    public int BufferLength { get; init; }
    public int BufferVersion { get; init; }
    public string ContinuityReason { get; init; } = string.Empty;
}

internal static class OutputProviderBodyRules
{
    internal const int MaxCharacters = 32768;

    internal static bool TryParse(string? body, out OutputReadResponseBody value)
    {
        value = null!;
        if (string.IsNullOrEmpty(body)) return false;
        try { value = ProviderProtocol.ParseBody<OutputReadResponseBody>(body); }
        catch (ProviderProtocolException) { return false; }
        return Guid.TryParseExact(value.PaneGuid, "D", out _) && (value.PaneName is null || value.PaneName.Length <= 256) &&
            value.Mode is "delta" or "snapshot" && (value.Mode != "delta" || value.IncrementalSupported) &&
            (value.ProviderCursor is null || value.ProviderCursor.Length is > 0 and <= 256) &&
            (value.ProviderCursor is null) == !value.IncrementalSupported &&
            value.Text is not null && value.Text.Length <= MaxCharacters && value.OmittedCharacters >= 0 &&
            value.OmittedLines >= 0 && value.ReturnedLines >= 0 && value.BufferLength >= 0 && value.BufferVersion >= 0 &&
            value.ContinuityReason.Length is > 0 and <= 64 && value.ContinuityReason.All(static character => character is >= 'a' and <= 'z' or '_') &&
            (value.Mode != "delta" || (!value.Gap && !value.Reset && value.OmittedCharacters == 0));
    }
}
