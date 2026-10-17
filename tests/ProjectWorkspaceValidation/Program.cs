using VisualStudioMcpServer;
using VisualStudioMcpServer.Tools;

var now = DateTimeOffset.UnixEpoch;
var checkCount = 0;
var traversalBudget = new ProjectTraversalBudget();
for (var i = 0; i < ProjectTraversalBudget.MaxNodes; i++)
    if (!traversalBudget.TryVisit()) throw new InvalidOperationException("Project traversal rejected a node before its limit.");
Check(traversalBudget.VisitedCount == ProjectTraversalBudget.MaxNodes, "project traversal counts each accepted node");
Check(!traversalBudget.TryVisit(), "project traversal rejects the first node beyond its limit");
var sharedActionBudget = new ProjectTraversalBudget();
for (var i = 0; i < ProjectTraversalBudget.MaxNodes - 2; i++)
    if (!sharedActionBudget.TryVisit()) throw new InvalidOperationException("Shared project-file action budget ended early.");
Check(sharedActionBudget.TryVisit(), "project-file action can share one budget across project and item lookup");
Check(sharedActionBudget.TryVisit(), "project-file action can account for document or postcondition traversal in the same budget");
Check(!sharedActionBudget.TryVisit(), "project-file action rejects aggregate traversal beyond the shared limit");
var store = new ProjectSnapshotStore(() => now);
var project = CreateProject("project-a", new string('A', 8));
Check(store.TryPut(project), "project snapshot stored");
Check(store.TryGetProject(project.SnapshotId, out var fetched) && fetched == project, "immutable project snapshot retrieved");

var cursor = store.AddCursor(new SnapshotCursor("projects", project.SnapshotId, null, "C:\\solution.sln", 20));
Check(store.TryGetCursor(cursor, out var cursorValue) && cursorValue!.Offset == 20, "opaque cursor maps to a stable offset");
Check(!new ProjectSnapshotStore(() => now).TryGetCursor(cursor, out _), "cursor is process local");
Check(store.TryGetCursor(cursor, out cursorValue) && cursorValue!.QueryKey == "C:\\solution.sln", "cursor remains bound to its query");

var files = CreateFiles("files-a", project.SnapshotId, "project-a");
Check(store.TryPut(files), "file snapshot stored");
var fileCursor = store.AddCursor(new SnapshotCursor("files", files.SnapshotId, files.ProjectId, "query", 100));
Check(store.TryGetFiles(files.SnapshotId, out var fileRead) && fileRead!.ProjectSnapshotId == project.SnapshotId, "file snapshot retains project identity");
Check(store.TryGetCursor(fileCursor, out var fileCursorRead) && fileCursorRead!.ProjectId == "project-a", "file cursor retains project identity");

store.InvalidateProject(project.SnapshotId);
Check(!store.TryGetProject(project.SnapshotId, out _), "project invalidation removes project snapshot");
Check(!store.TryGetFiles(files.SnapshotId, out _), "project invalidation removes dependent file snapshots");
Check(!store.TryGetCursor(fileCursor, out _), "project invalidation removes dependent cursors");

var lru = new ProjectSnapshotStore(() => now);
var snapshots = Enumerable.Range(0, ProjectSnapshotStore.MaxSnapshots + 1)
    .Select(index => CreateProject("p" + index, new string((char)('a' + index), 8))).ToArray();
foreach (var snapshot in snapshots) Check(lru.TryPut(snapshot), "snapshot fits retention limits");
Check(!lru.TryGetProject(snapshots[0].SnapshotId, out _), "ninth snapshot evicts least recently used");
Check(lru.TryGetProject(snapshots[^1].SnapshotId, out _), "newest snapshot retained");
Check(lru.GetUsage().Count == ProjectSnapshotStore.MaxSnapshots, "snapshot count is bounded");

var expiring = new ProjectSnapshotStore(() => now);
var expiringProject = CreateProject("expiring", new string('E', 8));
expiring.TryPut(expiringProject);
var expiryCursor = expiring.AddCursor(new SnapshotCursor("projects", expiringProject.SnapshotId, null, "", 1));
now += ProjectSnapshotStore.SnapshotLifetime;
Check(!expiring.TryGetProject(expiringProject.SnapshotId, out _), "snapshot expires after five minutes");
Check(!expiring.TryGetCursor(expiryCursor, out _), "cursor expires with its snapshot");

var oversized = new ProjectSnapshotData("oversized", 42, 638900000000000000, @"C:\solution.sln",
    Enumerable.Range(0, 3000).Select(index => new ProjectInfoRecord("id-" + index,
        new string('X', 2048), null, @"C:\project.csproj", "cs", null, "logical", false,
        true, true, true, true, false, null, null)).ToArray(), false, 3000,
    Array.Empty<string>(), DateTimeOffset.UnixEpoch);
Check(!new ProjectSnapshotStore(() => now).TryPut(oversized), "oversized snapshot rejected");

var mutationCalls = 0;
var dirtyStateError = ProjectMutationPreflight.GetError(new bool?[] { true, false });
if (dirtyStateError is null) mutationCalls++;
Check(dirtyStateError == "document_dirty", "dirty open documents block project registration changes");
Check(mutationCalls == 0, "dirty document preflight prevents registration and save calls");

var unknownStateError = ProjectMutationPreflight.GetError(new bool?[] { true, null });
Check(unknownStateError == "unsupported_state", "unavailable document state fails closed");

Check(ProjectMutationPreflight.GetError(new bool?[] { true, true }) is null, "clean open documents allow project registration preflight");

Console.WriteLine($"PASS {checkCount} ProjectWorkspaceValidation checks");

static ProjectSnapshotData CreateProject(string id, string name) => new(id, 42, 638900000000000000,
    @"C:\solution.sln", new[] { new ProjectInfoRecord(id, name, null, @"C:\project.csproj", "cs", null,
        name, false, true, true, true, true, false, null, null) }, false, 1, Array.Empty<string>(), DateTimeOffset.UnixEpoch);

static FileSnapshotData CreateFiles(string id, string projectSnapshotId, string projectId) => new(id,
    projectSnapshotId, projectId, 42, 638900000000000000, @"C:\solution.sln", @"C:\project.csproj",
    Array.Empty<ProjectFileRecord>(), false, 0, Array.Empty<string>(), DateTimeOffset.UnixEpoch);

void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    checkCount++;
}
