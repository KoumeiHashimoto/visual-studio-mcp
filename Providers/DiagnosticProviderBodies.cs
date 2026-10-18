namespace VisualStudioMcpServer.Providers;

// ---- P13-05a（#59）diagnostics.capture の応答本文（wire は camelCase）----
// 親 repository の Providers/DiagnosticReadCore.cs と同じ member 名・順序を保つ。未知 member は契約違反として拒否される。

internal sealed record DiagnosticRowBody
{
    public string? Code { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool DescriptionTruncated { get; init; }
    public string? DocumentPath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? ProjectName { get; init; }
    public string? ProjectId { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? BuildTool { get; init; }
}

internal sealed record DiagnosticsCaptureResponseBody
{
    public string CaptureId { get; init; } = string.Empty;
    public long ProviderGeneration { get; init; }
    public string Provenance { get; init; } = string.Empty;
    public bool Partial { get; init; }
    public bool SourcesStable { get; init; }
    public int SourceCount { get; init; }
    public int ScannedRows { get; init; }
    public string CodeAvailability { get; init; } = string.Empty;
    public string SourceAvailability { get; init; } = string.Empty;
    public string ProjectIdentityAvailability { get; init; } = string.Empty;
    public string RangeAvailability { get; init; } = string.Empty;
    public DiagnosticRowBody[] Rows { get; init; } = [];
}

internal static class DiagnosticProviderBodyRules
{
    internal const int MaxRows = 500;
    internal const int MaxDescriptionCharacters = 4096;

    internal static bool TryParse(string? body, out DiagnosticsCaptureResponseBody value)
    {
        value = null!;
        if (string.IsNullOrEmpty(body)) return false;
        try { value = ProviderProtocol.ParseBody<DiagnosticsCaptureResponseBody>(body); }
        catch (ProviderProtocolException) { return false; }
        return ProviderProtocol.IsUuidN(value.CaptureId) && value.ProviderGeneration >= 0 &&
            value.Provenance == "errorListTable" && value.SourceCount >= 0 && value.ScannedRows >= 0 &&
            IsAvailability(value.CodeAvailability) && IsAvailability(value.SourceAvailability) &&
            IsAvailability(value.ProjectIdentityAvailability) && value.RangeAvailability is "startOnly" or "unavailable" &&
            value.Rows is { Length: <= MaxRows } && value.Rows.All(static row => row is not null && IsValidRow(row));
    }

    private static bool IsValidRow(DiagnosticRowBody row) =>
        (row.Code is null || row.Code.Length <= 128) && row.Severity is "error" or "warning" or "message" or "unknown" &&
        row.Description is not null && row.Description.Length <= MaxDescriptionCharacters &&
        (row.DocumentPath is null || row.DocumentPath.Length <= 2048) &&
        (row.Line is null or >= 1) && (row.Column is null or >= 1) &&
        (row.ProjectName is null || row.ProjectName.Length <= 256) && ProviderBodyRules.IsOptionalToken(row.ProjectId, 128) &&
        row.Source is "build" or "intellisense" or "other" or "unknown" && (row.BuildTool is null || row.BuildTool.Length <= 128);

    private static bool IsAvailability(string? value) => value is "available" or "partial" or "unavailable";
}
