using System.Text;
using System.Text.Json;
using VisualStudioMcpServer.Tools;

namespace VisualStudioMcpServer;

/// <summary>Bounded process-local store of immutable solution/project/file snapshots and opaque cursors.</summary>
internal sealed class ProjectSnapshotStore
{
    internal const int MaxSnapshots = 8;
    internal const long MaxAggregateBytes = 16L * 1024 * 1024;
    internal const long MaxSnapshotBytes = 4L * 1024 * 1024;
    internal const int MaxCursors = 4096;
    internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CursorEntry> _cursors = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;
    private long _lru;
    private TargetIdentity? _currentTarget;

    internal ProjectSnapshotStore(Func<DateTimeOffset>? utcNow = null) => _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    internal string NewId() => Convert.ToBase64String(Guid.NewGuid().ToByteArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal bool TryPut(ProjectSnapshotData snapshot) => Put(snapshot.SnapshotId, snapshot);
    internal bool TryPut(FileSnapshotData snapshot) => Put(snapshot.SnapshotId, snapshot);

    internal bool TryGetProject(string id, out ProjectSnapshotData? snapshot)
    {
        lock (_gate)
        {
            Sweep();
            if (_snapshots.TryGetValue(id, out var entry) && entry.Value is ProjectSnapshotData value)
            {
                entry.LastUse = ++_lru;
                snapshot = value;
                return true;
            }
            snapshot = null;
            return false;
        }
    }

    internal bool TryGetFiles(string id, out FileSnapshotData? snapshot)
    {
        lock (_gate)
        {
            Sweep();
            if (_snapshots.TryGetValue(id, out var entry) && entry.Value is FileSnapshotData value)
            {
                entry.LastUse = ++_lru;
                snapshot = value;
                return true;
            }
            snapshot = null;
            return false;
        }
    }

    internal string AddCursor(SnapshotCursor cursor)
    {
        lock (_gate)
        {
            Sweep();
            while (_cursors.Count >= MaxCursors)
            {
                var oldest = _cursors.MinBy(pair => pair.Value.LastUse).Key;
                _cursors.Remove(oldest);
            }
            var id = NewId();
            _cursors[id] = new CursorEntry(cursor, ++_lru, _utcNow());
            return id;
        }
    }

    internal bool TryGetCursor(string id, out SnapshotCursor? cursor)
    {
        lock (_gate)
        {
            Sweep();
            if (id.Length <= 128 && _cursors.TryGetValue(id, out var entry))
            {
                entry.LastUse = ++_lru;
                cursor = entry.Cursor;
                return true;
            }
            cursor = null;
            return false;
        }
    }

    internal void InvalidateAll()
    {
        lock (_gate) { _snapshots.Clear(); _cursors.Clear(); _currentTarget = null; }
    }

    internal void ObserveTarget(int processId, long startTicks, string solutionPath)
    {
        lock (_gate)
        {
            var current = new TargetIdentity(processId, startTicks, Normalize(solutionPath));
            if (_currentTarget is not null && _currentTarget != current)
            {
                _snapshots.Clear();
                _cursors.Clear();
            }
            _currentTarget = current;
        }
    }

    internal void InvalidateProject(string projectSnapshotId)
    {
        lock (_gate)
        {
            var invalidIds = _snapshots.Where(pair => pair.Key == projectSnapshotId ||
                pair.Value.Value is FileSnapshotData files && files.ProjectSnapshotId == projectSnapshotId)
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var id in invalidIds) _snapshots.Remove(id);
            foreach (var id in _cursors.Where(pair => invalidIds.Contains(pair.Value.Cursor.SnapshotId))
                .Select(pair => pair.Key).ToArray()) _cursors.Remove(id);
        }
    }

    internal (int Count, long Bytes) GetUsage()
    {
        lock (_gate) { Sweep(); return (_snapshots.Count, _snapshots.Values.Sum(entry => entry.Bytes)); }
    }

    private bool Put(string id, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value).LongLength;
        if (bytes > MaxSnapshotBytes) return false;
        lock (_gate)
        {
            Sweep();
            if (_snapshots.Remove(id, out var replaced)) { }
            while (_snapshots.Count >= MaxSnapshots || _snapshots.Values.Sum(entry => entry.Bytes) + bytes > MaxAggregateBytes)
            {
                var oldest = _snapshots.MinBy(pair => pair.Value.LastUse).Key;
                RemoveSnapshotAndCursors(oldest);
            }
            _snapshots[id] = new Entry(value, bytes, ++_lru,
                value is ProjectSnapshotData project ? project.CreatedAt : ((FileSnapshotData)value).CreatedAt);
            return true;
        }
    }

    private void Sweep()
    {
        var now = _utcNow();
        foreach (var id in _snapshots.Where(pair => now - pair.Value.CreatedAt >= SnapshotLifetime)
            .Select(pair => pair.Key).ToArray()) RemoveSnapshotAndCursors(id);
        foreach (var id in _cursors.Where(pair => now - pair.Value.CreatedAt >= SnapshotLifetime ||
            !_snapshots.ContainsKey(pair.Value.Cursor.SnapshotId)).Select(pair => pair.Key).ToArray()) _cursors.Remove(id);
    }

    private void RemoveSnapshotAndCursors(string id)
    {
        _snapshots.Remove(id);
        foreach (var cursor in _cursors.Where(pair => pair.Value.Cursor.SnapshotId == id)
            .Select(pair => pair.Key).ToArray()) _cursors.Remove(cursor);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch { return path.ToUpperInvariant(); }
    }

    private sealed record TargetIdentity(int ProcessId, long StartTicks, string SolutionPath);

    private sealed class Entry(object value, long bytes, long lastUse, DateTimeOffset createdAt)
    {
        public object Value { get; } = value;
        public long Bytes { get; } = bytes;
        public long LastUse { get; set; } = lastUse;
        public DateTimeOffset CreatedAt { get; } = createdAt;
    }

    private sealed class CursorEntry(SnapshotCursor cursor, long lastUse, DateTimeOffset createdAt)
    {
        public SnapshotCursor Cursor { get; } = cursor;
        public long LastUse { get; set; } = lastUse;
        public DateTimeOffset CreatedAt { get; } = createdAt;
    }
}
