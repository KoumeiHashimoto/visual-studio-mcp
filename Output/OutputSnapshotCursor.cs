using Newtonsoft.Json;

namespace VisualStudioMcpServer.Output;

/// <summary>
/// read_output_since の opaque cursor。server の世代と pane identity に束縛する。
/// ProviderCursor は VSIX provider が連続性を証明するための位置で、DTE snapshot の cursor では null。
/// </summary>
internal sealed record OutputSnapshotCursor(string ServerEpoch, string PaneGuid, string CaptureId,
    string? ProviderCursor = null, string? ProviderEpoch = null);

internal static class OutputSnapshotCursorCodec
{
    private static readonly string ServerEpoch = Guid.NewGuid().ToString("N");

    internal static string Create(string paneGuid, string captureId, string? providerCursor = null, string? providerEpoch = null)
    {
        var payload = JsonConvert.SerializeObject(new OutputSnapshotCursor(ServerEpoch, paneGuid, captureId, providerCursor, providerEpoch));
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryRead(string? cursor, string expectedPaneGuid, out bool serverRestarted) =>
        TryRead(cursor, expectedPaneGuid, out serverRestarted, out _);

    internal static bool TryRead(string? cursor, string expectedPaneGuid, out bool serverRestarted, out OutputSnapshotCursor? value)
    {
        serverRestarted = false;
        value = null;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 2048) return false;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var decoded = JsonConvert.DeserializeObject<OutputSnapshotCursor>(payload);
            if (decoded is null || decoded.PaneGuid.Length > 128 || decoded.CaptureId.Length != 32 ||
                decoded.ServerEpoch.Length != 32 || !Guid.TryParseExact(decoded.CaptureId, "N", out _) ||
                !Guid.TryParseExact(decoded.ServerEpoch, "N", out _) ||
                decoded.ProviderCursor is { Length: 0 or > 256 } || decoded.ProviderEpoch is { Length: > 64 } ||
                !string.Equals(decoded.PaneGuid, expectedPaneGuid, StringComparison.OrdinalIgnoreCase)) return false;
            serverRestarted = decoded.ServerEpoch != ServerEpoch;
            value = decoded;
            return true;
        }
        catch { return false; }
    }
}
