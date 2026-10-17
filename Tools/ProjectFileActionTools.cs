using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EnvDTE;
using EnvDTE80;
using ModelContextProtocol.Server;

namespace VisualStudioMcpServer.Tools;

internal sealed record ProjectFileActionResult(bool Success, string Code, string Message,
    int? TargetProcessId, string? SolutionPath, string? ProjectPath, string? FilePath,
    bool Opened, bool Activated, bool PositionApplied, bool FilePreserved,
    bool RegistrationChanged, long ElapsedMilliseconds);

/// <summary>Opens and changes only project-file registrations captured by the bounded inventory.</summary>
[McpServerToolType]
public sealed class ProjectFileActionTools
{
    private const int MaxResponseCharacters = 65536;
    private static readonly ProjectSnapshotStore Snapshots = ProjectInventoryTools.SnapshotStore;
    private static readonly ProjectWorkspaceConnector Connector = new(Snapshots);
    private static readonly EnvDteProjectItemTraversalSource ItemTraversalSource = new();

    [McpServerTool]
    [System.ComponentModel.Description("Opens one registered project item selected by its file snapshot ID. Expected solution and file paths are required to prevent stale or ambiguous targets. Existing editor buffers are preserved.")]
    public static Task<string> OpenProjectFile(string fileSnapshotId, string itemId,
        string expectedSolutionPath, string expectedFilePath, int line = 0, int column = 0,
        string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) => Task.FromResult(Open(fileSnapshotId, itemId,
            expectedSolutionPath, expectedFilePath, line, column, format, timeoutMilliseconds, cancellationToken));

    [McpServerTool]
    [System.ComponentModel.Description("Registers an existing local file inside a project. Requires explicit allowProjectMutation=true. It never creates, copies, or deletes files.")]
    public static Task<string> AddProjectFile(string projectSnapshotId, string projectId, string filePath,
        string expectedSolutionPath, string expectedProjectPath, bool allowProjectMutation = false,
        string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) => Task.FromResult(Mutate(projectSnapshotId,
            projectId, null, null, filePath, expectedSolutionPath, expectedProjectPath,
            allowProjectMutation, format, timeoutMilliseconds, cancellationToken));

    [McpServerTool]
    [System.ComponentModel.Description("Removes a registered project-file item without deleting the physical file. Requires explicit allowProjectMutation=true.")]
    public static Task<string> RemoveProjectFile(string fileSnapshotId, string itemId,
        string expectedSolutionPath, string expectedProjectPath, string expectedFilePath, bool allowProjectMutation = false,
        string format = "json", int timeoutMilliseconds = 10000,
        CancellationToken cancellationToken = default) => Task.FromResult(Mutate(null, null,
            fileSnapshotId, itemId, expectedFilePath, expectedSolutionPath, expectedProjectPath,
            allowProjectMutation, format, timeoutMilliseconds, cancellationToken));

    private static string Open(string snapshotId, string itemId, string expectedSolutionPath,
        string expectedFilePath, int line, int column, string format, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!ValidFormat(format)) return Format(Result(false, "invalid_format", "Format must be text or json.", null, null, null, null, started));
        if (!TryBoundTarget(out var pid, out var ticks, out var error)) return Format(Result(false, error!, "The bound Visual Studio process identity is required.", null, null, null, null, started));
        if (string.IsNullOrWhiteSpace(snapshotId) || string.IsNullOrWhiteSpace(itemId) || !Absolute(expectedSolutionPath) || !Absolute(expectedFilePath) || line < 0 || column < 0)
            return Format(Result(false, "invalid_argument", "Snapshot ID, item ID, absolute expected paths, and non-negative line and column values are required.", pid, expectedSolutionPath, null, expectedFilePath, started));
        if (!Snapshots.TryGetFiles(snapshotId, out var snapshot) || snapshot is null) return Format(Result(false, "file_snapshot_expired", "The file snapshot expired; request a fresh project-file inventory.", pid, expectedSolutionPath, null, expectedFilePath, started));
        var captured = snapshot.Items.FirstOrDefault(item => item.ItemId == itemId);
        if (captured?.FullPath is null || !ProjectWorkspaceConnector.PathsEqual(captured.FullPath, expectedFilePath))
            return Format(Result(false, "stale_file", "The item is not a unique openable file in this snapshot or its expected path does not match.", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started));
        if (snapshot.TargetProcessId != pid || snapshot.TargetProcessStartUtcTicks != ticks) return Format(Result(false, "stale_target", "The file snapshot belongs to another Visual Studio process identity.", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started));
        try
        {
            var timeout = Math.Clamp(timeoutMilliseconds, 1000, 10000);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
            var result = StaComDispatcher.Invoke(() => OpenOnSta(snapshot, captured, expectedSolutionPath, expectedFilePath, pid, ticks, line, column, deadline, cancellationToken), TimeSpan.FromMilliseconds(timeout), cancellationToken);
            return Format(result with { ElapsedMilliseconds = Elapsed(started) });
        }
        catch (OperationCanceledException) { return Format(Result(false, "cancelled", "The file-open request was cancelled.", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started)); }
        catch (TimeoutException) { return Format(Result(false, "com_timeout", "The file-open request exceeded its deadline.", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started)); }
        catch (ProjectWorkspaceException ex) { return Format(Result(false, ex.Code, ex.Message, pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started)); }
        catch (COMException ex) { return Format(Result(false, "com_error", $"Visual Studio rejected the file-open request (HRESULT 0x{ex.HResult:X8}).", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started)); }
        catch (Exception) { return Format(Result(false, "operation_failed", "The registered project file could not be opened.", pid, expectedSolutionPath, snapshot.ProjectPath, expectedFilePath, started)); }
    }

    private static ProjectFileActionResult OpenOnSta(FileSnapshotData snapshot, ProjectFileRecord captured,
        string expectedSolution, string expectedFile, int pid, long ticks, int line, int column,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
        var traversal = new ProjectTraversalBudget();
        var dte = Connector.GetBoundDte(pid, ticks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var solutionPath = Path.GetFullPath(solution.FullName);
        if (!ProjectWorkspaceConnector.PathsEqual(solutionPath, expectedSolution) || !ProjectWorkspaceConnector.PathsEqual(solutionPath, snapshot.SolutionPath))
            throw new ProjectWorkspaceException("solution_mismatch", "The open solution does not match expectedSolutionPath.");
        if (!Snapshots.TryGetProject(snapshot.ProjectSnapshotId, out var projectSnapshot) || projectSnapshot is null)
            throw new ProjectWorkspaceException("project_snapshot_expired", "The parent project snapshot expired; request fresh inventory.");
        var selectedProject = projectSnapshot.Projects.FirstOrDefault(p => p.ProjectId == snapshot.ProjectId);
        if (selectedProject?.ProjectPath is null) throw new ProjectWorkspaceException("stale_project", "The captured project identity is unavailable.");
        var project = ProjectWorkspaceConnector.FindProject(solution.Projects, selectedProject.ProjectPath, selectedProject.Kind,
            string.Empty, selectedProject.LogicalPath, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), traversal, deadline, cancellationToken);
        if (project is null) throw new ProjectWorkspaceException("stale_project", "The project is no longer registered at its captured path.");
        var item = FindItem(project.ProjectItems, captured.LogicalPath, selectedProject.LogicalPath,
            expectedFile, 0, traversal, deadline, cancellationToken);
        if (item is null) throw new ProjectWorkspaceException("stale_file", "The registered project item no longer matches its captured logical path and file path.");
        ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
        var window = item.Open(Constants.vsViewKindTextView);
        if (window is null) throw new ProjectWorkspaceException("open_failed", "Visual Studio did not return a text editor window for this project item.");
        window.Visible = true;
        var activated = true;
        var positionApplied = line == 0 && column == 0;
        if (line > 0 || column > 0)
        {
            if (line <= 0 || column <= 0) return new ProjectFileActionResult(false, "invalid_position", "Line and column must both be positive when positioning the caret.", pid, solutionPath, snapshot.ProjectPath, expectedFile, true, activated, false, false, false, 0);
            try
            {
                var selection = (TextSelection)window.Document.Selection;
                selection.MoveToLineAndOffset(line, column, false);
                positionApplied = true;
            }
            catch (COMException) { return new ProjectFileActionResult(false, "invalid_position", "The requested line or column is outside the open editor buffer.", pid, solutionPath, snapshot.ProjectPath, expectedFile, true, activated, false, false, false, 0); }
        }
        return new ProjectFileActionResult(true, "ok", "The registered project file is open in the text editor.", pid, solutionPath, snapshot.ProjectPath, expectedFile, true, activated, positionApplied, false, false, 0);
    }

    private static string Mutate(string? projectSnapshotId, string? projectId, string? fileSnapshotId,
        string? itemId, string? filePath, string expectedSolutionPath, string expectedProjectPath,
        bool allowMutation, string format, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!ValidFormat(format)) return Format(Result(false, "invalid_format", "Format must be text or json.", null, null, null, filePath, started));
        if (!allowMutation) return Format(Result(false, "mutation_not_allowed", "Set allowProjectMutation=true to change project registration.", null, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (!TryBoundTarget(out var pid, out var ticks, out var error)) return Format(Result(false, error!, "The bound Visual Studio process identity is required.", null, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (!Absolute(expectedSolutionPath) || !Absolute(expectedProjectPath)) return Format(Result(false, "invalid_argument", "Expected solution and project paths must be absolute.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        ProjectSnapshotData? projects = null; ProjectInfoRecord? selected = null; FileSnapshotData? files = null; ProjectFileRecord? selectedItem = null;
        if (fileSnapshotId is null)
        {
            if (!Snapshots.TryGetProject(projectSnapshotId ?? "", out projects) || projects is null) return Format(Result(false, "project_snapshot_expired", "The project snapshot expired; request a fresh inventory.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
            selected = projects.Projects.FirstOrDefault(p => p.ProjectId == projectId);
        }
        else
        {
            if (!Snapshots.TryGetFiles(fileSnapshotId, out files) || files is null) return Format(Result(false, "file_snapshot_expired", "The file snapshot expired; request a fresh project-file inventory.", pid, expectedSolutionPath, expectedProjectPath, null, started));
            selectedItem = files.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (selectedItem?.FullPath is null || !ProjectWorkspaceConnector.PathsEqual(selectedItem.FullPath, filePath)) return Format(Result(false, "stale_file", "The item does not match the required expectedFilePath.", pid, expectedSolutionPath, files.ProjectPath, filePath, started));
            if (!Snapshots.TryGetProject(files.ProjectSnapshotId, out projects) || projects is null) return Format(Result(false, "project_snapshot_expired", "The parent project snapshot expired; request fresh inventory.", pid, expectedSolutionPath, files.ProjectPath, selectedItem?.FullPath, started));
            selected = projects.Projects.FirstOrDefault(p => p.ProjectId == files.ProjectId);
        }
        if (selected is null || selected.ProjectPath is null || selected.IsSolutionFolder) return Format(Result(false, "stale_project", "The selected project is unavailable or is a solution folder.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (!ProjectWorkspaceConnector.PathsEqual(selected.ProjectPath, expectedProjectPath)) return Format(Result(false, "project_mismatch", "The selected project does not match expectedProjectPath.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (projects!.TargetProcessId != pid || projects.TargetProcessStartUtcTicks != ticks || (files is not null && (files.TargetProcessId != pid || files.TargetProcessStartUtcTicks != ticks))) return Format(Result(false, "stale_target", "The snapshot belongs to another Visual Studio process identity.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (fileSnapshotId is not null && (selectedItem?.FullPath is null || selectedItem.IsFolder || selectedItem.FileCount != 1)) return Format(Result(false, "ambiguous_file", "Only an unambiguous single-file project item can be removed.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (fileSnapshotId is null && !SafeLocalFile(selected.ProjectPath, filePath ?? string.Empty)) return Format(Result(false, "invalid_file_path", "The file must be an existing local regular file inside the project directory and outside generated or metadata directories.", pid, expectedSolutionPath, expectedProjectPath, filePath, started));
        if (!WorkspaceMutationGate.Shared.TryAcquire()) return Format(Result(false, "workspace_busy", "Another build or project mutation is in progress.", pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started));
        var transferLease = false;
        try
        {
            var timeout = Math.Clamp(timeoutMilliseconds, 1000, 10000);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
            var result = StaComDispatcher.Invoke(() => MutateOnSta(selected, selectedItem, fileSnapshotId is null ? filePath : null,
                expectedSolutionPath, expectedProjectPath, pid, ticks, deadline, cancellationToken), TimeSpan.FromMilliseconds(timeout), cancellationToken);
            if (result.Success || !result.FilePreserved) Snapshots.InvalidateProject(projects.SnapshotId);
            return Format(result with { ElapsedMilliseconds = Elapsed(started) });
        }
        catch (OperationCanceledException)
        {
            transferLease = true;
            ReleaseAfterQueuedCleanup();
            Snapshots.InvalidateProject(projects.SnapshotId);
            return Format(Result(false, "outcome_unknown", "The request was cancelled while Visual Studio may still be processing it; the workspace remains locked until the queued operation settles. Refresh inventory before retrying.", pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started));
        }
        catch (TimeoutException)
        {
            transferLease = true;
            ReleaseAfterQueuedCleanup();
            Snapshots.InvalidateProject(projects.SnapshotId);
            return Format(Result(false, "outcome_unknown", "The Visual Studio operation exceeded its deadline; the workspace remains locked until the queued operation settles. Refresh inventory before retrying.", pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started));
        }
        catch (ProjectWorkspaceException ex)
        {
            if (ex.Code is "outcome_unknown" or "registration_failed") Snapshots.InvalidateProject(projects.SnapshotId);
            return Format(Result(false, ex.Code, ex.Message, pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started));
        }
        catch (COMException ex) { Snapshots.InvalidateProject(projects.SnapshotId); return Format(Result(false, "outcome_unknown", $"Visual Studio returned HRESULT 0x{ex.HResult:X8}; refresh inventory before retrying.", pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started)); }
        catch (Exception) { Snapshots.InvalidateProject(projects.SnapshotId); return Format(Result(false, "outcome_unknown", "The project-file operation could not be verified; refresh inventory before retrying.", pid, expectedSolutionPath, expectedProjectPath, filePath ?? selectedItem?.FullPath, started)); }
        finally { if (!transferLease) WorkspaceMutationGate.Shared.Release(); }
    }

    private static void ReleaseAfterQueuedCleanup()
    {
        try { WorkspaceMutationGate.Shared.ReleaseAfter(StaComDispatcher.Post(static () => { })); }
        catch { /* Keep the lease closed if the STA cannot accept its cleanup marker. */ }
    }

    private static ProjectFileActionResult MutateOnSta(ProjectInfoRecord selected, ProjectFileRecord? selectedItem,
        string? filePath, string expectedSolution, string expectedProject, int pid, long ticks,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
        var traversal = new ProjectTraversalBudget();
        var dte = Connector.GetBoundDte(pid, ticks);
        var solution = dte.Solution ?? throw new ProjectWorkspaceException("no_solution", "No solution is open.");
        var solutionPath = Path.GetFullPath(solution.FullName);
        if (!ProjectWorkspaceConnector.PathsEqual(solutionPath, expectedSolution)) throw new ProjectWorkspaceException("solution_mismatch", "The open solution does not match expectedSolutionPath.");
        ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
        if (dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) throw new ProjectWorkspaceException("debugger_active", "Project registration is allowed only while the debugger is in design mode.");
        if (solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress) throw new ProjectWorkspaceException("build_in_progress", "Wait for the active solution build to finish before changing project registration.");
        var project = ProjectWorkspaceConnector.FindProject(solution.Projects, expectedProject, selected.Kind,
            string.Empty, selected.LogicalPath, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), traversal, deadline, cancellationToken);
        if (project is null) throw new ProjectWorkspaceException("stale_project", "The project no longer matches its captured path and ancestry.");
        try
        {
            if (!project.Saved) throw new ProjectWorkspaceException("project_dirty", "Save or discard project-file changes before changing registration.");
        }
        catch (ProjectWorkspaceException) { throw; }
        catch (COMException) { throw new ProjectWorkspaceException("unsupported_state", "Visual Studio could not report the project's saved state."); }
        var documentStates = new List<bool?>();
        try
        {
            var documents = dte.Documents;
            var documentCount = documents.Count;
            ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
            if (documentCount > 10000) throw new ProjectWorkspaceException("unsupported_state", "The open document list is too large to verify safely.");
            for (var index = 1; index <= documentCount; index++)
            {
                ProjectItemTraversal.VisitTraversalNode(traversal, deadline, cancellationToken);
                try { documentStates.Add(documents.Item(index).Saved); }
                catch (COMException) { documentStates.Add(null); }
                ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
            }
            if (documents.Count != documentCount) throw new ProjectWorkspaceException("unsupported_state", "The open document list changed during the registration preflight.");
        }
        catch (ProjectWorkspaceException) { throw; }
        catch (COMException) { throw new ProjectWorkspaceException("unsupported_state", "Visual Studio could not report the saved state of every open document."); }
        var documentStateError = ProjectMutationPreflight.GetError(documentStates);
        if (documentStateError == "document_dirty") throw new ProjectWorkspaceException("document_dirty", "Save or discard all unsaved editor documents before changing project registration.");
        if (documentStateError is not null) throw new ProjectWorkspaceException("unsupported_state", "Visual Studio could not report the saved state of every open document.");
        bool changed;
        if (filePath is not null)
        {
            ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
            try { project.ProjectItems.AddFromFile(Path.GetFullPath(filePath)); }
            catch (COMException ex) { throw new ProjectWorkspaceException("registration_failed", $"The project system rejected file registration (HRESULT 0x{ex.HResult:X8})."); }
            changed = ContainsFileAfterMutation(project.ProjectItems, filePath, traversal, deadline, cancellationToken);
            if (!changed) throw new ProjectWorkspaceException("outcome_unknown", "The add operation returned without verifiable project membership.");
        }
        else
        {
            var live = FindItem(project.ProjectItems, selectedItem!.LogicalPath, selected.LogicalPath,
                selectedItem.FullPath!, 0, traversal, deadline, cancellationToken);
            if (live is null) throw new ProjectWorkspaceException("stale_file", "The captured project item is no longer registered at the expected path.");
            ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
            live.Remove();
            changed = !ContainsFileAfterMutation(project.ProjectItems, selectedItem.FullPath!, traversal, deadline, cancellationToken) && File.Exists(selectedItem.FullPath!);
            if (!changed) throw new ProjectWorkspaceException("outcome_unknown", "Item removal could not be verified or the physical file is missing.");
        }
        ProjectWorkspaceConnector.CheckDeadline(deadline, cancellationToken);
        project.Save(expectedProject);
        return new ProjectFileActionResult(true, "ok", filePath is null ? "The project item was removed and its physical file was preserved." : "The existing file was registered in the project.", pid, solutionPath, expectedProject, filePath ?? selectedItem?.FullPath, false, false, false, filePath is null && File.Exists(selectedItem!.FullPath!), true, 0);
    }

    private static bool ContainsFileAfterMutation(ProjectItems? items, string path, ProjectTraversalBudget traversal,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        try { return ProjectItemTraversal.ContainsFile(items, path, 0, traversal, deadline, cancellationToken, ItemTraversalSource); }
        catch (Exception) { throw new ProjectWorkspaceException("outcome_unknown", "The registration changed, but bounded membership verification did not finish; refresh inventory before retrying."); }
    }

    private static ProjectItem? FindItem(ProjectItems? items, string logicalPath, string parent, string expectedPath, int depth,
        ProjectTraversalBudget traversal, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        try { return ProjectItemTraversal.FindItem(items, logicalPath, parent, expectedPath, depth, traversal, deadline, cancellationToken, ItemTraversalSource); }
        catch (ProjectItemTraversalException ex) { throw new ProjectWorkspaceException(ex.Code, ex.Message); }
    }

    private static bool SafeLocalFile(string projectPath, string input)
    {
        if (!Absolute(input) || !File.Exists(input)) return false;
        try
        {
            var full = Path.GetFullPath(input);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (ProjectWorkspaceConnector.PathsEqual(full, projectPath)) return false;
            if (projectDirectory is null || !full.StartsWith(projectDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            if ((attributes & FileAttributes.ReadOnly) != 0) return false;
            var relative = Path.GetRelativePath(projectDirectory, full);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.ToLowerInvariant() is ".git" or ".vs" or "bin" or "obj")) return false;
            var current = full;
            while (current.Length >= projectDirectory.Length)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (ProjectWorkspaceConnector.PathsEqual(current, projectDirectory)) break;
                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current) break;
                current = parent;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryBoundTarget(out int pid, out long ticks, out string? error)
    {
        if (VisualStudioConnector.TryGetTargetProcessIdentity(out pid, out ticks)) { error = null; return true; }
        error = "target_identity_required"; pid = 0; ticks = 0; return false;
    }
    private static bool Absolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    private static bool ValidFormat(string format) => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "text", StringComparison.OrdinalIgnoreCase);
    private static long Elapsed(long start) => (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    private static ProjectFileActionResult Result(bool success, string code, string message, int? pid, string? solution, string? project, string? file, long start) =>
        new(success, code, message, pid, solution, project, file, false, false, false, false, false, Elapsed(start));
    private static string Format(ProjectFileActionResult result)
    {
        if (JsonSerializer.Serialize(result).Length > MaxResponseCharacters) result = result with { Message = "The result exceeded the response size limit." };
        return JsonSerializer.Serialize(result);
    }
}
