using VisualStudioMcpServer.Workspace;

var checks = 0;
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "workspace-fixture"));
var store = new ActiveWorkspaceSnapshotStore(42, 638900000000000000);
var current = Snapshot(1, root);
Check(store.TryUpdate(current), "active public workspace snapshot accepted");
Check(store.GetCurrent() == current, "active workspace snapshot retrieved");
Check(!store.TryUpdate(current with { VisualStudioPid = 43, Generation = 2 }), "wrong Visual Studio PID rejected");
Check(!store.TryUpdate(current with { VisualStudioStartTicks = 638900000000000001, Generation = 2 }), "wrong Visual Studio start time rejected");
Check(!store.TryUpdate(current with { Root = "relative-folder", Generation = 2 }), "relative workspace root rejected");
Check(!store.TryUpdate(current with { WorkspaceKind = "cmakeOpenFolder", Generation = 2 }), "unproven CMake classification rejected");
Check(!store.TryUpdate(current with { CMakeMetadataStatus = "available", Generation = 2 }), "undocumented CMake metadata rejected");
Check(!store.TryUpdate(current with { Root = new string('x', ActiveWorkspaceSnapshotStore.MaxRootLength + 1), Generation = 2 }), "oversized workspace root rejected");

var closed = new ActiveWorkspaceSnapshot(false, 42, 638900000000000000, 2, "", "", "", "");
Check(store.TryUpdate(closed), "workspace close snapshot accepted");
Check(store.GetCurrent() is null, "workspace close clears active metadata");
Check(!store.TryUpdate(current), "stale workspace event cannot restore a closed workspace");
Check(!store.TryUpdate(Snapshot(2, Path.Combine(root, "other"))), "conflicting same-generation update rejected");
Check(store.TryUpdate(Snapshot(3, root)), "newer workspace generation accepted after close");

Console.WriteLine($"PASS {checks} WorkspaceSnapshotValidation checks");

ActiveWorkspaceSnapshot Snapshot(long generation, string path) => new(true, 42, 638900000000000000,
    generation, path, "openFolder", "unsupported", "The public Workspace API does not identify CMake metadata.");

void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    checks++;
}
