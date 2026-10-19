using System.ComponentModel;
using Newtonsoft.Json;
using VisualStudioMcpServer.Diagnostics;
using VisualStudioMcpServer.Providers;
using ModelContextProtocol.Server;

namespace VisualStudioMcpServer.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    private const int MaximumPageSize = 200;
    private const int MaximumMessageCharacters = 4096;
    private const int MaximumResponseCharacters = 64 * 1024;
    private static readonly DiagnosticSnapshotStore Store = new();

    [McpServerTool]
    [Description("Returns a stable, bounded page from a captured Visual Studio Error List snapshot. Prefers the VSIX Error List table provider for code, source, project identity and Open Folder workspaces; otherwise falls back to the DTE reader, whose fields remain explicitly unavailable. Provenance reports which reader captured the snapshot.")]
    public static async Task<string> GetDiagnostics(
        [Description("Exact active solution path. Required for solution workspaces.")]
        string? expectedSolutionPath = null,
        [Description("Expected Open Folder root. Supported through the VSIX provider; the DTE fallback returns unsupported for Open Folder.")]
        string? expectedWorkspaceRoot = null,
        [Description("Optional exact severity filter: Error, Warning, Message, or Unknown.")]
        string? severity = null,
        [Description("Optional stable project identity filter (the project GUID reported by the VSIX provider). The DTE fallback cannot filter by project.")]
        string? projectId = null,
        [Description("Optional case-insensitive file path substring filter.")]
        string? file = null,
        [Description("Optional source filter: build, intellisense, other, or unknown. The provider reports build or other from the Error List source and never infers intellisense; the DTE fallback supports only unknown.")]
        string? source = null,
        [Description("Page size from 1 to 200, default 100.")]
        int pageSize = 100,
        [Description("Opaque cursor returned by a prior page. Cursors expire with their bounded snapshot.")]
        string? cursor = null,
        [Description("Build observation annotation only. Rows are never attributed to an operation without provider evidence.")]
        string? operationId = null)
    {
        if (pageSize is < 1 or > MaximumPageSize)
            return Error("invalid_page_size", $"pageSize must be between 1 and {MaximumPageSize}.");
        if (projectId is not null && !ProviderBodyRules.IsToken(projectId, 128))
            return Error("invalid_project_id", "projectId must be a stable project identity of at most 128 characters.");
        if (source is not null && !new[] { "build", "intellisense", "other", "unknown" }.Contains(source, StringComparer.OrdinalIgnoreCase))
            return Error("invalid_source", "source must be build, intellisense, other, or unknown.");
        if (severity is not null && !new[] { "Error", "Warning", "Message", "Unknown" }
                .Contains(severity, StringComparer.OrdinalIgnoreCase))
            return Error("invalid_severity", "severity must be Error, Warning, Message, or Unknown.");
        if (operationId is { Length: > 128 } || (operationId is not null && operationId.Any(char.IsControl)))
            return Error("invalid_operation_id", "operationId must be at most 128 printable characters.");

        var solutionPathRequested = !string.IsNullOrWhiteSpace(expectedSolutionPath);
        var workspaceRootRequested = !string.IsNullOrWhiteSpace(expectedWorkspaceRoot);
        if (solutionPathRequested == workspaceRootRequested)
            return Error("workspace_identity_required", "Supply exactly one of expectedSolutionPath or expectedWorkspaceRoot.");
        if ((solutionPathRequested && (expectedSolutionPath is null || !Path.IsPathFullyQualified(expectedSolutionPath))) ||
            (workspaceRootRequested && (expectedWorkspaceRoot is null || !Path.IsPathFullyQualified(expectedWorkspaceRoot))))
            return Error("invalid_workspace_identity", "The expected solution path or workspace root must be absolute.");

        var filterFingerprint = DiagnosticSnapshotStore.FilterFingerprint(severity, file, source, projectId);
        string? identity;
        try { identity = Path.GetFullPath(solutionPathRequested ? expectedSolutionPath! : expectedWorkspaceRoot!); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error("invalid_workspace_identity", "The expected solution path or workspace root is invalid.");
        }
        if (identity.Length > 1024)
            return Error("invalid_workspace_identity", "The expected solution path or workspace root exceeds 1024 characters.");

        if (cursor is not null)
        {
            if (!DiagnosticSnapshotStore.TryReadCursor(cursor, out var cursorCaptureId, out var cursorOffset, out var cursorFilter))
                return Error("invalid_cursor", "The diagnostics cursor is malformed.");
            if (!string.Equals(cursorFilter, filterFingerprint, StringComparison.Ordinal))
                return Error("cursor_filter_mismatch", "The diagnostics cursor belongs to different filters.");
            var info = Store.GetInfo(cursorCaptureId, DateTimeOffset.UtcNow);
            if (info is not null && info.Provenance != DiagnosticCaptureInfo.Dte.Provenance)
            {
                // provider の snapshot は capture 時に IDE 側で workspace を照合済み。page 送りで再列挙しない。
                return ReadPage(cursorCaptureId, identity, severity, file, source, projectId, filterFingerprint,
                    cursorOffset, pageSize, operationId, null);
            }
            return ReadOnlyToolInvocation.Invoke("get_diagnostics", () =>
                DteFlow(identity, solutionPathRequested, workspaceRootRequested, severity, projectId, file, source,
                    pageSize, cursorCaptureId, cursorOffset, filterFingerprint, operationId, null));
        }

        var outcome = await ProviderReadGateway.ReadAsync("diagnostics.capture", target => new DiagnosticsCaptureRequestBody
        {
            Target = target,
            Severity = severity is null || severity.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? null : severity.ToLowerInvariant(),
            File = string.IsNullOrEmpty(file) || file.Length > 1024 ? null : file,
            Source = source?.ToLowerInvariant(),
            ProjectId = projectId
        }, identity).ConfigureAwait(false);
        if (outcome.IsOk)
        {
            if (!DiagnosticProviderBodyRules.TryParse(outcome.Body, out var body))
                return ProviderToolResults.InvalidProviderResponse(outcome);
            var rows = body.Rows.Select(row => new DiagnosticSnapshotItem(
                    ToLegacySeverity(row.Severity), row.Description, row.DocumentPath ?? string.Empty,
                    row.Line ?? 0, row.Column ?? 0, row.ProjectName ?? string.Empty)
                {
                    Code = row.Code,
                    Source = row.Source,
                    ProjectId = row.ProjectId,
                    BuildTool = row.BuildTool,
                    DescriptionTruncated = row.DescriptionTruncated
                }).ToArray();
            var captureInfo = new DiagnosticCaptureInfo(body.Provenance, body.CodeAvailability, body.SourceAvailability,
                body.ProjectIdentityAvailability, body.RangeAvailability, outcome.ProviderEpoch);
            var captureId = Store.Capture(identity, rows, !body.Partial, DateTimeOffset.UtcNow, captureInfo);
            return ReadPage(captureId, identity, severity, file, source, projectId, filterFingerprint, 0, pageSize, operationId, null);
        }
        // IDE 側の照合で別 workspace と判明した場合は、DTE で別の workspace を読まない。
        if (outcome.Code is ProviderErrorCodes.TargetMismatch or ProviderErrorCodes.InvalidRequest)
            return ProviderToolResults.FromOutcome(outcome);

        var fallbackReason = outcome.ProviderReason ?? outcome.Code ?? "provider_unavailable";
        return ReadOnlyToolInvocation.Invoke("get_diagnostics", () =>
            DteFlow(identity, solutionPathRequested, workspaceRootRequested, severity, projectId, file, source,
                pageSize, null, 0, filterFingerprint, operationId, fallbackReason));
    }

    /// <summary>DTE の Error List を固定 snapshot として読む fallback。provider が使えない場合だけ使う。</summary>
    private static string DteFlow(string identity, bool solutionPathRequested, bool workspaceRootRequested, string? severity,
        string? projectId, string? file, string? source, int pageSize, string? cursorCaptureId, int cursorOffset,
        string filterFingerprint, string? operationId, string? fallbackReason)
    {
        if (projectId is not null)
            return Error("project_filter_unavailable", "The DTE Error List reader does not expose a stable project identity.", fallbackReason);
        if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source, "unknown", StringComparison.OrdinalIgnoreCase))
            return Error("source_filter_unavailable", "The DTE Error List reader cannot establish diagnostic source provenance.", fallbackReason);
        var solution = VisualStudioConnector.GetSolutionInfo();
        if (solution is null)
            return Error("workspace_unavailable", "The active Visual Studio solution identity is unavailable.", fallbackReason);
        if (string.Equals(solution.WorkspaceKind, "openFolder", StringComparison.OrdinalIgnoreCase))
            return Error("unsupported", "The DTE diagnostic reader does not support Open Folder workspaces.", fallbackReason);
        if (workspaceRootRequested)
            return Error("workspace_kind_mismatch", "expectedWorkspaceRoot is valid only for an Open Folder workspace.", fallbackReason);
        if (string.IsNullOrWhiteSpace(solution.SolutionPath) || !solutionPathRequested ||
            !PathsEqual(solution.SolutionPath, identity))
            return Error("target_mismatch", "The active solution does not match expectedSolutionPath.", fallbackReason);

        var captureId = cursorCaptureId;
        var offset = cursorOffset;
        if (captureId is null)
        {
            try
            {
                var captured = VisualStudioConnector.GetErrorListItems(501);
                var complete = captured.Count <= 500;
                var rows = captured.Take(500).Select(item => new DiagnosticSnapshotItem(
                    Bound(item.Severity, 32), Bound(item.Description, MaximumMessageCharacters),
                    Bound(item.FileName, 2048), Math.Max(0, item.Line), Math.Max(0, item.Column),
                    Bound(item.Project, 256))).ToArray();
                captureId = Store.Capture(identity, rows, complete, DateTimeOffset.UtcNow);
            }
            catch
            {
                return Error("diagnostic_capture_failed", "The Error List could not be captured as a stable snapshot.", fallbackReason);
            }
            offset = 0;
        }
        return ReadPage(captureId, identity, severity, file, source, projectId, filterFingerprint, offset, pageSize,
            operationId, fallbackReason);
    }

    private static string ReadPage(string captureId, string identity, string? severity, string? file, string? source,
        string? projectId, string filterFingerprint, int offset, int pageSize, string? operationId, string? fallbackReason)
    {
        if (!Store.TryRead(captureId, identity, severity, file, source, projectId, filterFingerprint, offset,
                pageSize, DateTimeOffset.UtcNow, out var page) || page is null)
            return Error("snapshot_expired", "The diagnostics snapshot expired or no longer matches the active solution. Capture a new first page.", fallbackReason);

        var result = new DiagnosticsResult
        {
            Success = true,
            CaptureId = page.CaptureId,
            CapturedAtUtc = page.CapturedAtUtc,
            Provenance = page.Info.Provenance,
            ProviderEpoch = page.Info.ProviderEpoch,
            ProviderFallbackReason = fallbackReason,
            Total = page.TotalItems,
            Offset = page.Offset,
            Returned = page.Items.Count,
            CaptureComplete = page.CaptureComplete,
            Truncated = !page.CaptureComplete,
            SourceAvailability = page.Info.SourceAvailability,
            CodeAvailability = page.Info.CodeAvailability,
            ProjectIdentityAvailability = page.Info.ProjectIdentityAvailability,
            RangeAvailability = page.Info.RangeAvailability,
            OperationId = operationId,
            OperationCorrelation = string.IsNullOrWhiteSpace(operationId) ? "not_requested" : "unknown",
            Items = page.Items,
            NextCursor = page.NextCursor
        };
        var json = JsonConvert.SerializeObject(result);
        while (json.Length > MaximumResponseCharacters && result.Items.Count > 0)
        {
            var items = result.Items.Take(result.Items.Count - 1).ToArray();
            var returned = items.Length;
            var nextOffset = page.Offset + returned;
            result = result with
            {
                Items = items,
                Returned = returned,
                NextCursor = nextOffset < page.TotalItems
                    ? DiagnosticSnapshotStore.CreateCursorForOffset(page.CaptureId, nextOffset, filterFingerprint)
                    : null,
                Truncated = true
            };
            json = JsonConvert.SerializeObject(result);
        }
        return json.Length <= MaximumResponseCharacters
            ? json
            : Error("response_too_large", "Diagnostics response metadata exceeded its size limit.", fallbackReason);
    }

    private static string ToLegacySeverity(string severity) => severity switch
    {
        "error" => "Error",
        "warning" => "Warning",
        "message" => "Message",
        _ => "Unknown"
    };

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];

    private static string Error(string code, string message, string? fallbackReason = null) =>
        fallbackReason is null
            ? JsonConvert.SerializeObject(new { Success = false, Code = code, Message = message })
            : JsonConvert.SerializeObject(new { Success = false, Code = code, Message = message, Provenance = "dte", ProviderFallbackReason = fallbackReason });

    private sealed record DiagnosticsResult
    {
        public bool Success { get; init; }
        public string CaptureId { get; init; } = string.Empty;
        public DateTimeOffset CapturedAtUtc { get; init; }
        public string Provenance { get; init; } = string.Empty;
        public string? ProviderEpoch { get; init; }
        public string? ProviderFallbackReason { get; init; }
        public int Total { get; init; }
        public int Offset { get; init; }
        public int Returned { get; init; }
        public bool CaptureComplete { get; init; }
        public bool Truncated { get; init; }
        public string SourceAvailability { get; init; } = string.Empty;
        public string CodeAvailability { get; init; } = string.Empty;
        public string ProjectIdentityAvailability { get; init; } = string.Empty;
        public string RangeAvailability { get; init; } = string.Empty;
        public string? OperationId { get; init; }
        public string OperationCorrelation { get; init; } = string.Empty;
        public IReadOnlyList<DiagnosticSnapshotItem> Items { get; init; } = Array.Empty<DiagnosticSnapshotItem>();
        public string? NextCursor { get; init; }
    }
}
