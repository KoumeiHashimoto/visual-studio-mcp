/**
  Visual Studioのプロジェクトとワークスペース情報を管理します。
*/
using System.Diagnostics;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using VisualStudioMcpServer.Tools;

namespace VisualStudioMcpServer;

/// <summary>Per-call DTE adapter. It returns only immutable managed snapshot values.</summary>
internal sealed class ProjectWorkspaceConnector
{
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
    internal const int MaxDepth = ProjectTraversalBudget.MaxDepth;
    private const int MaxNodes = ProjectTraversalBudget.MaxNodes;
    private readonly ProjectSnapshotStore _store;

    internal ProjectWorkspaceConnector(ProjectSnapshotStore store) => _store = store;

    internal ProjectSnapshotData CaptureProjects(int processId, long startTicks, string? expectedSolutionPath,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var dte = GetBoundDte(processId, startTicks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var solutionPath = solution.FullName;
        if (string.IsNullOrWhiteSpace(solutionPath)) { _store.InvalidateAll(); throw new ProjectWorkspaceException("no_solution", "No solution is open."); }
        solutionPath = Path.GetFullPath(solutionPath);
        _store.ObserveTarget(processId, startTicks, solutionPath);
        ValidateExpectedSolution(solutionPath, expectedSolutionPath);
        var records = new List<ProjectInfoRecord>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incomplete = false;
        var nodeCount = 0;
        var projects = solution.Projects;
        try
        {
            foreach (Project project in projects)
            {
                WalkProject(project, null, string.Empty, 0);
                if (nodeCount >= MaxNodes) { incomplete = true; AddWarning("Project traversal reached the 10000-node limit."); break; }
            }
        }
        catch (COMException ex) { incomplete = true; AddWarning($"Some solution projects could not be enumerated (HRESULT 0x{ex.HResult:X8})."); }
        return new ProjectSnapshotData(_store.NewId(), processId, startTicks, solutionPath,
            records.ToArray(), incomplete, incomplete ? null : records.Count, warnings.ToArray(), DateTimeOffset.UtcNow);

        void WalkProject(Project project, string? parentId, string parentPath, int depth)
        {
            CheckDeadline(deadline, cancellationToken);
            if (project is null) { incomplete = true; AddWarning("An unavailable project entry was skipped."); return; }
            if (depth > MaxDepth) { incomplete = true; AddWarning("Project traversal reached the depth limit."); return; }
            if (++nodeCount > MaxNodes) { incomplete = true; return; }
            string name;
            try { name = project.Name ?? string.Empty; }
            catch (COMException ex) { incomplete = true; AddWarning($"A project name was unavailable (HRESULT 0x{ex.HResult:X8})."); return; }
            var boundedName = name.Length <= 256 ? name : name[..256];
            var kind = TryRead(() => project.Kind, null, "A project type was unavailable.", ReportIncomplete);
            var isFolder = string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase);
            var uniqueName = TryRead(() => project.UniqueName, null, "A project unique name was unavailable.", ReportIncomplete);
            var path = TryRead(() => project.FullName, null, "A project path was unavailable.", ReportIncomplete);
            if (isFolder) path = null;
            if (!isFolder && !string.IsNullOrWhiteSpace(path))
            {
                try { path = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { path = null; incomplete = true; AddWarning("A project path could not be normalized."); }
            }
            if (path?.Length > 2048) { path = null; incomplete = true; AddWarning("An over-limit project path is unavailable for selection."); }
            var logicalPath = string.IsNullOrEmpty(parentPath) ? boundedName : parentPath + "\\" + boundedName;
            if (logicalPath.Length > 2048) { logicalPath = string.Empty; incomplete = true; AddWarning("An over-limit logical project path is unavailable."); }
            var identityKey = (path ?? "virtual") + "|" + kind + "|" + logicalPath;
            if (!visited.Add(identityKey)) { incomplete = true; AddWarning("A duplicate or cyclic project entry was skipped."); return; }

            var projectId = _store.NewId();
            var saved = TryRead(() => (bool?)project.Saved, null, "Project saved state is unavailable.", ReportIncomplete);
            var readOnly = path is not null ? TryFileReadOnly(path) : null;
            var record = new ProjectInfoRecord(projectId, boundedName, BoundDisplay(uniqueName, 256), path,
                BoundDisplay(kind, 128), parentId, logicalPath, isFolder, true,
                !isFolder && path is not null && logicalPath.Length > 0, !isFolder && path is not null && logicalPath.Length > 0,
                saved, readOnly, null, null);
            records.Add(record);

            if (!isFolder) return;
            ProjectItems? items;
            try { items = project.ProjectItems; }
            catch (COMException ex) { incomplete = true; AddWarning($"A solution folder could not be enumerated (HRESULT 0x{ex.HResult:X8})."); return; }
            if (items is null) return;
            var count = TryRead(() => items.Count, 0, "A solution-folder item count is unavailable.", ReportIncomplete);
            for (var index = 1; index <= count && nodeCount < MaxNodes; index++)
            {
                CheckDeadline(deadline, cancellationToken);
                try
                {
                    var item = items.Item(index);
                    var subProject = item?.SubProject;
                    if (subProject is not null) WalkProject(subProject, projectId, logicalPath, depth + 1);
                    else { incomplete = true; AddWarning("A non-project solution-folder entry was not included."); }
                }
                catch (COMException ex) { incomplete = true; AddWarning($"A solution-folder child was unavailable (HRESULT 0x{ex.HResult:X8})."); }
            }
        }

        void AddWarning(string warning)
        {
            if (warnings.Count < 20 && !warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning.Length <= 1024 ? warning : warning[..1024]);
        }

        void ReportIncomplete(string warning) { incomplete = true; AddWarning(warning); }
    }

    internal ProjectFilesCapture CaptureFiles(ProjectSnapshotData projectSnapshot, ProjectInfoRecord selectedProject,
        string? expectedSolutionPath, int targetPid, long targetTicks, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var dte = GetBoundDte(targetPid, targetTicks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var solutionPath = solution.FullName;
        if (string.IsNullOrWhiteSpace(solutionPath)) { _store.InvalidateAll(); throw new ProjectWorkspaceException("no_solution", "No solution is open."); }
        solutionPath = Path.GetFullPath(solutionPath);
        _store.ObserveTarget(targetPid, targetTicks, solutionPath);
        ValidateExpectedSolution(solutionPath, expectedSolutionPath);
        if (!PathsEqual(solutionPath, projectSnapshot.SolutionPath)) throw new ProjectWorkspaceException("stale_project", "The solution changed after the project snapshot was captured.");
        var live = FindProject(solution.Projects, selectedProject.ProjectPath, selectedProject.Kind,
            string.Empty, selectedProject.LogicalPath, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), deadline, cancellationToken);
        if (live is null) throw new ProjectWorkspaceException("stale_project", "The selected project is no longer available at its captured identity.");
        if (selectedProject.IsSolutionFolder || selectedProject.ProjectPath is null)
            throw new ProjectWorkspaceException("unsupported_project_type", "A solution folder cannot be enumerated as a project file tree.");

        var records = new List<ProjectFileRecord>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodeCount = 0;
        var incomplete = false;
        var rootItems = live.ProjectItems;
        if (rootItems is not null) WalkItems(rootItems, null, selectedProject.LogicalPath, 0);
        var fileSnapshot = new FileSnapshotData(_store.NewId(), projectSnapshot.SnapshotId, selectedProject.ProjectId,
            targetPid, targetTicks, solutionPath, selectedProject.ProjectPath!, records.ToArray(), incomplete,
            incomplete ? null : records.Count, warnings.ToArray(), DateTimeOffset.UtcNow);
        return new ProjectFilesCapture(fileSnapshot, incomplete, warnings.ToArray());

        void WalkItems(ProjectItems items, string? parentId, string parentLogicalPath, int depth)
        {
            CheckDeadline(deadline, cancellationToken);
            if (depth > MaxDepth) { incomplete = true; AddWarning("Project-item traversal reached the depth limit."); return; }
            var count = TryRead(() => items.Count, 0, "A project-item collection could not be read.", ReportIncomplete);
            for (var index = 1; index <= count; index++)
            {
                CheckDeadline(deadline, cancellationToken);
                if (++nodeCount > MaxNodes) { incomplete = true; AddWarning("Project-item traversal reached the 10000-node limit."); return; }
                ProjectItem? item;
                try { item = items.Item(index); }
                catch (COMException ex) { incomplete = true; AddWarning($"A project item was unavailable (HRESULT 0x{ex.HResult:X8})."); continue; }
                if (item is null) { incomplete = true; AddWarning("An unavailable project item was skipped."); continue; }
                var name = TryRead(() => item.Name, string.Empty, "A project-item name is unavailable.", ReportIncomplete);
                var boundedName = name.Length <= 256 ? name : name[..256];
                var logical = parentLogicalPath + "\\" + boundedName;
                if (logical.Length > 2048) logical = string.Empty;
                var kind = TryRead(() => item.Kind, null, null);
                var rawFileCount = TryRead(() => item.FileCount, 0, "A project-item file count is unavailable.", ReportIncomplete);
                if (rawFileCount > 1024) { incomplete = true; AddWarning("A project item exceeded the associated-file count limit."); }
                var fileCount = Math.Clamp(rawFileCount, 0, 1024);
                var children = TryRead(() => item.ProjectItems, null, "A child project-item collection is unavailable.", ReportIncomplete);
                var rawChildCount = children is null ? 0 : TryRead(() => children.Count, 0, "A child item count is unavailable.", ReportIncomplete);
                if (rawChildCount > 10000) { incomplete = true; AddWarning("A project item exceeded the child-item count limit."); }
                var childCount = Math.Clamp(rawChildCount, 0, 10000);
                var isFolder = fileCount == 0 || childCount > 0;
                var paths = new List<string>();
                var associatedIncomplete = false;
                for (var fileIndex = 1; fileIndex <= fileCount; fileIndex++)
                {
                    try
                    {
                        var file = item.FileNames[checked((short)fileIndex)];
                        if (string.IsNullOrWhiteSpace(file)) { associatedIncomplete = true; continue; }
                        var full = Path.GetFullPath(file);
                        if (full.Length > 2048) { associatedIncomplete = true; continue; }
                        if (paths.Count < 8) paths.Add(full); else associatedIncomplete = true;
                    }
                    catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException or PathTooLongException)
                    { associatedIncomplete = true; }
                }
                var duplicateKey = logical + "|" + string.Join("|", paths);
                if (duplicateKey.Length > 4096 || !visited.Add(duplicateKey)) { incomplete = true; AddWarning("A duplicate or over-limit project item was skipped."); continue; }
                var singlePath = fileCount == 1 && paths.Count == 1 && !isFolder && childCount == 0 ? paths[0] : null;
                var readOnly = singlePath is not null ? TryFileReadOnly(singlePath) : null;
                var record = new ProjectFileRecord(_store.NewId(), boundedName, logical,
                    singlePath, paths.ToArray(), associatedIncomplete, BoundDisplay(kind, 128), parentId,
                    isFolder, fileCount, childCount, null, null, readOnly, null,
                    singlePath is not null && logical.Length > 0 && File.Exists(singlePath) && !isFolder,
                    singlePath is not null && logical.Length > 0 && File.Exists(singlePath) && !isFolder && childCount == 0);
                records.Add(record);
                if (children is not null && childCount > 0) WalkItems(children, record.ItemId, logical, depth + 1);
            }
        }

        void AddWarning(string warning)
        {
            if (warnings.Count < 20 && !warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning.Length <= 1024 ? warning : warning[..1024]);
        }
        void ReportIncomplete(string warning) { incomplete = true; AddWarning(warning); }
    }

    internal ProjectInfoRecord? ReadProjectInfo(ProjectSnapshotData snapshot, ProjectInfoRecord selected,
        string? expectedSolutionPath, int pid, long ticks, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var dte = GetBoundDte(pid, ticks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var path = solution.FullName;
        if (string.IsNullOrWhiteSpace(path)) { _store.InvalidateAll(); throw new ProjectWorkspaceException("no_solution", "No solution is open."); }
        path = Path.GetFullPath(path);
        _store.ObserveTarget(pid, ticks, path);
        ValidateExpectedSolution(path, expectedSolutionPath);
        if (!PathsEqual(path, snapshot.SolutionPath)) throw new ProjectWorkspaceException("stale_project", "The solution changed after the project snapshot was captured.");
        var live = FindProject(solution.Projects, selected.ProjectPath, selected.Kind,
            string.Empty, selected.LogicalPath, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), deadline, cancellationToken);
        if (live is null) return null;
        var project = live;
        if (project is null) return selected with { IsLoaded = false };
        var saved = TryRead(() => (bool?)project.Saved, null, "Project saved state is unavailable.");
        var readOnly = selected.ProjectPath is not null ? TryFileReadOnly(selected.ProjectPath) : null;
        string? configuration = null;
        string? platform = null;
        try
        {
            var active = project.ConfigurationManager?.ActiveConfiguration;
            configuration = BoundDisplay(active?.ConfigurationName, 256);
            platform = BoundDisplay(active?.PlatformName, 256);
        }
        catch (COMException) { }
        return selected with { IsSaved = saved, IsReadOnly = readOnly, IsLoaded = true, Configuration = configuration, Platform = platform };
    }

    internal void VerifySolutionIdentity(int pid, long ticks, string expectedSnapshotPath, string? expectedSolutionPath)
    {
        var dte = GetBoundDte(pid, ticks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var path = solution.FullName;
        if (string.IsNullOrWhiteSpace(path)) { _store.InvalidateAll(); throw new ProjectWorkspaceException("no_solution", "No solution is open."); }
        path = Path.GetFullPath(path);
        _store.ObserveTarget(pid, ticks, path);
        ValidateExpectedSolution(path, expectedSolutionPath);
        if (!PathsEqual(path, expectedSnapshotPath)) throw new ProjectWorkspaceException("stale_project", "The open solution changed after this snapshot was captured.");
    }

    internal static Project? FindProject(Projects projects, string? wantedPath, string? wantedKind,
        string parentLogical, string wantedLogicalPath, int depth, HashSet<string> visited,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        return FindProject(projects, wantedPath, wantedKind, parentLogical, wantedLogicalPath,
            depth, visited, new ProjectTraversalBudget(), deadline, cancellationToken);
    }

    /**
      共有探索予算と要求期限を使って対象プロジェクトを再解決します。
    */
    internal static Project? FindProject(Projects projects, string? wantedPath, string? wantedKind,
        string parentLogical, string wantedLogicalPath, int depth, HashSet<string> visited,
        ProjectTraversalBudget traversal, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        CheckDeadline(deadline, cancellationToken);
        if (depth > MaxDepth) return null;
        var count = TryRead(() => projects.Count, 0, null);
        for (var index = 1; index <= count; index++)
        {
            CheckDeadline(deadline, cancellationToken);
            if (!traversal.TryVisit()) throw TraversalLimitExceeded();
            Project? project;
            try { project = projects.Item(index); }
            catch (COMException) { continue; }
            if (project is null) continue;
            string? path = null; string? kind = null; string? name = null;
            try { path = project.FullName; kind = project.Kind; name = project.Name; }
            catch (COMException) { continue; }
            if (path is not null && Path.IsPathFullyQualified(path))
            {
                try { path = Path.GetFullPath(path); } catch { path = null; }
            }
            var logicalPath = string.IsNullOrEmpty(parentLogical) ? name ?? string.Empty : parentLogical + "\\" + name;
            var key = (path ?? "virtual") + "|" + kind + "|" + logicalPath;
            if (!visited.Add(key)) continue;
            if (PathIdentityMatches(path, wantedPath) && string.Equals(kind, wantedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(logicalPath, wantedLogicalPath, StringComparison.OrdinalIgnoreCase)) return project;
            if (!string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase)) continue;
            ProjectItems? items;
            try { items = project.ProjectItems; } catch (COMException) { continue; }
            if (items is null) continue;
            var childCount = TryRead(() => items.Count, 0, null);
            for (var child = 1; child <= childCount; child++)
            {
                try
                {
                    var subproject = items.Item(child)?.SubProject;
                    if (subproject is not null)
                    {
                        var found = FindProjectInFolder(subproject, wantedPath, wantedKind, wantedLogicalPath,
                            name ?? string.Empty, depth + 1, visited, traversal, deadline, cancellationToken);
                        if (found is not null) return found;
                    }
                }
                catch (COMException) { }
            }
        }
        return null;
    }

    private static Project? FindProjectInFolder(Project project, string? wantedPath, string? wantedKind,
        string wantedLogicalPath, string parentLogical, int depth, HashSet<string> visited,
        ProjectTraversalBudget traversal, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        CheckDeadline(deadline, cancellationToken);
        if (depth > MaxDepth) return null;
        string? name = null; string? kind = null; string? path = null;
        try { name = project.Name; kind = project.Kind; path = project.FullName; } catch (COMException) { return null; }
        var logical = parentLogical + "\\" + name;
        if (string.Equals(logical, wantedLogicalPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kind, wantedKind, StringComparison.OrdinalIgnoreCase) && PathIdentityMatches(path, wantedPath)) return project;
        if (!string.Equals(kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase)) return null;
        ProjectItems? items;
        try { items = project.ProjectItems; } catch (COMException) { return null; }
        if (items is null) return null;
        var count = TryRead(() => items.Count, 0, null);
        for (var i = 1; i <= count; i++)
        {
            CheckDeadline(deadline, cancellationToken);
            if (!traversal.TryVisit()) throw TraversalLimitExceeded();
            try
            {
                var child = items.Item(i)?.SubProject;
                if (child is null) continue;
                var key = child.FullName + "|" + child.Kind + "|" + child.Name;
                if (!visited.Add(key)) continue;
                var found = FindProjectInFolder(child, wantedPath, wantedKind, wantedLogicalPath,
                    logical, depth + 1, visited, traversal, deadline, cancellationToken);
                if (found is not null) return found;
            }
            catch (COMException) { }
        }
        return null;
    }

    private static ProjectWorkspaceException TraversalLimitExceeded() =>
        new("operation_failed", "Project re-resolution exceeded the 10000-node traversal limit.");

    internal DTE2 GetBoundDte(int processId, long startTicks)
    {
        if (!VisualStudioConnector.TryGetTargetProcessIdentity(out var configuredPid, out var configuredTicks))
        {
            _store.InvalidateAll();
            throw new ProjectWorkspaceException("target_identity_required", "The registered Visual Studio process identity is required.");
        }
        if (configuredPid != processId || configuredTicks != startTicks)
        {
            _store.InvalidateAll();
            throw new ProjectWorkspaceException("stale_target", "The requested Visual Studio process identity does not match this server.");
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                _store.InvalidateAll();
                throw new ProjectWorkspaceException("stale_target", "The bound Visual Studio process has exited or its PID was reused.");
            }
        }
        catch (ArgumentException) { _store.InvalidateAll(); throw new ProjectWorkspaceException("stale_target", "The bound Visual Studio process no longer exists."); }
        var dte = VisualStudioConnector.GetActiveVS();
        if (dte is null)
        {
            _store.InvalidateAll();
            throw new ProjectWorkspaceException("dte_disconnected", "The bound Visual Studio automation object is unavailable.");
        }
        return dte;
    }

    private static void ValidateExpectedSolution(string actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        if (!Path.IsPathFullyQualified(expected)) throw new ProjectWorkspaceException("invalid_argument", "Expected solution path must be absolute.");
        if (!PathsEqual(actual, expected)) throw new ProjectWorkspaceException("solution_mismatch", "The open solution does not match expectedSolutionPath.");
    }

    internal static bool PathsEqual(string? first, string? second)
    {
        if (first is null || second is null) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool PathIdentityMatches(string? actual, string? expected) =>
        actual is null && expected is null || PathsEqual(actual, expected);

    private static bool? TryFileReadOnly(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? BoundDisplay(string? value, int limit) => value is null ? null : value.Length <= limit ? value : value[..limit];

    private static T TryRead<T>(Func<T> read, T fallback, string? warning, Action<string>? warningSink = null)
    {
        try { return read(); }
        catch (COMException) { if (warning is not null) warningSink?.Invoke(warning); return fallback; }
        catch (InvalidOperationException) { if (warning is not null) warningSink?.Invoke(warning); return fallback; }
    }

    internal static void CheckDeadline(DateTimeOffset deadline, CancellationToken token)
    {
        ProjectTraversalBudget.CheckDeadline(deadline, token);
    }
}

internal sealed record ProjectFilesCapture(FileSnapshotData Snapshot, bool Incomplete, IReadOnlyList<string> Warnings);

internal sealed class ProjectWorkspaceException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
