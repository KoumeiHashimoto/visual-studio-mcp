using ModelContextProtocol.Server;
using Newtonsoft.Json;
using System.ComponentModel;
using VisualStudioMcpServer.Output;
using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Tools;

[McpServerToolType]
public static class ReadOutputSinceTool
{
    private const int MaximumLines = 2000;
    private const int MaximumCharacters = 32 * 1024;
    private const int MaximumResponseCharacters = 64 * 1024;

    [McpServerTool]
    [Description("Reads Output Window text from a pane selected by its exact GUID. Through the VSIX provider, a cursor returns Mode=delta only when the pane buffer is proven to have only been appended since that cursor; otherwise it returns a fresh bounded snapshot with Gap/Reset. The DTE fallback never claims incremental continuity.")]
    public static async Task<string> ReadOutputSince(
        [Description("Exact Output Window pane GUID returned by list_output_panes(format='json').")]
        string paneId,
        [Description("Opaque cursor from a previous read of the same pane. A delta is returned only when continuity is proven.")]
        string? cursor = null,
        [Description("Maximum lines to return, defaults to 200 and is capped at 2000.")]
        int maxLines = 200,
        [Description("Maximum UTF-16 code units to return, defaults to 16384 and is capped at 32768.")]
        int maxCharacters = 16384)
    {
        if (string.IsNullOrWhiteSpace(paneId)) return Error("pane_id_required", "An exact pane GUID is required.");
        if (paneId.Length > 128) return Error("invalid_pane_id", "The pane GUID is invalid.");
        if (maxLines < 1 || maxLines > MaximumLines) return Error("invalid_max_lines", $"maxLines must be between 1 and {MaximumLines}.");
        if (maxCharacters < 1 || maxCharacters > MaximumCharacters)
            return Error("invalid_max_characters", $"maxCharacters must be between 1 and {MaximumCharacters}.");

        var serverRestarted = false;
        OutputSnapshotCursor? previous = null;
        if (cursor is not null && !OutputSnapshotCursorCodec.TryRead(cursor, paneId, out serverRestarted, out previous))
            return Error("invalid_cursor", "The cursor is malformed or belongs to another pane.");

        string? fallbackReason = null;
        if (Guid.TryParse(paneId, out var paneGuid))
        {
            // server が再起動した cursor の provider 位置は使わない。provider は新しい baseline を返す。
            var providerCursor = serverRestarted ? null : previous?.ProviderCursor;
            var outcome = await ProviderReadGateway.ReadAsync("output.read", target => new OutputReadRequestBody
            {
                Target = target,
                PaneGuid = paneGuid.ToString("D"),
                ProviderCursor = providerCursor,
                MaxLines = maxLines,
                MaxCharacters = maxCharacters
            }).ConfigureAwait(false);
            if (outcome.IsOk)
            {
                if (!OutputProviderBodyRules.TryParse(outcome.Body, out var body) || body.Text.Length > maxCharacters ||
                    !string.Equals(body.PaneGuid, paneGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                    (providerCursor is null && body.Mode == "delta"))
                    return ProviderToolResults.InvalidProviderResponse(outcome);
                return ProviderResult(paneId, body, outcome.ProviderEpoch, cursor is not null, serverRestarted, previous);
            }
            fallbackReason = outcome.ProviderReason ?? outcome.Code ?? "provider_unavailable";
        }
        else
        {
            fallbackReason = "pane_id_not_guid";
        }

        var reset = cursor is not null;
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
                // DTE は版付きの変更列を公開しないため、cursor 付きの再読は常に連続性不明として扱う。
                Gap = reset,
                Reset = reset,
                ServerRestarted = serverRestarted,
                Truncated = snapshot.Truncated,
                OmittedLines = snapshot.OmittedLines,
                ReturnedLines = snapshot.ReturnedLines,
                Text = snapshot.Text,
                Cursor = OutputSnapshotCursorCodec.Create(snapshot.PaneGuid, captureId),
                Provenance = "dte",
                ProviderFallbackReason = fallbackReason
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

    /// <summary>provider の delta / snapshot を結果にする。delta の text は切り詰めない（cursor の連続性を壊すため）。</summary>
    private static string ProviderResult(string paneId, OutputReadResponseBody body, string? providerEpoch, bool cursorSupplied,
        bool serverRestarted, OutputSnapshotCursor? previous)
    {
        var captureId = Guid.NewGuid().ToString("N");
        // 以前の cursor が DTE snapshot（provider 位置なし）または別 server のものなら、この応答は連続ではない。
        var unprovenCursor = cursorSupplied && (serverRestarted || previous?.ProviderCursor is null);
        var result = new OutputSinceResult
        {
            PaneGuid = paneId,
            CaptureId = captureId,
            Mode = body.Mode,
            IncrementalSupported = body.IncrementalSupported,
            Gap = body.Gap || unprovenCursor,
            Reset = body.Reset || serverRestarted,
            ServerRestarted = serverRestarted,
            Truncated = body.Truncated,
            OmittedLines = body.OmittedLines,
            OmittedCharacters = body.OmittedCharacters,
            ReturnedLines = body.ReturnedLines,
            Text = body.Text,
            Cursor = OutputSnapshotCursorCodec.Create(paneId, captureId, body.ProviderCursor, providerEpoch),
            Provenance = "outputBufferProvider",
            ProviderEpoch = providerEpoch,
            PaneName = body.PaneName,
            ContinuityReason = body.ContinuityReason,
            BufferVersion = body.BufferVersion
        };
        var json = JsonConvert.SerializeObject(result);
        return json.Length <= MaximumResponseCharacters
            ? json
            : Error("response_too_large", "The provider output exceeded the response limit. Request fewer characters.");
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
        public string Provenance { get; init; } = "dte";
        public string? ProviderFallbackReason { get; init; }
        public string? ProviderEpoch { get; init; }
        public string? PaneName { get; init; }
        public string? ContinuityReason { get; init; }
        public int? OmittedCharacters { get; init; }
        public int? BufferVersion { get; init; }
    }
}
