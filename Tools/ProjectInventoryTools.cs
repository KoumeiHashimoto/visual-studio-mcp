using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace VisualStudioMcpServer.Tools;

/// <summary>Bounded read-only solution and registered project-file inventory.</summary>
[McpServerToolType]
public class ProjectInventoryTools
{
    private static readonly ProjectSnapshotStore Snapshots = new();
    private static readonly ProjectWorkspaceConnector Connector = new(Snapshots);
    private const int MaxResponseCharacters = 65536;
    internal static ProjectSnapshotStore SnapshotStore => Snapshots;

    [McpServerTool]
    [System.ComponentModel.Description("Lists a bounded snapshot of projects in the explicitly bound Visual Studio solution. Pass the returned projectSnapshotId to get_project_info and list_project_files. This reads IDE registration only; it does not scan the disk or load projects.")]
    public static Task<string> ListSolutionProjects(string? expectedSolutionPath = null, int pageSize = 100,
        string? cursor = null, string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        ListProjectsAsync(expectedSolutionPath, pageSize, cursor, format, timeoutMilliseconds, cancellationToken);

    [McpServerTool]
    [System.ComponentModel.Description("Revalidates a project captured by list_solution_projects and returns current bounded project metadata. Project IDs are scoped to one snapshot and the bound Visual Studio process.")]
    public static Task<string> GetProjectInfo(string projectSnapshotId, string projectId,
        string? expectedSolutionPath = null, string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        GetProjectInfoAsync(projectSnapshotId, projectId, expectedSolutionPath, format, timeoutMilliseconds, cancellationToken);

    [McpServerTool]
    [System.ComponentModel.Description("Lists registered project items from an immutable file snapshot. This does not enumerate arbitrary disk files. Pass the returned file SnapshotId to open_project_file or remove_project_file.")]
    public static Task<string> ListProjectFiles(string projectSnapshotId, string projectId,
        string? expectedSolutionPath = null, int pageSize = 100, string? cursor = null,
        string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) =>
        ListFilesAsync(projectSnapshotId, projectId, expectedSolutionPath, pageSize, cursor, format, timeoutMilliseconds, cancellationToken);

    private static async Task<string> ListProjectsAsync(string? expectedSolutionPath, int pageSize,
        string? cursor, string format, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        if (!ValidFormat(format)) return Format(ProjectError("invalid_format", "Format must be text or json.", null, null, null, started), format);
        if (!TryTarget(out var pid, out var ticks, out var targetError))
            return Format(ProjectError(targetError!, "The bound Visual Studio process identity is required.", null, null, null, started), format);
        if (!ValidExpectedPath(expectedSolutionPath)) return Format(ProjectError("invalid_argument", "Expected solution path must be absolute when supplied.", pid, null, null, started), format);
        if (cursor is { Length: > 128 }) return Format(ProjectError("invalid_cursor", "The page cursor is invalid.", pid, null, null, started), format);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var timeout = Math.Clamp(timeoutMilliseconds, 1000, 10000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        try
        {
            ProjectSnapshotData snapshot;
            var offset = 0;
            var queryKey = QueryKey(expectedSolutionPath);
            if (!string.IsNullOrEmpty(cursor))
            {
                if (!Snapshots.TryGetCursor(cursor, out var savedCursor) || savedCursor is null || savedCursor.Kind != "projects" || savedCursor.QueryKey != queryKey)
                    return Format(ProjectError("invalid_cursor", "The page cursor is invalid or belongs to another query.", pid, null, null, started), format);
                if (!Snapshots.TryGetProject(savedCursor.SnapshotId, out var stored) || stored is null)
                    return Format(ProjectError("project_snapshot_expired", "The project snapshot expired; request a fresh inventory.", pid, null, null, started), format);
                snapshot = stored;
                offset = savedCursor.Offset;
                StaComDispatcher.Invoke(() => { Connector.VerifySolutionIdentity(pid, ticks,
                    snapshot.SolutionPath, expectedSolutionPath); return true; }, deadline - DateTimeOffset.UtcNow, cancellationToken);
            }
            else
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                snapshot = StaComDispatcher.Invoke(() => Connector.CaptureProjects(pid, ticks, expectedSolutionPath,
                    deadline, cancellationToken), remaining, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The project inventory deadline expired.");
                if (!Snapshots.TryPut(snapshot)) return Format(ProjectError("operation_failed", "The project snapshot exceeds the retention limit.", pid, snapshot.SolutionPath, null, started), format);
            }

            return Format(ProjectPage(snapshot, offset, pageSize, queryKey, started), format);
        }
        catch (ProjectWorkspaceException ex) { return Format(ProjectError(ex.Code, ex.Message, pid, null, null, started), format); }
        catch (OperationCanceledException) { return Format(ProjectError("cancelled", "The project inventory request was cancelled.", pid, null, null, started), format); }
        catch (TimeoutException) { return Format(ProjectError("com_timeout", "The Visual Studio project inventory exceeded its deadline.", pid, null, null, started), format); }
        catch (System.Runtime.InteropServices.COMException ex) { return Format(ProjectError("com_error", $"Visual Studio rejected project inspection (HRESULT 0x{ex.HResult:X8}).", pid, null, null, started), format); }
        catch (Exception) { return Format(ProjectError("operation_failed", "The solution project inventory could not be completed.", pid, null, null, started), format); }
    }

    private static async Task<string> GetProjectInfoAsync(string projectSnapshotId, string projectId,
        string? expectedSolutionPath, string format, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        if (!ValidFormat(format)) return Format(ProjectError("invalid_format", "Format must be text or json.", null, null, null, started), format);
        if (!TryTarget(out var pid, out var ticks, out var targetError)) return Format(ProjectError(targetError!, "The bound Visual Studio process identity is required.", null, null, null, started), format);
        if (string.IsNullOrWhiteSpace(projectSnapshotId) || string.IsNullOrWhiteSpace(projectId)) return Format(ProjectError("invalid_argument", "Project snapshot and project IDs are required.", pid, null, null, started), format);
        if (!Snapshots.TryGetProject(projectSnapshotId, out var snapshot) || snapshot is null) return Format(ProjectError("project_snapshot_expired", "The project snapshot expired; request a fresh inventory.", pid, null, null, started), format);
        if (snapshot.TargetProcessId != pid || snapshot.TargetProcessStartUtcTicks != ticks) return Format(ProjectError("stale_target", "The project snapshot belongs to another Visual Studio process identity.", pid, snapshot.SolutionPath, null, started), format);
        if (!ValidExpectedPath(expectedSolutionPath)) return Format(ProjectError("invalid_argument", "Expected solution path must be absolute when supplied.", pid, snapshot.SolutionPath, null, started), format);
        var selected = snapshot.Projects.FirstOrDefault(project => project.ProjectId == projectId);
        if (selected is null) return Format(ProjectError("stale_project", "The project ID is not part of this snapshot.", pid, snapshot.SolutionPath, null, started), format);
        var timeout = Math.Clamp(timeoutMilliseconds, 1000, 10000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        try
        {
            var project = StaComDispatcher.Invoke(() => Connector.ReadProjectInfo(snapshot, selected,
                expectedSolutionPath, pid, ticks, deadline, cancellationToken), deadline - DateTimeOffset.UtcNow, cancellationToken);
            if (project is null) return Format(ProjectError("stale_project", "The project no longer matches its captured path and logical ancestry.", pid, snapshot.SolutionPath, null, started), format);
            var result = new ProjectInfoResult(true, "ok", "Project information was revalidated.", pid,
                snapshot.SolutionPath, snapshot.SnapshotId, null, project, null, false,
                snapshot.Incomplete, snapshot.Projects.Count, snapshot.TotalCount, snapshot.Warnings, Elapsed(started));
            return Format(result, format);
        }
        catch (ProjectWorkspaceException ex) { return Format(ProjectError(ex.Code, ex.Message, pid, snapshot.SolutionPath, null, started), format); }
        catch (OperationCanceledException) { return Format(ProjectError("cancelled", "The project information request was cancelled.", pid, snapshot.SolutionPath, null, started), format); }
        catch (TimeoutException) { return Format(ProjectError("com_timeout", "The project information request exceeded its deadline.", pid, snapshot.SolutionPath, null, started), format); }
        catch (System.Runtime.InteropServices.COMException ex) { return Format(ProjectError("com_error", $"Visual Studio rejected project inspection (HRESULT 0x{ex.HResult:X8}).", pid, snapshot.SolutionPath, null, started), format); }
        catch (Exception) { return Format(ProjectError("operation_failed", "The project information could not be read.", pid, snapshot.SolutionPath, null, started), format); }
    }

    private static async Task<string> ListFilesAsync(string projectSnapshotId, string projectId,
        string? expectedSolutionPath, int pageSize, string? cursor, string format,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        if (!ValidFormat(format)) return Format(FileError("invalid_format", "Format must be text or json.", null, null, null, null, started), format);
        if (!TryTarget(out var pid, out var ticks, out var targetError)) return Format(FileError(targetError!, "The bound Visual Studio process identity is required.", null, null, null, null, started), format);
        if (string.IsNullOrWhiteSpace(projectSnapshotId) || string.IsNullOrWhiteSpace(projectId)) return Format(FileError("invalid_argument", "Project snapshot and project IDs are required.", pid, null, null, null, started), format);
        if (!Snapshots.TryGetProject(projectSnapshotId, out var projectSnapshot) || projectSnapshot is null) return Format(FileError("project_snapshot_expired", "The project snapshot expired; request a fresh inventory.", pid, null, null, null, started), format);
        if (projectSnapshot.TargetProcessId != pid || projectSnapshot.TargetProcessStartUtcTicks != ticks) return Format(FileError("stale_target", "The project snapshot belongs to another Visual Studio process identity.", pid, projectSnapshot.SolutionPath, null, null, started), format);
        var selected = projectSnapshot.Projects.FirstOrDefault(project => project.ProjectId == projectId);
        if (selected is null) return Format(FileError("stale_project", "The project ID is not part of this snapshot.", pid, projectSnapshot.SolutionPath, null, null, started), format);
        if (selected.IsSolutionFolder || selected.ProjectPath is null) return Format(FileError("unsupported_project_type", "Solution folders do not contain a project file tree.", pid, projectSnapshot.SolutionPath, null, null, started), format);
        if (!ValidExpectedPath(expectedSolutionPath)) return Format(FileError("invalid_argument", "Expected solution path must be absolute when supplied.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format);
        if (cursor is { Length: > 128 }) return Format(FileError("invalid_cursor", "The page cursor is invalid.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Clamp(timeoutMilliseconds, 1000, 10000));
        try
        {
            FileSnapshotData files;
            var offset = 0;
            var queryKey = QueryKey(expectedSolutionPath) + "|" + projectSnapshotId + "|" + projectId;
            if (!string.IsNullOrEmpty(cursor))
            {
                if (!Snapshots.TryGetCursor(cursor, out var savedCursor) || savedCursor is null || savedCursor.Kind != "files" || savedCursor.QueryKey != queryKey || savedCursor.ProjectId != projectId)
                    return Format(FileError("invalid_cursor", "The page cursor is invalid or belongs to another project.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format);
                if (!Snapshots.TryGetFiles(savedCursor.SnapshotId, out var stored) || stored is null)
                    return Format(FileError("file_snapshot_expired", "The file snapshot expired; request a fresh file inventory.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format);
                if (stored.ProjectSnapshotId != projectSnapshotId || stored.ProjectId != projectId)
                    return Format(FileError("stale_project", "The file snapshot belongs to a different project identity.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format);
                var verified = StaComDispatcher.Invoke(() => Connector.ReadProjectInfo(projectSnapshot, selected,
                    expectedSolutionPath, pid, ticks, deadline, cancellationToken), deadline - DateTimeOffset.UtcNow, cancellationToken);
                if (verified is null) return Format(FileError("stale_project", "The project no longer matches its captured path and logical ancestry.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, stored.SnapshotId, started), format);
                files = stored;
                offset = savedCursor.Offset;
            }
            else
            {
                var capture = StaComDispatcher.Invoke(() => Connector.CaptureFiles(projectSnapshot, selected,
                    expectedSolutionPath, pid, ticks, deadline, cancellationToken), deadline - DateTimeOffset.UtcNow, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The file inventory deadline expired.");
                files = capture.Snapshot;
                if (!Snapshots.TryPut(files)) return Format(FileError("operation_failed", "The file snapshot exceeds the retention limit.", pid, files.SolutionPath, files.ProjectPath, null, started), format);
            }
            return Format(FilePage(files, offset, pageSize, queryKey, started), format);
        }
        catch (ProjectWorkspaceException ex) { return Format(FileError(ex.Code, ex.Message, pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format); }
        catch (OperationCanceledException) { return Format(FileError("cancelled", "The project file inventory request was cancelled.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format); }
        catch (TimeoutException) { return Format(FileError("com_timeout", "The project file inventory exceeded its deadline.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format); }
        catch (System.Runtime.InteropServices.COMException ex) { return Format(FileError("com_error", $"Visual Studio rejected project-item inspection (HRESULT 0x{ex.HResult:X8}).", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format); }
        catch (Exception) { return Format(FileError("operation_failed", "The project file inventory could not be completed.", pid, projectSnapshot.SolutionPath, selected.ProjectPath, null, started), format); }
    }

    private static ProjectInfoResult ProjectPage(ProjectSnapshotData snapshot, int offset, int pageSize,
        string queryKey, long started)
    {
        if (offset < 0 || offset > snapshot.Projects.Count) return ProjectError("invalid_cursor", "The page cursor offset is invalid.", snapshot.TargetProcessId, snapshot.SolutionPath, snapshot.SnapshotId, started);
        var count = Math.Min(pageSize, snapshot.Projects.Count - offset);
        ProjectInfoResult result;
        while (true)
        {
            var hasMore = offset + count < snapshot.Projects.Count;
            var provisionalCursor = hasMore ? new string('x', 22) : null;
            result = new ProjectInfoResult(true, snapshot.Incomplete ? "partial" : "ok",
                snapshot.Incomplete ? "Project inventory is incomplete; review warnings before relying on it." : "Project inventory was captured.",
                snapshot.TargetProcessId, snapshot.SolutionPath, snapshot.SnapshotId,
                snapshot.Projects.Skip(offset).Take(count).ToArray(), null, provisionalCursor, hasMore,
                snapshot.Incomplete, snapshot.Projects.Count, snapshot.TotalCount, snapshot.Warnings, Elapsed(started));
            if (JsonSerializer.Serialize(result).Length <= MaxResponseCharacters || count == 0) break;
            count--;
        }
        if (result.HasMore)
            result = result with { NextCursor = Snapshots.AddCursor(new SnapshotCursor("projects", snapshot.SnapshotId, null, queryKey, offset + count)) };
        else result = result with { NextCursor = null };
        return result;
    }

    private static ProjectFilesResult FilePage(FileSnapshotData snapshot, int offset, int pageSize,
        string queryKey, long started)
    {
        if (offset < 0 || offset > snapshot.Items.Count) return FileError("invalid_cursor", "The page cursor offset is invalid.", snapshot.TargetProcessId, snapshot.SolutionPath, snapshot.ProjectPath, snapshot.SnapshotId, started);
        var count = Math.Min(pageSize, snapshot.Items.Count - offset);
        ProjectFilesResult result;
        while (true)
        {
            var hasMore = offset + count < snapshot.Items.Count;
            var provisionalCursor = hasMore ? new string('x', 22) : null;
            result = new ProjectFilesResult(true, snapshot.Incomplete ? "partial" : "ok",
                snapshot.Incomplete ? "Project-file inventory is incomplete; review warnings before relying on it." : "Project-file inventory was captured.",
                snapshot.TargetProcessId, snapshot.SolutionPath, snapshot.ProjectPath, snapshot.SnapshotId,
                snapshot.Items.Skip(offset).Take(count).ToArray(), provisionalCursor, hasMore,
                snapshot.Incomplete, snapshot.Items.Count, snapshot.TotalCount, snapshot.Warnings, Elapsed(started));
            if (JsonSerializer.Serialize(result).Length <= MaxResponseCharacters || count == 0) break;
            count--;
        }
        if (result.HasMore)
            result = result with { NextCursor = Snapshots.AddCursor(new SnapshotCursor("files", snapshot.SnapshotId, snapshot.ProjectId, queryKey, offset + count)) };
        else result = result with { NextCursor = null };
        return result;
    }

    private static ProjectInfoResult ProjectError(string code, string message, int? pid, string? solutionPath, string? snapshotId, long started) =>
        new(false, code, Limit(message), pid, solutionPath, snapshotId, null, null, null, false, false, 0, null, Array.Empty<string>(), Elapsed(started));

    private static ProjectFilesResult FileError(string code, string message, int? pid, string? solutionPath, string? projectPath, string? snapshotId, long started) =>
        new(false, code, Limit(message), pid, solutionPath, projectPath, snapshotId, null, null, false, false, 0, null, Array.Empty<string>(), Elapsed(started));

    private static string Format<T>(T result, string format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(result);
            if (json.Length <= MaxResponseCharacters) return json;
            return JsonSerializer.Serialize(ProjectError("operation_failed", "The inventory page exceeded the response limit.", null, null, null, Stopwatch.GetTimestamp()));
        }
        return result switch
        {
            ProjectInfoResult projects => $"{projects.Code}: {projects.Message} projects={projects.Items?.Count ?? 0}; total={projects.TotalCount?.ToString() ?? "unknown"}; incomplete={projects.Incomplete}",
            ProjectFilesResult files => $"{files.Code}: {files.Message} items={files.Items?.Count ?? 0}; total={files.TotalCount?.ToString() ?? "unknown"}; incomplete={files.Incomplete}",
            _ => "operation_failed: Inventory response could not be formatted."
        };
    }

    private static bool TryTarget(out int pid, out long ticks, out string? error)
    {
        if (!VisualStudioConnector.TryGetTargetProcessIdentity(out pid, out ticks)) { error = "target_identity_required"; return false; }
        error = null;
        return true;
    }

    private static bool ValidFormat(string? format) => string.Equals(format, "text", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
    private static bool ValidExpectedPath(string? path) => string.IsNullOrWhiteSpace(path) || path.Length <= 2048 && Path.IsPathFullyQualified(path);
    private static string QueryKey(string? expectedPath) => string.IsNullOrWhiteSpace(expectedPath) ? "" : Path.GetFullPath(expectedPath).ToUpperInvariant();
    private static string Limit(string message) => message.Length <= 1024 ? message : message[..1024];
    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
