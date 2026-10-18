using System.ComponentModel;
using Newtonsoft.Json;
using VisualStudioMcpServer.Diagnostics;
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
    [Description("Returns a stable, bounded page from a captured Visual Studio Error List snapshot. DTE-only fields remain explicitly unavailable.")]
    public static string GetDiagnostics(
        [Description("Exact active solution path. Required for solution workspaces; Open Folder is currently unsupported.")]
        string? expectedSolutionPath = null,
        [Description("Expected Open Folder root. The current DTE-only reader returns unsupported for Open Folder.")]
        string? expectedWorkspaceRoot = null,
        [Description("Optional exact severity filter: Error, Warning, Message, or Unknown.")]
        string? severity = null,
        [Description("Stable project identity filter. Unavailable from the current DTE reader; requests return unsupported.")]
        string? projectId = null,
        [Description("Optional case-insensitive file path substring filter.")]
        string? file = null,
        [Description("Optional source filter. DTE currently supports only 'unknown'.")]
        string? source = null,
        [Description("Page size from 1 to 200, default 100.")]
        int pageSize = 100,
        [Description("Opaque cursor returned by a prior page. Cursors expire with their bounded snapshot.")]
        string? cursor = null,
        [Description("Build observation annotation only. DTE does not provide exact diagnostic-to-operation correlation.")]
        string? operationId = null)
    {
        if (pageSize is < 1 or > MaximumPageSize)
            return Error("invalid_page_size", $"pageSize must be between 1 and {MaximumPageSize}.");
        if (projectId is not null)
            return Error("project_filter_unavailable", "The DTE Error List reader does not expose a stable project identity.");
        if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source, "unknown", StringComparison.OrdinalIgnoreCase))
            return Error("source_filter_unavailable", "The DTE Error List reader cannot establish diagnostic source provenance.");
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

        var filterFingerprint = DiagnosticSnapshotStore.FilterFingerprint(severity, file, source);
        return ReadOnlyToolInvocation.Invoke("get_diagnostics", () =>
        {
            var solution = VisualStudioConnector.GetSolutionInfo();
            if (solution is null)
                return Error("workspace_unavailable", "The active Visual Studio solution identity is unavailable.");
            if (string.Equals(solution.WorkspaceKind, "openFolder", StringComparison.OrdinalIgnoreCase))
                return Error("unsupported", "The DTE diagnostic reader does not support Open Folder workspaces.");
            if (workspaceRootRequested)
                return Error("workspace_kind_mismatch", "expectedWorkspaceRoot is valid only for an Open Folder workspace.");
            if (string.IsNullOrWhiteSpace(solution.SolutionPath) || !solutionPathRequested ||
                !PathsEqual(solution.SolutionPath, expectedSolutionPath!))
                return Error("target_mismatch", "The active solution does not match expectedSolutionPath.");

            var identity = Path.GetFullPath(solution.SolutionPath);
            string captureId;
            int offset;
            string cursorFilter;
            if (cursor is null)
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
                    return Error("diagnostic_capture_failed", "The Error List could not be captured as a stable snapshot.");
                }
                offset = 0;
                cursorFilter = filterFingerprint;
            }
            else
            {
                if (!DiagnosticSnapshotStore.TryReadCursor(cursor, out captureId, out offset, out cursorFilter))
                    return Error("invalid_cursor", "The diagnostics cursor is malformed.");
                if (!string.Equals(cursorFilter, filterFingerprint, StringComparison.Ordinal))
                    return Error("cursor_filter_mismatch", "The diagnostics cursor belongs to different filters.");
            }

            if (!Store.TryRead(captureId, identity, severity, file, source, cursorFilter, offset,
                    pageSize, DateTimeOffset.UtcNow, out var page) || page is null)
                return Error("snapshot_expired", "The diagnostics snapshot expired or no longer matches the active solution. Capture a new first page.");

            var result = new DiagnosticsResult
            {
                Success = true,
                CaptureId = page.CaptureId,
                CapturedAtUtc = page.CapturedAtUtc,
                Total = page.TotalItems,
                Offset = page.Offset,
                Returned = page.Items.Count,
                CaptureComplete = page.CaptureComplete,
                Truncated = !page.CaptureComplete,
                SourceAvailability = "unavailable",
                CodeAvailability = "unavailable",
                ProjectIdentityAvailability = "unavailable",
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
                : Error("response_too_large", "Diagnostics response metadata exceeded its size limit.");
        });
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];

    private static string Error(string code, string message) =>
        JsonConvert.SerializeObject(new { Success = false, Code = code, Message = message });

    private sealed record DiagnosticsResult
    {
        public bool Success { get; init; }
        public string CaptureId { get; init; } = string.Empty;
        public DateTimeOffset CapturedAtUtc { get; init; }
        public int Total { get; init; }
        public int Offset { get; init; }
        public int Returned { get; init; }
        public bool CaptureComplete { get; init; }
        public bool Truncated { get; init; }
        public string SourceAvailability { get; init; } = string.Empty;
        public string CodeAvailability { get; init; } = string.Empty;
        public string ProjectIdentityAvailability { get; init; } = string.Empty;
        public string? OperationId { get; init; }
        public string OperationCorrelation { get; init; } = string.Empty;
        public IReadOnlyList<DiagnosticSnapshotItem> Items { get; init; } = Array.Empty<DiagnosticSnapshotItem>();
        public string? NextCursor { get; init; }
    }
}
