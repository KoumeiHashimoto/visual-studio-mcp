using Newtonsoft.Json;

namespace VisualStudioMcpServer.Output;

internal sealed record OutputSnapshotCursor(string ServerEpoch, string PaneGuid, string CaptureId);

internal static class OutputSnapshotCursorCodec
{
    private static readonly string ServerEpoch = Guid.NewGuid().ToString("N");

    internal static string Create(string paneGuid, string captureId)
    {
        var payload = JsonConvert.SerializeObject(new OutputSnapshotCursor(ServerEpoch, paneGuid, captureId));
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryRead(string? cursor, string expectedPaneGuid, out bool serverRestarted)
    {
        serverRestarted = false;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 2048) return false;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var value = JsonConvert.DeserializeObject<OutputSnapshotCursor>(payload);
            if (value is null || value.PaneGuid.Length > 128 || value.CaptureId.Length != 32 ||
                value.ServerEpoch.Length != 32 || !Guid.TryParseExact(value.CaptureId, "N", out _) ||
                !Guid.TryParseExact(value.ServerEpoch, "N", out _) ||
                !string.Equals(value.PaneGuid, expectedPaneGuid, StringComparison.OrdinalIgnoreCase)) return false;
            serverRestarted = value.ServerEpoch != ServerEpoch;
            return true;
        }
        catch { return false; }
    }
}
