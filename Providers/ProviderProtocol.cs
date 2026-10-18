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
    Goodbye
}

internal sealed record ProviderFrame
{
    public int ProtocolVersion { get; init; } = ProviderProtocol.CurrentVersion;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderFrameType Type { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public int VisualStudioPid { get; init; }
    public long VisualStudioStartTicks { get; init; }
    public int ServerPid { get; init; }
    public long ServerStartTicks { get; init; }
    public string ServerInstanceId { get; init; } = string.Empty;
    public string ProviderEpoch { get; init; } = string.Empty;
    public long DeadlineUnixMilliseconds { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public string Proof { get; init; } = string.Empty;
}

internal static class ProviderProtocol
{
    internal const int CurrentVersion = 1;
    internal const int MaxRequestBytes = 64 * 1024;
    internal const int MaxResponseBytes = 256 * 1024;
    internal const int MaxQueuedRequests = 8;
    internal const int MaxHandshakeBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProviderFrame> ReadAsync(Stream stream, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("The provider frame length is invalid.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        ProviderFrame? frame;
        try { frame = JsonSerializer.Deserialize<ProviderFrame>(payload, JsonOptions); }
        catch (JsonException) { throw new InvalidDataException("The provider frame is malformed."); }
        if (frame is null) throw new InvalidDataException("The provider frame is empty.");
        Validate(frame);
        return frame;
    }

    internal static string CreateProof(ReadOnlySpan<byte> secret, string purpose, ProviderFrame frame)
    {
        var canonical = string.Join("\n", purpose, frame.ProtocolVersion, frame.Type, frame.RequestId,
            frame.VisualStudioPid, frame.VisualStudioStartTicks, frame.ServerPid, frame.ServerStartTicks,
            frame.ServerInstanceId, frame.ProviderEpoch, frame.DeadlineUnixMilliseconds, frame.Nonce);
        using var hmac = new HMACSHA256(secret.ToArray());
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool VerifyProof(ReadOnlySpan<byte> secret, string purpose, ProviderFrame frame)
    {
        if (string.IsNullOrEmpty(frame.Proof)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(frame.Proof); }
        catch (FormatException) { return false; }
        byte[] expected;
        try { expected = Convert.FromBase64String(CreateProof(secret, purpose, frame)); }
        catch (FormatException) { return false; }
        if (supplied.Length != expected.Length) return false;
        var difference = 0;
        for (var index = 0; index < supplied.Length; index++) difference |= supplied[index] ^ expected[index];
        return difference == 0;
    }

    internal static bool IsUuidN(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') && value[12] == '4' &&
        value[16] is '8' or '9' or 'a' or 'b' && Guid.TryParseExact(value, "N", out _);

    private static void Validate(ProviderFrame frame)
    {
        if (frame.ProtocolVersion != CurrentVersion || !Enum.IsDefined(frame.Type) ||
            !IsUuidN(frame.RequestId) || !IsUuidN(frame.ProviderEpoch) || frame.VisualStudioPid <= 0 ||
            frame.VisualStudioStartTicks <= 0 || frame.ServerPid <= 0 || frame.ServerStartTicks <= 0 ||
            frame.ServerInstanceId.Length != 22 || frame.ServerInstanceId.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) ||
            frame.DeadlineUnixMilliseconds < 0 ||
            frame.Nonce.Length > 64 || frame.Proof.Length > 128)
            throw new InvalidDataException("The provider frame identity or type is invalid.");
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
