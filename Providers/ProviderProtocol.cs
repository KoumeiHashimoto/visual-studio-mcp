using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualStudioMcpServer.Providers;

internal enum ProviderFrameType
{
    Challenge,
    Hello,
    Ready,
    Ping,
    Pong,
    Goodbye,
    Request,
    Response,
    Event,
    Cancel
}

internal sealed record ProviderFrame
{
    public int ProtocolVersion { get; init; } = ProviderProtocol.CurrentVersion;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderFrameType Type { get; init; }
    public ulong Sequence { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string DispatchState { get; init; } = string.Empty;
    public int VisualStudioPid { get; init; }
    public long VisualStudioStartTicks { get; init; }
    public int ServerPid { get; init; }
    public long ServerStartTicks { get; init; }
    public string ServerInstanceId { get; init; } = string.Empty;
    public string ProviderEpoch { get; init; } = string.Empty;
    public long DeadlineUnixMilliseconds { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public string Proof { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string CatalogHash { get; init; } = string.Empty;
    public string[] ImplementedOperations { get; init; } = [];
}

/// <summary>プロトコル版または catalogHash の不一致。companion は provider_protocol_mismatch として扱う。</summary>
internal sealed class ProviderProtocolMismatchException(string message) : ProviderProtocolException(message);

/// <summary>受信 frame の契約違反。受信側は以後何も書かずに切断する。</summary>
internal sealed class ProviderProtocolViolationException(string message) : ProviderProtocolException(message);

/// <summary>provider 契約上の失敗の基底。InvalidDataException と同じく fail closed で扱う。</summary>
internal abstract class ProviderProtocolException(string message) : Exception(message);

internal enum ProviderOperationKind
{
    Read,
    Execution,
    Event
}

internal sealed record ProviderCatalogEntry(string Operation, ProviderOperationKind Kind, string Direction,
    int MaxRequestBytes, int MaxResponseBytes, int MaxDeadlineMilliseconds)
{
    internal string CanonicalLine => string.Join("|", Operation, Kind switch
    {
        ProviderOperationKind.Read => "read",
        ProviderOperationKind.Execution => "execution",
        _ => "event"
    }, Direction, MaxRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        MaxResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>P13-06 で凍結した操作・イベント catalog。変更には版の更新と両 repository の同期が必要。</summary>
internal static class ProviderCatalog
{
    internal const string CompanionToVisualStudio = "c2v";
    internal const string VisualStudioToCompanion = "v2c";
    internal const string CanonicalHeader = "vsdmcp-provider-catalog-v2";
    internal const int MaxEventBytes = 16 * 1024;

    internal static readonly IReadOnlyList<ProviderCatalogEntry> Entries =
    [
        Request("capabilities.probe", ProviderOperationKind.Read, 10_000),
        Request("debugger.getState", ProviderOperationKind.Read, 10_000),
        Request("execution.readTarget", ProviderOperationKind.Read, 10_000),
        Request("execution.dispatch", ProviderOperationKind.Execution, 30_000),
        Request("ownership.reconcile", ProviderOperationKind.Read, 10_000),
        Request("documents.list", ProviderOperationKind.Read, 10_000),
        Request("editor.getContext", ProviderOperationKind.Read, 10_000),
        Request("documents.read", ProviderOperationKind.Read, 10_000),
        Request("diagnostics.capture", ProviderOperationKind.Read, 10_000),
        Request("output.read", ProviderOperationKind.Read, 10_000),
        Event("debugger.modeChanged"),
        Event("execution.observed"),
        Event("provider.featureAvailability"),
        Event("provider.eventsDropped")
    ];

    private static readonly Dictionary<string, ProviderCatalogEntry> ByName =
        Entries.ToDictionary(static entry => entry.Operation, StringComparer.Ordinal);

    internal static string CanonicalText { get; } = CanonicalHeader + "\n" + string.Join("\n",
        Entries.Select(static entry => entry.CanonicalLine).OrderBy(static line => line, StringComparer.Ordinal));

    internal static string Hash { get; } = ProviderProtocol.Sha256Hex(Encoding.UTF8.GetBytes(CanonicalText));

    internal static bool TryGet(string? operation, out ProviderCatalogEntry entry)
    {
        if (operation is not null && ByName.TryGetValue(operation, out var found))
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    internal static bool IsRequest(string? operation) =>
        TryGet(operation, out var entry) && entry.Kind != ProviderOperationKind.Event;

    internal static bool IsEvent(string? operation) =>
        TryGet(operation, out var entry) && entry.Kind == ProviderOperationKind.Event;

    private static ProviderCatalogEntry Request(string operation, ProviderOperationKind kind, int maxDeadline) =>
        new(operation, kind, CompanionToVisualStudio, ProviderProtocol.MaxRequestBytes, ProviderProtocol.MaxResponseBytes, maxDeadline);

    private static ProviderCatalogEntry Event(string operation) =>
        new(operation, ProviderOperationKind.Event, VisualStudioToCompanion, MaxEventBytes, 0, 0);
}

internal static class ProviderOutcome
{
    internal const string Ok = "ok";
    internal const string Error = "error";
}

internal static class ProviderDispatchState
{
    internal const string NotDispatched = "notDispatched";
    internal const string Dispatched = "dispatched";
    internal const string Unknown = "unknown";

    internal static bool IsValid(string value) => value is NotDispatched or Dispatched or Unknown;
}

internal static class ProviderErrorCodes
{
    internal const string InvalidRequest = "invalid_request";
    internal const string UnsupportedOperation = "unsupported_operation";
    internal const string ProviderUnavailable = "provider_unavailable";
    internal const string ProviderBusy = "provider_busy";
    internal const string DeadlineExpired = "deadline_expired";
    internal const string Cancelled = "cancelled";
    internal const string TargetMismatch = "target_mismatch";
    internal const string OwnerFenceFailed = "owner_fence_failed";
    internal const string UiThreadTimeout = "ui_thread_timeout";
    internal const string ResultUnknown = "result_unknown";
    internal const string InternalError = "internal_error";

    // companion 側でだけ確定する結果。VSIX の応答には現れない。
    internal const string ProviderTimeout = "provider_timeout";
    internal const string ProviderDisconnected = "provider_disconnected";
    internal const string ProviderProtocolMismatch = "provider_protocol_mismatch";
}

internal static class ProviderProtocol
{
    internal const int CurrentVersion = 2;
    internal const int MaxRequestBytes = 64 * 1024;
    internal const int MaxResponseBytes = 256 * 1024;
    internal const int MaxQueuedRequests = 8;
    internal const int MaxHandshakeBytes = 4096;
    internal const int MaxImplementedOperations = 32;
    internal const int MaxErrorMessageCharacters = 512;
    internal const string SessionKeyLabel = "vsdmcp-provider-session-v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 6,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    internal static byte[] Serialize(ProviderFrame frame, int maximumBytes)
    {
        Validate(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (bytes.Length == 0 || bytes.Length > maximumBytes)
            throw new InvalidDataException("The provider frame exceeds its wire size limit.");
        return bytes;
    }

    internal static async Task WriteAsync(Stream stream, ProviderFrame frame, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var payload = Serialize(frame, maximumBytes);
        await WritePayloadAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WritePayloadAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProviderFrame> ReadAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken) =>
        (await ReadWithLengthAsync(stream, maximumBytes, cancellationToken).ConfigureAwait(false)).Frame;

    internal static async Task<(ProviderFrame Frame, int Length)> ReadWithLengthAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        // 宣言長を検査してから payload を確保する。
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("The provider frame length is invalid.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        ProviderFrame? frame;
        try { frame = JsonSerializer.Deserialize<ProviderFrame>(payload, JsonOptions); }
        catch (JsonException) { throw new InvalidDataException("The provider frame is malformed."); }
        if (frame is null) throw new InvalidDataException("The provider frame is empty.");
        Validate(frame);
        return (frame, length);
    }

    /// <summary>
    /// handshake 用 proof。既存の client/server purpose に catalogHash と本文の SHA-256 を加える。
    /// 本文の hash により Hello の ownerFence を proof で保護する（Challenge/Ready は空本文の hash）。
    /// </summary>
    internal static string CreateProof(ReadOnlySpan<byte> secret, string purpose, ProviderFrame frame)
    {
        var canonical = string.Join("\n", purpose, frame.ProtocolVersion, frame.Type, frame.RequestId,
            frame.VisualStudioPid, frame.VisualStudioStartTicks, frame.ServerPid, frame.ServerStartTicks,
            frame.ServerInstanceId, frame.ProviderEpoch, frame.DeadlineUnixMilliseconds, frame.Nonce, frame.CatalogHash,
            Sha256Hex(Encoding.UTF8.GetBytes(frame.Body)));
        return Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool VerifyProof(ReadOnlySpan<byte> secret, string purpose, ProviderFrame frame) =>
        FixedTimeEquals(frame.Proof, CreateProof(secret, purpose, frame));

    /// <summary>Ready 後の frame 用 session key。bootstrap secret は導出後に呼び出し側がゼロ化する。</summary>
    internal static byte[] DeriveSessionKey(ReadOnlySpan<byte> secret, string challengeNonce, string providerEpoch,
        string catalogHash)
    {
        var label = SessionKeyLabel + "\n" + challengeNonce + "\n" + providerEpoch + "\n" + catalogHash;
        return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(label));
    }

    /// <summary>
    /// 本文に束縛した session proof。identity 6 項目は VS PID/開始時刻、server PID/開始時刻、server instance、nonce の順。
    /// </summary>
    internal static string CreateSessionProof(ReadOnlySpan<byte> sessionKey, string direction, ProviderFrame frame)
    {
        var canonical = string.Join("\n", direction, frame.ProtocolVersion, frame.Type, frame.Sequence,
            frame.RequestId, frame.CorrelationId, frame.Operation, frame.Outcome, frame.DispatchState,
            frame.VisualStudioPid, frame.VisualStudioStartTicks, frame.ServerPid, frame.ServerStartTicks,
            frame.ServerInstanceId, frame.Nonce, frame.ProviderEpoch, frame.DeadlineUnixMilliseconds,
            Sha256Hex(Encoding.UTF8.GetBytes(frame.Body)));
        return Convert.ToBase64String(HMACSHA256.HashData(sessionKey, Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool VerifySessionProof(ReadOnlySpan<byte> sessionKey, string direction, ProviderFrame frame) =>
        FixedTimeEquals(frame.Proof, CreateSessionProof(sessionKey, direction, frame));

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string DirectionOf(ProviderFrameType type, bool fromCompanion) =>
        fromCompanion ? ProviderCatalog.CompanionToVisualStudio : ProviderCatalog.VisualStudioToCompanion;

    /// <summary>frame type ごとの許可方向。Goodbye は両方向で許可する。</summary>
    internal static bool IsAllowedDirection(ProviderFrameType type, bool fromCompanion) => type switch
    {
        ProviderFrameType.Hello or ProviderFrameType.Pong or ProviderFrameType.Request or ProviderFrameType.Cancel => fromCompanion,
        ProviderFrameType.Challenge or ProviderFrameType.Ready or ProviderFrameType.Ping or
            ProviderFrameType.Response or ProviderFrameType.Event => !fromCompanion,
        ProviderFrameType.Goodbye => true,
        _ => false
    };

    internal static bool IsHandshake(ProviderFrameType type) =>
        type is ProviderFrameType.Challenge or ProviderFrameType.Hello or ProviderFrameType.Ready;

    internal static bool IsUuidN(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') && value[12] == '4' &&
        value[16] is '8' or '9' or 'a' or 'b' && Guid.TryParseExact(value, "N", out _);

    internal static bool IsServerInstanceId(string? value) =>
        value is { Length: 22 } && value.All(static character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    internal static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>本文を JSON object として解析する。depth/未知 member/非 object は契約違反として扱う。</summary>
    internal static T ParseBody<T>(string body) where T : class
    {
        if (string.IsNullOrEmpty(body) || body[0] != '{')
            throw new ProviderProtocolViolationException("The provider body is not a JSON object.");
        try
        {
            return JsonSerializer.Deserialize<T>(body, BodyJsonOptions)
                ?? throw new ProviderProtocolViolationException("The provider body is empty.");
        }
        catch (JsonException) { throw new ProviderProtocolViolationException("The provider body is malformed."); }
    }

    /// <summary>型を持たない応答本文の構造だけを検査する（各 operation の DTO は利用 slice で追加する）。</summary>
    internal static void ValidateObjectBody(string body)
    {
        if (string.IsNullOrEmpty(body) || body[0] != '{')
            throw new ProviderProtocolViolationException("The provider body is not a JSON object.");
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 6 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProviderProtocolViolationException("The provider body is not a JSON object.");
        }
        catch (JsonException) { throw new ProviderProtocolViolationException("The provider body is malformed."); }
    }

    internal static string SerializeBody<T>(T body) => JsonSerializer.Serialize(body, BodyJsonOptions);

    private static bool FixedTimeEquals(string suppliedProof, string expectedProof)
    {
        if (string.IsNullOrEmpty(suppliedProof)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(suppliedProof); }
        catch (FormatException) { return false; }
        var expected = Convert.FromBase64String(expectedProof);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static void Validate(ProviderFrame frame)
    {
        // JSON の null は既定値を上書きするため、長さの検査より先に拒否する。
        if (frame.RequestId is null || frame.CorrelationId is null || frame.Operation is null || frame.Outcome is null ||
            frame.DispatchState is null || frame.ServerInstanceId is null || frame.ProviderEpoch is null ||
            frame.Nonce is null || frame.Proof is null || frame.Body is null || frame.CatalogHash is null ||
            frame.ImplementedOperations is null || frame.ImplementedOperations.Any(static operation => operation is null))
            throw new InvalidDataException("The provider frame contains a null field.");
        if (!Enum.IsDefined(frame.Type) || !IsUuidN(frame.RequestId) || !IsUuidN(frame.ProviderEpoch) ||
            frame.VisualStudioPid <= 0 || frame.VisualStudioStartTicks <= 0 || frame.ServerPid <= 0 ||
            frame.ServerStartTicks <= 0 || !IsServerInstanceId(frame.ServerInstanceId) ||
            frame.DeadlineUnixMilliseconds < 0 || frame.Nonce.Length > 64 || frame.Proof.Length > 128 ||
            frame.ImplementedOperations is null)
            throw new InvalidDataException("The provider frame identity or type is invalid.");
        if (frame.ProtocolVersion != CurrentVersion)
            throw new ProviderProtocolMismatchException("The provider protocol version does not match.");

        if (IsHandshake(frame.Type))
        {
            if (frame.Sequence != 0 || frame.CorrelationId.Length != 0 || frame.Operation.Length != 0 ||
                frame.Outcome.Length != 0 || frame.DispatchState.Length != 0 || frame.Nonce.Length == 0 ||
                !IsLowerHex64(frame.CatalogHash) ||
                (frame.Type != ProviderFrameType.Hello && frame.Body.Length != 0) ||
                (frame.Type == ProviderFrameType.Hello && frame.Body.Length > 256) ||
                (frame.Type != ProviderFrameType.Ready && frame.ImplementedOperations.Length != 0))
                throw new InvalidDataException("The provider handshake frame is invalid.");
            if (frame.Type == ProviderFrameType.Ready &&
                (frame.ImplementedOperations.Length > MaxImplementedOperations ||
                 frame.ImplementedOperations.Distinct(StringComparer.Ordinal).Count() != frame.ImplementedOperations.Length ||
                 !frame.ImplementedOperations.All(ProviderCatalog.IsRequest)))
                throw new InvalidDataException("The provider implemented operation list is invalid.");
            return;
        }

        if (frame.Sequence == 0 || frame.CatalogHash.Length != 0 || frame.ImplementedOperations.Length != 0 ||
            frame.Nonce.Length != 0 || frame.DeadlineUnixMilliseconds <= 0)
            throw new InvalidDataException("The provider session frame is invalid.");
        var valid = frame.Type switch
        {
            ProviderFrameType.Request => ProviderCatalog.IsRequest(frame.Operation) && frame.CorrelationId.Length == 0 &&
                frame.Outcome.Length == 0 && frame.DispatchState.Length == 0 && frame.Body.Length != 0,
            ProviderFrameType.Response => ProviderCatalog.IsRequest(frame.Operation) && IsUuidN(frame.CorrelationId) &&
                frame.Outcome is ProviderOutcome.Ok or ProviderOutcome.Error &&
                ProviderDispatchState.IsValid(frame.DispatchState) && frame.Body.Length != 0,
            ProviderFrameType.Event => ProviderCatalog.IsEvent(frame.Operation) && frame.CorrelationId.Length == 0 &&
                frame.Outcome.Length == 0 && frame.DispatchState.Length == 0 && frame.Body.Length != 0,
            ProviderFrameType.Cancel => IsUuidN(frame.CorrelationId) && frame.Operation.Length == 0 &&
                frame.Outcome.Length == 0 && frame.DispatchState.Length == 0 && frame.Body.Length == 0,
            _ => frame.CorrelationId.Length == 0 && frame.Operation.Length == 0 && frame.Outcome.Length == 0 &&
                frame.DispatchState.Length == 0 && frame.Body.Length == 0
        };
        if (!valid) throw new InvalidDataException("The provider session frame fields do not match its type.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The provider pipe closed during a frame.");
            offset += read;
        }
    }
}

/// <summary>
/// Ready 後の 1 接続分の session 状態。方向ラベルと方向別 sequence を管理し、受信 frame の proof と連番を検査する。
/// </summary>
internal sealed class ProviderSession : IDisposable
{
    private readonly byte[] _sessionKey;
    private readonly string _sendDirection;
    private readonly string _receiveDirection;
    private readonly bool _isCompanion;
    private ulong _lastSent;
    private ulong _lastReceived;

    internal ProviderSession(byte[] sessionKey, bool isCompanion)
    {
        _sessionKey = (byte[])sessionKey.Clone();
        _isCompanion = isCompanion;
        _sendDirection = isCompanion ? ProviderCatalog.CompanionToVisualStudio : ProviderCatalog.VisualStudioToCompanion;
        _receiveDirection = isCompanion ? ProviderCatalog.VisualStudioToCompanion : ProviderCatalog.CompanionToVisualStudio;
    }

    internal ulong LastSent => _lastSent;
    internal ulong LastReceived => _lastReceived;

    /// <summary>
    /// 次の sequence と proof を付ける。単一 writer の lock 内で呼び、書き込み後に <see cref="CommitSent"/> で確定する。
    /// 上限超過などで書けなかった frame は sequence を消費しない。
    /// </summary>
    internal ProviderFrame Seal(ProviderFrame frame)
    {
        if (!ProviderProtocol.IsAllowedDirection(frame.Type, _isCompanion) || ProviderProtocol.IsHandshake(frame.Type))
            throw new InvalidOperationException("The provider frame cannot be sent in this direction.");
        var sealedFrame = frame with { Sequence = _lastSent + 1, Proof = string.Empty };
        return sealedFrame with { Proof = ProviderProtocol.CreateSessionProof(_sessionKey, _sendDirection, sealedFrame) };
    }

    internal void CommitSent(ProviderFrame sealedFrame)
    {
        if (sealedFrame.Sequence != _lastSent + 1)
            throw new InvalidOperationException("The provider frame was sealed out of order.");
        _lastSent = sealedFrame.Sequence;
    }

    /// <summary>受信 frame の方向・proof・sequence を検査する。違反時は例外を投げ、呼び出し側は切断する。</summary>
    internal void Open(ProviderFrame frame)
    {
        if (!ProviderProtocol.IsAllowedDirection(frame.Type, !_isCompanion) || ProviderProtocol.IsHandshake(frame.Type))
            throw new ProviderProtocolViolationException("The provider frame arrived in the wrong direction.");
        if (!ProviderProtocol.VerifySessionProof(_sessionKey, _receiveDirection, frame))
            throw new ProviderProtocolViolationException("The provider frame proof is invalid.");
        if (frame.Sequence != _lastReceived + 1)
            throw new ProviderProtocolViolationException("The provider frame sequence is not contiguous.");
        _lastReceived = frame.Sequence;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_sessionKey);
}

// ---- 本文 DTO（wire は camelCase）----

internal sealed record ProviderTarget
{
    public int VisualStudioPid { get; init; }
    public long VisualStudioStartTicks { get; init; }
    public string? WorkspacePath { get; init; }
}

internal sealed record ProviderErrorBody
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

internal sealed record ProviderOwnerHelloBody
{
    public string? OwnerFence { get; init; }
}

internal sealed record TargetOnlyRequestBody
{
    public ProviderTarget? Target { get; init; }
}

internal sealed record DebuggerGetStateRequestBody
{
    public ProviderTarget? Target { get; init; }
    public bool IncludeStartupSnapshot { get; init; }
}

internal sealed record ExecutionDispatchRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? ExpectedStartupSnapshotId { get; init; }
    public string? ExpectedDebugSessionId { get; init; }
    public string? ExpectedProcessSetSnapshotId { get; init; }
    public string[] PolicyFlags { get; init; } = [];
    public string OwnerFence { get; init; } = string.Empty;
}

internal sealed record OwnershipReconcileRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string? PreviousServerInstanceId { get; init; }
}

internal sealed record DocumentsListRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string Scope { get; init; } = string.Empty;
    public int PageSize { get; init; }
    public string? Cursor { get; init; }
}

internal sealed record EditorGetContextRequestBody
{
    public ProviderTarget? Target { get; init; }
    public bool IncludeSelectedText { get; init; }
    public int MaxCharacters { get; init; }
}

internal sealed record DocumentsReadRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string DocumentId { get; init; } = string.Empty;
    public string? ExpectedBufferVersion { get; init; }
    public int StartLine { get; init; }
    public int LineCount { get; init; }
    public int MaxCharacters { get; init; }
}

internal sealed record DiagnosticsCaptureRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string? Severity { get; init; }
    public string? File { get; init; }
    public string? Source { get; init; }
    public string? ProjectId { get; init; }
}

internal sealed record OutputReadRequestBody
{
    public ProviderTarget? Target { get; init; }
    public string PaneGuid { get; init; } = string.Empty;
    public string? ProviderCursor { get; init; }
    public int MaxLines { get; init; }
    public int MaxCharacters { get; init; }
}

/// <summary>capabilities.probe の機能 1 件。</summary>
internal sealed record CapabilitiesProbeFeature
{
    public string FeatureKey { get; init; } = string.Empty;
    public string Availability { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string ObservedAtUtc { get; init; } = string.Empty;
}

/// <summary>capabilities.probe の応答本文。版は取得できない場合 null。</summary>
internal sealed record CapabilitiesProbeResponseBody
{
    public string? VisualStudioVersion { get; init; }
    public string? VsixVersion { get; init; }
    public string? ProviderVersion { get; init; }
    public CapabilitiesProbeFeature[] Features { get; init; } = [];
}

internal sealed record OwnershipReconcileOperation
{
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string DispatchState { get; init; } = string.Empty;
}

internal sealed record OwnershipReconcileResponseBody
{
    public OwnershipReconcileOperation[] UnsettledOperations { get; init; } = [];
    public bool BuildBusy { get; init; }
    public string DebuggerState { get; init; } = string.Empty;
    public bool PreviousOwnerExited { get; init; }
    public bool QuarantineReleasable { get; init; }
}

internal sealed record DebuggerModeChangedEventBody
{
    public ulong ProviderSequence { get; init; }
    public string State { get; init; } = string.Empty;
    public string? DebugSessionId { get; init; }
    public string? ProcessSetSnapshotId { get; init; }
    public string? LaunchOperationId { get; init; }
    public string ObservedAtUtc { get; init; } = string.Empty;
}

internal sealed record ExecutionObservedEventBody
{
    public string OperationId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string? DebugSessionId { get; init; }
    public string? ProcessSetSnapshotId { get; init; }
    /// <summary>停止 operation だけが持つ、検証済み process 集合の process ごとの結果。それ以外は null。</summary>
    public ProcessOutcomeBody[]? ProcessOutcomes { get; init; }
}

/// <summary>
/// 停止した session の process ごとの結果（execution.observed の processOutcomes）。PID と開始時刻で同一 instance を表す。
/// outcome は exited（終了を観測、exitCode は取得できた場合だけ）、detached（debugger 終了後の有限待機の後も同一 instance が生存）、
/// unknown（同一 instance を検証できない、または観測できない）のいずれか。
/// </summary>
internal sealed record ProcessOutcomeBody
{
    public int ProcessId { get; init; }
    public long? StartTicks { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
}

internal sealed record ProviderFeatureAvailabilityEntry
{
    public string FeatureKey { get; init; } = string.Empty;
    public string Availability { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string ObservedAtUtc { get; init; } = string.Empty;
}

internal sealed record ProviderFeatureAvailabilityEventBody
{
    public ProviderFeatureAvailabilityEntry[] Features { get; init; } = [];
}

internal sealed record ProviderEventsDroppedEventBody
{
    public ulong DroppedCount { get; init; }
    public ulong FirstProviderSequence { get; init; }
    public ulong LastProviderSequence { get; init; }
}

/// <summary>catalog の per-field 上限。送信前（companion）と受信時（VSIX）の両方で同じ規則を使う。</summary>
internal static class ProviderBodyRules
{
    internal static bool IsToken(string? value, int maxLength) =>
        value is not null && value.Length > 0 && value.Length <= maxLength && value.All(static character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':');

    internal static bool IsOptionalToken(string? value, int maxLength) => value is null || IsToken(value, maxLength);

    internal static bool IsTimestamp(string? value) =>
        value is { Length: > 0 and <= 40 } && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    internal static bool IsValidTarget(ProviderTarget? target) =>
        target is not null && target.VisualStudioPid > 0 && target.VisualStudioStartTicks > 0 &&
        (target.WorkspacePath is null || target.WorkspacePath.Length <= 1024);

    /// <summary>
    /// 要求本文を operation の型で解析し、値の範囲を検査する。
    /// 構造違反は <see cref="ProviderProtocolViolationException"/>、値の範囲外は false（invalid_request）とする。
    /// </summary>
    internal static bool TryValidateRequest(string operation, string body, out ProviderTarget? target)
    {
        target = null;
        switch (operation)
        {
            case "capabilities.probe":
            case "execution.readTarget":
                target = ProviderProtocol.ParseBody<TargetOnlyRequestBody>(body).Target;
                return IsValidTarget(target);
            case "debugger.getState":
                target = ProviderProtocol.ParseBody<DebuggerGetStateRequestBody>(body).Target;
                return IsValidTarget(target);
            case "execution.dispatch":
            {
                var value = ProviderProtocol.ParseBody<ExecutionDispatchRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && ProviderProtocol.IsUuidN(value.OperationId) &&
                    value.Action is "startDebugging" or "startWithoutDebugging" or "stopDebugging" &&
                    IsOptionalToken(value.ExpectedStartupSnapshotId, 64) && IsOptionalToken(value.ExpectedDebugSessionId, 64) &&
                    IsOptionalToken(value.ExpectedProcessSetSnapshotId, 64) && value.PolicyFlags is { Length: <= 16 } &&
                    value.PolicyFlags.All(static flag => IsToken(flag, 64)) && ProviderProtocol.IsUuidN(value.OwnerFence);
            }
            case "ownership.reconcile":
            {
                var value = ProviderProtocol.ParseBody<OwnershipReconcileRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) &&
                    (value.PreviousServerInstanceId is null || ProviderProtocol.IsServerInstanceId(value.PreviousServerInstanceId));
            }
            case "documents.list":
            {
                var value = ProviderProtocol.ParseBody<DocumentsListRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && IsToken(value.Scope, 32) && value.PageSize is >= 1 and <= 100 &&
                    (value.Cursor is null || value.Cursor.Length is > 0 and <= 256);
            }
            case "editor.getContext":
            {
                var value = ProviderProtocol.ParseBody<EditorGetContextRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && value.MaxCharacters is >= 0 and <= 4096;
            }
            case "documents.read":
            {
                var value = ProviderProtocol.ParseBody<DocumentsReadRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && IsToken(value.DocumentId, 128) &&
                    IsOptionalToken(value.ExpectedBufferVersion, 64) && value.StartLine >= 1 &&
                    value.LineCount is >= 1 and <= 500 && value.MaxCharacters is >= 1 and <= 32768;
            }
            case "diagnostics.capture":
            {
                var value = ProviderProtocol.ParseBody<DiagnosticsCaptureRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && (value.Severity is null or "error" or "warning" or "message") &&
                    (value.File is null || value.File.Length is > 0 and <= 1024) &&
                    IsOptionalToken(value.Source, 64) && IsOptionalToken(value.ProjectId, 128);
            }
            case "output.read":
            {
                var value = ProviderProtocol.ParseBody<OutputReadRequestBody>(body);
                target = value.Target;
                return IsValidTarget(target) && Guid.TryParseExact(value.PaneGuid, "D", out _) &&
                    (value.ProviderCursor is null || value.ProviderCursor.Length is > 0 and <= 256) &&
                    value.MaxLines is >= 1 and <= 2000 && value.MaxCharacters is >= 1 and <= 32768;
            }
            default:
                throw new ProviderProtocolViolationException("The provider operation is not in the catalog.");
        }
    }

    /// <summary>イベント本文を検査する。構造・値いずれの違反も契約違反として扱う。</summary>
    internal static object ParseEvent(string operation, string body)
    {
        object result = operation switch
        {
            "debugger.modeChanged" => ProviderProtocol.ParseBody<DebuggerModeChangedEventBody>(body),
            "execution.observed" => ProviderProtocol.ParseBody<ExecutionObservedEventBody>(body),
            "provider.featureAvailability" => ProviderProtocol.ParseBody<ProviderFeatureAvailabilityEventBody>(body),
            "provider.eventsDropped" => ProviderProtocol.ParseBody<ProviderEventsDroppedEventBody>(body),
            _ => throw new ProviderProtocolViolationException("The provider event is not in the catalog.")
        };
        var valid = result switch
        {
            DebuggerModeChangedEventBody value => value.ProviderSequence >= 1 &&
                value.State is "running" or "break" or "design" && IsOptionalToken(value.DebugSessionId, 64) &&
                IsOptionalToken(value.ProcessSetSnapshotId, 64) &&
                (value.LaunchOperationId is null || ProviderProtocol.IsUuidN(value.LaunchOperationId)) &&
                IsTimestamp(value.ObservedAtUtc),
            ExecutionObservedEventBody value => ProviderProtocol.IsUuidN(value.OperationId) &&
                value.State is "observed" or "failed" or "uncertain" && IsOptionalToken(value.Code, 64) &&
                IsOptionalToken(value.DebugSessionId, 64) && IsOptionalToken(value.ProcessSetSnapshotId, 64) &&
                IsValidProcessOutcomes(value.ProcessOutcomes),
            ProviderFeatureAvailabilityEventBody value => value.Features is { Length: <= 16 } &&
                value.Features.All(static feature => feature is not null && IsToken(feature.FeatureKey, 64) &&
                    IsToken(feature.Availability, 32) && IsToken(feature.Reason, 128) && IsTimestamp(feature.ObservedAtUtc)),
            ProviderEventsDroppedEventBody value => value.DroppedCount >= 1 &&
                value.FirstProviderSequence <= value.LastProviderSequence,
            _ => false
        };
        if (!valid) throw new ProviderProtocolViolationException("The provider event body is out of range.");
        return result;
    }

    /// <summary>processOutcomes の検査。null（停止以外）か、32 件以下で値域内の要素だけを許す。終了以外に exitCode は付かない。</summary>
    internal static bool IsValidProcessOutcomes(ProcessOutcomeBody[]? outcomes) =>
        outcomes is null || (outcomes.Length <= 32 && outcomes.All(static outcome => outcome is not null &&
            outcome.ProcessId > 0 && (outcome.StartTicks is null || outcome.StartTicks > 0) &&
            outcome.Outcome is "exited" or "detached" or "unknown" && (outcome.ExitCode is null || outcome.Outcome == "exited")));

    internal static ProviderErrorBody ParseError(string body)
    {
        var value = ProviderProtocol.ParseBody<ProviderErrorBody>(body);
        if (!IsErrorCode(value.Code) || value.Message is null || value.Message.Length > ProviderProtocol.MaxErrorMessageCharacters)
            throw new ProviderProtocolViolationException("The provider error body is out of range.");
        return value;
    }

    internal static CapabilitiesProbeResponseBody ParseCapabilitiesProbeResponse(string body)
    {
        var value = ProviderProtocol.ParseBody<CapabilitiesProbeResponseBody>(body);
        static bool OptionalText(string? text) => text is null || text.Length is > 0 and <= 64;
        if (!OptionalText(value.VisualStudioVersion) || !OptionalText(value.VsixVersion) || !OptionalText(value.ProviderVersion) ||
            value.Features is not { Length: <= 64 } ||
            !value.Features.All(static feature => feature is not null && IsToken(feature.FeatureKey, 64) &&
                feature.Availability is "supported" or "unsupported" or "unknown" && IsToken(feature.Reason, 256) &&
                DateTimeOffset.TryParse(feature.ObservedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _)))
            throw new ProviderProtocolViolationException("The capabilities probe response is out of range.");
        return value;
    }

    internal static OwnershipReconcileResponseBody ParseReconcileResponse(string body)
    {
        var value = ProviderProtocol.ParseBody<OwnershipReconcileResponseBody>(body);
        if (value.UnsettledOperations is not { Length: <= 8 } ||
            !value.UnsettledOperations.All(static operation => operation is not null &&
                ProviderProtocol.IsUuidN(operation.OperationId) && IsToken(operation.Action, 64) &&
                ProviderDispatchState.IsValid(operation.DispatchState)) ||
            value.DebuggerState is not ("design" or "break" or "running" or "unknown"))
            throw new ProviderProtocolViolationException("The ownership reconcile response is out of range.");
        return value;
    }

    internal static bool IsErrorCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(static character => character is >= 'a' and <= 'z' or '_');
}
