using System.Security.Cryptography;
using System.Text;

namespace VisualStudioMcpServer.Diagnostics;

internal sealed record DiagnosticSnapshotItem(
    string Severity,
    string Description,
    string FileName,
    int Line,
    int Column,
    string Project);

internal sealed record DiagnosticSnapshotPage(
    string CaptureId,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<DiagnosticSnapshotItem> Items,
    int TotalItems,
    int Offset,
    bool CaptureComplete,
    string? NextCursor);

internal sealed class DiagnosticSnapshotStore
{
    private const int MaximumSnapshots = 8;
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);

    internal string Capture(string workspaceIdentity, IEnumerable<DiagnosticSnapshotItem> items,
        bool captureComplete, DateTimeOffset now)
    {
        var id = Guid.NewGuid().ToString("N");
        var snapshot = new Snapshot(id, workspaceIdentity, now, items.Take(500).ToArray(), captureComplete);
        lock (_gate)
        {
            RemoveExpired(now);
            while (_snapshots.Count >= MaximumSnapshots)
            {
                var oldest = _snapshots.Values.OrderBy(value => value.CapturedAtUtc).First();
                _snapshots.Remove(oldest.Id);
            }
            _snapshots.Add(id, snapshot);
        }
        return id;
    }

    internal bool TryRead(string captureId, string workspaceIdentity, string? severity, string? file,
        string? source, string filterFingerprint, int offset, int pageSize, DateTimeOffset now,
        out DiagnosticSnapshotPage? page)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            if (!_snapshots.TryGetValue(captureId, out var snapshot) ||
                !string.Equals(snapshot.WorkspaceIdentity, workspaceIdentity, StringComparison.OrdinalIgnoreCase))
            {
                page = null;
                return false;
            }

            var filtered = snapshot.Items.Where(item => Matches(item, severity, file, source)).ToArray();
            if (offset < 0 || offset > filtered.Length)
            {
                page = null;
                return false;
            }

            var items = filtered.Skip(offset).Take(pageSize).ToArray();
            var nextOffset = offset + items.Length;
            page = new DiagnosticSnapshotPage(snapshot.Id, snapshot.CapturedAtUtc, items,
                filtered.Length, offset, snapshot.CaptureComplete,
                nextOffset < filtered.Length ? CreateCursor(snapshot.Id, nextOffset, filterFingerprint) : null);
            return true;
        }
    }

    internal static string FilterFingerprint(string? severity, string? file, string? source)
    {
        var canonical = string.Join("\n", Normalize(severity), Normalize(file), Normalize(source));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string CreateCursorForOffset(string captureId, int offset, string filterFingerprint) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"d1:{captureId}:{filterFingerprint}:{offset}"));

    internal static bool TryReadCursor(string? cursor, out string captureId, out int offset,
        out string filterFingerprint)
    {
        captureId = string.Empty;
        offset = 0;
        filterFingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 256) return false;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = value.Split(':');
            if (parts.Length != 4 || parts[0] != "d1" || parts[1].Length != 32 ||
                !Guid.TryParseExact(parts[1], "N", out _) ||
                parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit) ||
                !int.TryParse(parts[3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out offset) || offset <= 0)
                return false;
            captureId = parts[1];
            filterFingerprint = parts[2];
            return true;
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool Matches(DiagnosticSnapshotItem item, string? severity, string? file, string? source) =>
        (string.IsNullOrWhiteSpace(severity) || string.Equals(item.Severity, severity, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(file) || item.FileName.Contains(file, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(source) || string.Equals("unknown", source, StringComparison.OrdinalIgnoreCase));

    private static string CreateCursor(string captureId, int offset, string filterFingerprint) =>
        CreateCursorForOffset(captureId, offset, filterFingerprint);

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var id in _snapshots.Values.Where(value => now - value.CapturedAtUtc > TimeToLive)
                     .Select(value => value.Id).ToArray())
            _snapshots.Remove(id);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private sealed record Snapshot(string Id, string WorkspaceIdentity, DateTimeOffset CapturedAtUtc,
        IReadOnlyList<DiagnosticSnapshotItem> Items, bool CaptureComplete);
}
