namespace VisualStudioMcpServer.Providers;

// ---- P13-04（#58）documents.list / editor.getContext / documents.read の応答本文（wire は camelCase）----
// 親 repository の Providers/DocumentProviderBodies.cs と同じ member 名を保つ。未知 member は契約違反として拒否される。

internal sealed record DocumentListEntryBody
{
    public string? DocumentId { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string MonikerAvailability { get; init; } = string.Empty;
    public string ProviderKind { get; init; } = string.Empty;
    public string ScopeClass { get; init; } = string.Empty;
    public bool? IsDirty { get; init; }
    public bool? IsReadOnly { get; init; }
    public string? BufferGeneration { get; init; }
    public bool IsVisibleTab { get; init; }
    public string? ContentType { get; init; }
}

internal sealed record DocumentsListResponseBody
{
    public string SnapshotId { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Offset { get; init; }
    public bool CaptureComplete { get; init; }
    public DocumentListEntryBody[] Documents { get; init; } = [];
    public string? NextCursor { get; init; }
}

internal sealed record EditorSelectionBody
{
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public bool IsReversed { get; init; }
}

internal sealed record EditorGetContextResponseBody
{
    public string ViewStatus { get; init; } = string.Empty;
    public string? DocumentId { get; init; }
    public string? Path { get; init; }
    public string ScopeClass { get; init; } = string.Empty;
    public bool InScope { get; init; }
    public string? ProviderKind { get; init; }
    public string? BufferGeneration { get; init; }
    public string? ContentType { get; init; }
    public int? CaretLine { get; init; }
    public int? CaretColumn { get; init; }
    public string SelectionKind { get; init; } = string.Empty;
    public EditorSelectionBody[] Selections { get; init; } = [];
    public bool SelectionsTruncated { get; init; }
    public string? SelectedText { get; init; }
    public string SelectedTextStatus { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public string? UnavailableReason { get; init; }
}

internal sealed record DocumentsReadResponseBody
{
    public string DocumentId { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string ContentSource { get; init; } = string.Empty;
    public string ProviderKind { get; init; } = string.Empty;
    public bool? IsDirty { get; init; }
    public bool? IsReadOnly { get; init; }
    public string BufferVersion { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public int TotalLines { get; init; }
    public int TotalLength { get; init; }
    public int StartLine { get; init; }
    public int ReturnedLines { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool Truncated { get; init; }
}

/// <summary>応答本文の構造と値域を検査する。値域外は provider の契約違反として tool で構造化エラーにする。</summary>
internal static class DocumentProviderBodyRules
{
    internal const int MaxListPage = 100;
    internal const int MaxSelections = 16;
    internal const int MaxSelectedTextCharacters = 4096;
    internal const int MaxReadCharacters = 32768;
    internal const int MaxPathCharacters = 2048;

    internal static bool TryParseList(string? body, out DocumentsListResponseBody value) =>
        TryParse(body, out value, static list =>
            ProviderProtocol.IsUuidN(list.SnapshotId) && list.Scope is "workspace" or "all" &&
            list.Total >= 0 && list.Offset >= 0 && list.Documents is { Length: <= MaxListPage } &&
            list.Offset + list.Documents.Length <= list.Total &&
            (list.NextCursor is null || list.NextCursor.Length is > 0 and <= 256) &&
            list.Documents.All(static entry => entry is not null && IsValidEntry(entry)));

    internal static bool TryParseContext(string? body, out EditorGetContextResponseBody value) =>
        TryParse(body, out value, static context =>
            context.ViewStatus is "focused" or "lastCodeView" or "none" &&
            ProviderBodyRules.IsOptionalToken(context.DocumentId, 128) && IsOptionalText(context.Path, MaxPathCharacters) &&
            IsScopeClass(context.ScopeClass) && IsOptionalText(context.ProviderKind, 32) &&
            ProviderBodyRules.IsOptionalToken(context.BufferGeneration, 64) && IsOptionalText(context.ContentType, 128) &&
            (context.CaretLine is null) == (context.CaretColumn is null) &&
            (context.CaretLine is null or >= 1) && (context.CaretColumn is null or >= 1) &&
            context.SelectionKind is "empty" or "single" or "multiple" or "box" or "unavailable" &&
            context.Selections is { Length: <= MaxSelections } &&
            context.Selections.All(static selection => selection is not null && selection.StartLine >= 1 &&
                selection.StartColumn >= 1 && selection.EndLine >= selection.StartLine && selection.EndColumn >= 1) &&
            (context.SelectedText is null || context.SelectedText.Length <= MaxSelectedTextCharacters) &&
            context.SelectedTextStatus is "notRequested" or "included" or "empty" or "outOfScope" or
                "unsupportedSelection" or "unavailable" &&
            (context.SelectedText is null) == (context.SelectedTextStatus != "included") &&
            IsOptionalText(context.UnavailableReason, 128));

    internal static bool TryParseRead(string? body, out DocumentsReadResponseBody value) =>
        TryParse(body, out value, static read =>
            ProviderBodyRules.IsToken(read.DocumentId, 128) && IsOptionalText(read.Path, MaxPathCharacters) &&
            read.ContentSource == "editorBuffer" && read.ProviderKind == "textBuffer" &&
            ProviderBodyRules.IsToken(read.BufferVersion, 64) && IsOptionalText(read.ContentType, 128) &&
            read.TotalLines >= 1 && read.TotalLength >= 0 && read.StartLine >= 1 && read.StartLine <= read.TotalLines &&
            read.ReturnedLines >= 0 && read.EndLine >= read.StartLine && read.EndLine <= read.TotalLines + 1 &&
            read.EndColumn >= 1 && read.Text is not null && read.Text.Length <= MaxReadCharacters);

    private static bool IsValidEntry(DocumentListEntryBody entry) =>
        ProviderBodyRules.IsOptionalToken(entry.DocumentId, 128) && IsOptionalText(entry.Path, MaxPathCharacters) &&
        IsOptionalText(entry.Name, 260) &&
        entry.MonikerAvailability is "available" or "untitled" or "nonFile" or "unavailable" &&
        entry.ProviderKind is "textBuffer" or "projection" or "nonText" or "pendingInitialization" or "unavailable" &&
        IsScopeClass(entry.ScopeClass) && ProviderBodyRules.IsOptionalToken(entry.BufferGeneration, 64) &&
        IsOptionalText(entry.ContentType, 128);

    private static bool IsScopeClass(string? value) => value is "workspace" or "external" or "untitled" or "unknown";

    private static bool IsOptionalText(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool TryParse<T>(string? body, out T value, Func<T, bool> isValid) where T : class
    {
        value = null!;
        if (string.IsNullOrEmpty(body)) return false;
        try { value = ProviderProtocol.ParseBody<T>(body); }
        catch (ProviderProtocolException) { return false; }
        return isValid(value);
    }
}
