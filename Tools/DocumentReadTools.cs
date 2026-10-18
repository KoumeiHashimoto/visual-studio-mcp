using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// P13-04（#58）の live document read。VSIX provider だけが RDT と editor buffer を読めるため、provider がない場合は
/// requires_vsix_provider を返し、disk 読み取りや DTE で代用しない。どの経路も保存・再読込・activation を起こさない。
/// </summary>
[McpServerToolType]
public static class DocumentReadTools
{
    internal const int MaximumPageSize = 100;
    internal const int MaximumSelectedTextCharacters = 4096;
    internal const int MaximumLineCount = 500;
    internal const int MaximumReadCharacters = 32 * 1024;

    [McpServerTool]
    [Description("Lists Running Document Table entries from the bound Visual Studio instance as a bounded snapshot with opaque document IDs, dirty/read-only state, buffer generation, and tab visibility. RDT entries are not identical to visible tabs. Requires the VSIX provider; reads never save, open, or activate documents.")]
    public static async Task<string> ListOpenDocuments(
        [Description("'workspace' (default) returns registered project items and the active Open Folder root only; 'all' explicitly includes external and untitled documents.")]
        string scope = "workspace",
        [Description("Page size from 1 to 100, default 100.")]
        int pageSize = 100,
        [Description("Opaque cursor from a previous page of the same snapshot and scope.")]
        string? cursor = null)
    {
        if (scope is not ("workspace" or "all"))
            return ProviderToolResults.Error("invalid_scope", "scope must be 'workspace' or 'all'.");
        if (pageSize is < 1 or > MaximumPageSize)
            return ProviderToolResults.Error("invalid_page_size", $"pageSize must be between 1 and {MaximumPageSize}.");
        if (cursor is not null && cursor.Length is 0 or > 256)
            return ProviderToolResults.Error("invalid_cursor", "cursor must be a non-empty opaque value of at most 256 characters.");

        var outcome = await ProviderReadGateway.ReadAsync("documents.list", target => new DocumentsListRequestBody
        {
            Target = target,
            Scope = scope,
            PageSize = pageSize,
            Cursor = cursor
        }).ConfigureAwait(false);
        if (!outcome.IsOk) return ProviderToolResults.FromOutcome(outcome);
        if (!DocumentProviderBodyRules.TryParseList(outcome.Body, out var body) || body.Scope != scope ||
            body.Documents.Length > pageSize)
            return ProviderToolResults.InvalidProviderResponse(outcome);
        return ProviderToolResults.Success(body, outcome, result => result["Returned"] = body.Documents.Length);
    }

    [McpServerTool]
    [Description("Returns the focused or last active code view in the bound Visual Studio instance: document ID, buffer generation, one-based UTF-16 caret and end-exclusive selection ranges, and content type. Selected text is opt-in and omitted for documents outside the workspace scope. Requires the VSIX provider; never changes focus or selection.")]
    public static async Task<string> GetEditorContext(
        [Description("Include the primary selection text. Defaults to false.")]
        bool includeSelectedText = false,
        [Description("Maximum selected-text UTF-16 code units, from 0 to 4096, default 4096.")]
        int maxCharacters = MaximumSelectedTextCharacters)
    {
        if (maxCharacters is < 0 or > MaximumSelectedTextCharacters)
            return ProviderToolResults.Error("invalid_max_characters",
                $"maxCharacters must be between 0 and {MaximumSelectedTextCharacters}.");

        var outcome = await ProviderReadGateway.ReadAsync("editor.getContext", target => new EditorGetContextRequestBody
        {
            Target = target,
            IncludeSelectedText = includeSelectedText,
            MaxCharacters = maxCharacters
        }).ConfigureAwait(false);
        if (!outcome.IsOk) return ProviderToolResults.FromOutcome(outcome);
        if (!DocumentProviderBodyRules.TryParseContext(outcome.Body, out var body) ||
            (body.SelectedText?.Length ?? 0) > maxCharacters ||
            (!includeSelectedText && body.SelectedTextStatus != "notRequested"))
            return ProviderToolResults.InvalidProviderResponse(outcome);
        return ProviderToolResults.Success(body, outcome);
    }

    [McpServerTool]
    [Description("Reads a bounded line range from one immutable live editor-buffer snapshot, including unsaved changes. Lines and columns are one-based UTF-16 code units and the end position is exclusive; surrogate pairs are never split. A stale expectedBufferVersion returns document_changed. Requires the VSIX provider; never saves or reloads.")]
    public static async Task<string> ReadDocument(
        [Description("Opaque document ID from list_open_documents or get_editor_context.")]
        string documentId,
        [Description("Optional buffer version from a previous response. A different current version returns document_changed instead of mixing revisions.")]
        string? expectedBufferVersion = null,
        [Description("One-based first line, default 1.")]
        int startLine = 1,
        [Description("Number of lines from 1 to 500, default 200.")]
        int lineCount = 200,
        [Description("Maximum UTF-16 code units from 1 to 32768, default 16384.")]
        int maxCharacters = 16384)
    {
        if (!ProviderBodyRules.IsToken(documentId, 128))
            return ProviderToolResults.Error("invalid_document_id", "documentId must be an opaque ID returned by this server.");
        if (!ProviderBodyRules.IsOptionalToken(expectedBufferVersion, 64))
            return ProviderToolResults.Error("invalid_buffer_version", "expectedBufferVersion must be a version returned by this server.");
        if (startLine < 1)
            return ProviderToolResults.Error("invalid_start_line", "startLine must be one-based.");
        if (lineCount is < 1 or > MaximumLineCount)
            return ProviderToolResults.Error("invalid_line_count", $"lineCount must be between 1 and {MaximumLineCount}.");
        if (maxCharacters is < 1 or > MaximumReadCharacters)
            return ProviderToolResults.Error("invalid_max_characters", $"maxCharacters must be between 1 and {MaximumReadCharacters}.");

        var outcome = await ProviderReadGateway.ReadAsync("documents.read", target => new DocumentsReadRequestBody
        {
            Target = target,
            DocumentId = documentId,
            ExpectedBufferVersion = expectedBufferVersion,
            StartLine = startLine,
            LineCount = lineCount,
            MaxCharacters = maxCharacters
        }).ConfigureAwait(false);
        if (!outcome.IsOk) return ProviderToolResults.FromOutcome(outcome);
        if (!DocumentProviderBodyRules.TryParseRead(outcome.Body, out var body) || body.DocumentId != documentId ||
            body.StartLine != startLine || body.Text.Length > maxCharacters || body.ReturnedLines > lineCount ||
            (expectedBufferVersion is not null && body.BufferVersion != expectedBufferVersion))
            return ProviderToolResults.InvalidProviderResponse(outcome);
        return ProviderToolResults.Success(body, outcome);
    }
}
