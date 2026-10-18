using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;
using VisualStudioMcpServer.Output;

namespace VisualStudioMcpServer.Tools;

[McpServerToolType]
public static class ReadOutputSinceTool
{
    private const int MaximumLines = 2000;
    private const int MaximumCharacters = 32 * 1024;
    private const int MaximumResponseCharacters = 64 * 1024;

    [McpServerTool]
    [Description("Reads a bounded snapshot from an Output Window pane selected by its exact GUID. This provider does not claim incremental delta continuity.")]
    public static string ReadOutputSince(
        [Description("Exact Output Window pane GUID returned by list_output_panes(format='json').")]
        string paneId,
        [Description("Opaque cursor from a previous snapshot. The provider currently resets to a fresh snapshot because it cannot prove delta continuity.")]
        string? cursor = null,
        [Description("Maximum tail lines to capture, defaults to 200 and is capped at 2000.")]
        int maxLines = 200,
        [Description("Maximum UTF-16 code units to capture, defaults to 16384 and is capped at 32768.")]
        int maxCharacters = 16384)
    {
        if (string.IsNullOrWhiteSpace(paneId)) return Error("pane_id_required", "An exact pane GUID is required.");
        if (paneId.Length > 128) return Error("invalid_pane_id", "The pane GUID is invalid.");
        if (maxLines < 1 || maxLines > MaximumLines) return Error("invalid_max_lines", $"maxLines must be between 1 and {MaximumLines}.");
        if (maxCharacters < 1 || maxCharacters > MaximumCharacters)
            return Error("invalid_max_characters", $"maxCharacters must be between 1 and {MaximumCharacters}.");

        var reset = false;
        var serverRestarted = false;
        if (cursor is not null)
        {
            if (!OutputSnapshotCursorCodec.TryRead(cursor, paneId, out serverRestarted))
                return Error("invalid_cursor", "The cursor is malformed or belongs to another pane.");
            // DTE exposes no versioned output-change sequence, so continuity is unknown on every follow-up read.
            reset = true;
        }

        return ReadOnlyToolInvocation.Invoke("read_output_since", () =>
        {
            var snapshot = VisualStudioConnector.ReadOutputPaneSnapshot(paneId, maxLines, maxCharacters);
            if (snapshot is null) return Error("pane_not_found", "No Output Window pane has the requested GUID.");

            var captureId = Guid.NewGuid().ToString("N");
            var result = new OutputSinceResult
            {
                PaneGuid = snapshot.PaneGuid,
                CaptureId = captureId,
                Mode = "snapshot",
                IncrementalSupported = false,
                Gap = reset,
                Reset = reset,
                ServerRestarted = serverRestarted,
                Truncated = snapshot.Truncated,
                OmittedLines = snapshot.OmittedLines,
                ReturnedLines = snapshot.ReturnedLines,
                Text = snapshot.Text,
                Cursor = OutputSnapshotCursorCodec.Create(snapshot.PaneGuid, captureId)
            };

            var json = JsonConvert.SerializeObject(result);
            while (json.Length > MaximumResponseCharacters && result.Text.Length > 0)
            {
                var newLength = Math.Max(0, result.Text.Length - Math.Max(1, result.Text.Length / 8));
                if (newLength > 0 && char.IsHighSurrogate(result.Text[newLength - 1]) && char.IsLowSurrogate(result.Text[newLength]))
                    newLength--;
                result = result with { Text = result.Text[..newLength], Truncated = true };
                json = JsonConvert.SerializeObject(result);
            }
            return json.Length <= MaximumResponseCharacters
                ? json
                : Error("response_too_large", "Output snapshot metadata exceeded the response limit.");
        });
    }

    private static string Error(string code, string message) =>
        JsonConvert.SerializeObject(new { Success = false, Code = code, Message = message });

    private sealed record OutputSinceResult
    {
        public string PaneGuid { get; init; } = string.Empty;
        public string CaptureId { get; init; } = string.Empty;
        public string Mode { get; init; } = "snapshot";
        public bool IncrementalSupported { get; init; }
        public bool Gap { get; init; }
        public bool Reset { get; init; }
        public bool ServerRestarted { get; init; }
        public bool Truncated { get; init; }
        public int OmittedLines { get; init; }
        public int ReturnedLines { get; init; }
        public string Text { get; init; } = string.Empty;
        public string Cursor { get; init; } = string.Empty;
    }
}
