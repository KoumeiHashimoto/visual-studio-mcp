using VisualStudioMcpServer;
using VisualStudioMcpServer.Tools;

var now = DateTimeOffset.UnixEpoch;
var checkCount = 0;
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
