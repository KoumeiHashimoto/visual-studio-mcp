/**
  プロジェクトスナップショットと項目探索の境界を検証します。
*/
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
var actionSource = new FakeProjectItemSource();
var wrongSameName = new FakeProjectItem("same.cs", Path.Combine(Path.GetTempPath(), "wrong", "same.cs"));
var expectedSameName = new FakeProjectItem("same.cs", Path.Combine(Path.GetTempPath(), "expected", "same.cs"));
var projectTree = new FakeProjectItems(new[] { new FakeProjectItem("Folder", null,
    new FakeProjectItems(new[] { wrongSameName, expectedSameName })) });
var actionBudget = new ProjectTraversalBudget();
var selectedActionItem = ProjectItemTraversal.FindItem(projectTree, "Root\\Folder\\same.cs", "Root",
    expectedSameName.FilePath!, 0, actionBudget, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource);
Check(ReferenceEquals(selectedActionItem, expectedSameName), "project-file traversal resolves logical ancestry and exact physical identity");
Check(ProjectItemTraversal.ContainsFile(projectTree, expectedSameName.FilePath!, 0, actionBudget,
    DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource), "membership traversal reuses the action budget");
Check(ProjectItemTraversal.ContainsFile(projectTree, expectedSameName.FilePath!, 0, new ProjectTraversalBudget(),
    DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource), "existing-file membership is available for an add no-op check");
var dependentItem = new FakeProjectItem("Parent.cs", Path.Combine(Path.GetTempPath(), "Parent.cs"),
    new FakeProjectItems(new[] { new FakeProjectItem("Parent.Designer.cs", Path.Combine(Path.GetTempPath(), "Parent.Designer.cs")) }));
Check(ProjectItemTraversal.HasChildren(dependentItem, new ProjectTraversalBudget(), DateTimeOffset.UtcNow.AddSeconds(2),
    CancellationToken.None, actionSource), "project-file removal preflight detects dependent child items");
Check(!ProjectItemTraversal.HasChildren(expectedSameName, new ProjectTraversalBudget(), DateTimeOffset.UtcNow.AddSeconds(2),
    CancellationToken.None, actionSource), "project-file removal preflight accepts a leaf item");

var exhaustedActionBudget = new ProjectTraversalBudget();
for (var i = 0; i < ProjectTraversalBudget.MaxNodes - 1; i++) exhaustedActionBudget.TryVisit();
var finalNode = new FakeProjectItems(new[] { new FakeProjectItem("target.cs", Path.Combine(Path.GetTempPath(), "target.cs")) });
Check(ProjectItemTraversal.FindItem(finalNode, "Root\\target.cs", "Root", Path.Combine(Path.GetTempPath(), "target.cs"),
    0, exhaustedActionBudget, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource) is not null,
    "project-file lookup can consume the final shared node");
var aggregateLimit = false;
try { ProjectItemTraversal.ContainsFile(finalNode, Path.Combine(Path.GetTempPath(), "missing.cs"), 0,
    exhaustedActionBudget, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource); }
catch (ProjectItemTraversalException ex) { aggregateLimit = ex.Code == "operation_failed"; }
Check(aggregateLimit, "postcondition traversal fails when earlier action phases used the shared limit");

var expiredActionDeadline = false;
try { ProjectItemTraversal.FindItem(finalNode, "Root\\target.cs", "Root", Path.Combine(Path.GetTempPath(), "target.cs"),
    0, new ProjectTraversalBudget(), DateTimeOffset.UtcNow.AddMilliseconds(-1), CancellationToken.None, actionSource); }
catch (TimeoutException) { expiredActionDeadline = true; }
Check(expiredActionDeadline, "project-file traversal honors the caller deadline before enumeration");
using (var cancelledAction = new CancellationTokenSource())
{
    cancelledAction.Cancel();
    var cancelledActionTraversal = false;
    try { ProjectItemTraversal.FindItem(finalNode, "Root\\target.cs", "Root", Path.Combine(Path.GetTempPath(), "target.cs"),
        0, new ProjectTraversalBudget(), DateTimeOffset.UtcNow.AddSeconds(2), cancelledAction.Token, actionSource); }
    catch (OperationCanceledException) { cancelledActionTraversal = true; }
    Check(cancelledActionTraversal, "project-file traversal honors caller cancellation before enumeration");
}
var depthTree = new FakeProjectItems(new[] { new FakeProjectItem("leaf.cs", Path.Combine(Path.GetTempPath(), "leaf.cs")) });
for (var i = 0; i <= ProjectTraversalBudget.MaxDepth; i++) depthTree = new FakeProjectItems(new[] { new FakeProjectItem("nested", null, depthTree) });
var depthLimit = false;
try { ProjectItemTraversal.ContainsFile(depthTree, "missing.cs", 0, new ProjectTraversalBudget(),
    DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None, actionSource); }
catch (ProjectItemTraversalException ex) { depthLimit = ex.Code == "operation_failed"; }
Check(depthLimit, "project-file traversal fails explicitly beyond the shared depth limit");
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

var fingerprintPath = Path.Combine(Path.GetTempPath(), "project-file-fingerprint-validation-" + Guid.NewGuid().ToString("N") + ".bin");
try
{
    File.WriteAllBytes(fingerprintPath, new byte[] { 0, 1, 2, 3, 255 });
    var fingerprint = ProjectFileFingerprint.Read(fingerprintPath, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None);
    Check(fingerprint.Length == 5 && fingerprint.Sha256 == "FF5D8507B6A72BEE2DEBCE2C0054798DEACCDC5D8A1B945B6280CE8AA9CBA52E",
        "physical file preservation fingerprint includes content and length");
    File.WriteAllBytes(fingerprintPath, new byte[] { 0, 1, 2, 3, 254 });
    Check(ProjectFileFingerprint.Read(fingerprintPath, DateTimeOffset.UtcNow.AddSeconds(2), CancellationToken.None) != fingerprint,
        "physical file fingerprint detects changed content");
    var expiredFingerprint = false;
    try { ProjectFileFingerprint.Read(fingerprintPath, DateTimeOffset.UtcNow.AddMilliseconds(-1), CancellationToken.None); }
    catch (TimeoutException) { expiredFingerprint = true; }
    Check(expiredFingerprint, "physical file fingerprint honors the caller deadline");
}
finally { File.Delete(fingerprintPath); }

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

/**
  決定的な偽プロジェクト項目コレクションです。
*/
sealed class FakeProjectItems(IReadOnlyList<FakeProjectItem> items)
{
    /**
      コレクション件数を返します。
    */
    internal int Count => items.Count;

    /**
      1始まりの位置で項目を返します。
    */
    internal FakeProjectItem Item(int oneBasedIndex) => items[oneBasedIndex - 1];
}

/**
  決定的な偽プロジェクト項目を保持します。
*/
sealed class FakeProjectItem(string name, string? filePath, FakeProjectItems? children = null)
{
    /**
      項目名を取得します。
    */
    internal string Name { get; } = name;

    /**
      物理ファイルパスを取得します。
    */
    internal string? FilePath { get; } = filePath;

    /**
      子項目コレクションを取得します。
    */
    internal FakeProjectItems? Children { get; } = children;
}

/**
  偽プロジェクト項目を探索アルゴリズムへ公開します。
*/
sealed class FakeProjectItemSource : IProjectItemTraversalSource<FakeProjectItems, FakeProjectItem>
{
    /**
      コレクション件数を返します。
    */
    public int GetCount(FakeProjectItems collection) => collection.Count;

    /**
      1始まりの位置で項目を返します。
    */
    public FakeProjectItem? GetItem(FakeProjectItems collection, int oneBasedIndex) => collection.Item(oneBasedIndex);

    /**
      項目名を返します。
    */
    public string GetName(FakeProjectItem item) => item.Name;

    /**
      項目の物理ファイル数を返します。
    */
    public int GetFileCount(FakeProjectItem item) => item.FilePath is null ? 0 : 1;

    /**
      指定位置の物理ファイル名を返します。
    */
    public string GetFileName(FakeProjectItem item, int oneBasedIndex) => oneBasedIndex == 1 ? item.FilePath ?? string.Empty : throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));

    /**
      子項目コレクションを返します。
    */
    public FakeProjectItems? GetChildren(FakeProjectItem item) => item.Children;
}
