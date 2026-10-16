using System.Text.Json;

namespace VisualStudioMcpServer.ConnectionKeys;

/// <summary>
/// Validated data carried by a VSDMCP-KEY1 connection key.
/// </summary>
public sealed record ConnectionKeyPayload(
    int Version,
    string KeyId,
    int VisualStudioPid,
    long VisualStudioStartTicks,
    int ServerPid,
    string ServerInstanceId,
    string Url,
    string Token,
    string Otp,
    long ExpiresAt)
{
    /// <summary>Builds the exact bearer value used for this grant.</summary>
    public string ToBearerValue() => $"vsc1.{KeyId}.{Token}.{Otp}";

    /// <inheritdoc />
    public override string ToString() => "ConnectionKeyPayload(credential-redacted)";
}

/// <summary>
/// Strict parser and encoder for the VSDMCP-KEY1 wire format.
/// </summary>
public static class ConnectionKeyCodec
{
    private const string Prefix = "VSDMCP-KEY1:";
    private const int MaxOuterChars = 8192;
    private const int MaxJsonBytes = 4096;
    private const string InvalidMessage = "Connection key format is invalid.";
    private static readonly string[] FieldNames = ["v", "keyId", "visualStudioPid", "visualStudioStartTicks", "serverPid", "serverInstanceId", "url", "token", "otp", "expiresAt"];

    /// <summary>Encodes a validated payload into one key string.</summary>
    public static string Encode(ConnectionKeyPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Validate(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", payload.Version);
            writer.WriteString("keyId", payload.KeyId);
            writer.WriteNumber("visualStudioPid", payload.VisualStudioPid);
            writer.WriteString("visualStudioStartTicks", payload.VisualStudioStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteNumber("serverPid", payload.ServerPid);
            writer.WriteString("serverInstanceId", payload.ServerInstanceId);
            writer.WriteString("url", payload.Url);
            writer.WriteString("token", payload.Token);
            writer.WriteString("otp", payload.Otp);
            writer.WriteNumber("expiresAt", payload.ExpiresAt);
            writer.WriteEndObject();
        }
        return Prefix + Base64UrlEncode(stream.ToArray());
    }

    /// <summary>Parses and validates a complete key without exposing parser details in errors.</summary>
    public static bool TryDecode(string? value, out ConnectionKeyPayload? payload)
    {
        payload = null;
        if (value is null || value.Length > MaxOuterChars) return false;
        var text = value.Trim();
        if (text.Length > MaxOuterChars || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var json = Base64UrlDecode(text.AsSpan(Prefix.Length));
            if (json.Length is 0 or > MaxJsonBytes) return false;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!FieldNames.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
            }
            if (seen.Count != FieldNames.Length) return false;
            var root = document.RootElement;
            if (!TryInt32(root, "v", out var version) || !TryString(root, "keyId", out var keyId) ||
                !TryInt32(root, "visualStudioPid", out var vsPid) || !TryString(root, "visualStudioStartTicks", out var ticksText) ||
                !long.TryParse(ticksText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
                !TryInt32(root, "serverPid", out var serverPid) || !TryString(root, "serverInstanceId", out var instance) ||
                !TryString(root, "url", out var url) || !TryString(root, "token", out var token) || !TryString(root, "otp", out var otp) ||
                !TryInt64(root, "expiresAt", out var expiry)) return false;
            var parsed = new ConnectionKeyPayload(version, keyId!, vsPid, ticks, serverPid, instance!, url!, token!, otp!, expiry);
            Validate(parsed);
            payload = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Parses a vsc1 bearer value, rejecting non-ASCII and oversized credentials.</summary>
    public static bool TryParseBearer(string? value, out string keyId, out string token, out string otp)
    {
        keyId = token = otp = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 256 || value.Any(c => c > 0x7f)) return false;
        var parts = value.Split('.');
        if (parts.Length != 4 || parts[0] != "vsc1" || !IsBase64Url(parts[1], 22) || !IsBase64Url(parts[2], 43) || !IsBase64Url(parts[3], 43)) return false;
        keyId = parts[1]; token = parts[2]; otp = parts[3];
        return true;
    }

    /// <summary>Encodes random bytes as canonical unpadded base64url.</summary>
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || text.Contains('=') || text.Contains('+') || text.Contains('/') || text.ToString().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException(InvalidMessage);
        var standard = text.ToString().Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - standard.Length % 4) % 4);
        var bytes = Convert.FromBase64String(standard);
        if (!Base64UrlEncode(bytes).AsSpan().SequenceEqual(text)) throw new FormatException(InvalidMessage);
        return bytes;
    }

    private static bool IsBase64Url(string value, int chars)
    {
        if (value.Length != chars) return false;
        try { return Base64UrlEncode(Base64UrlDecode(value)) == value && Base64UrlDecode(value).Length == (chars == 22 ? 16 : 32); }
        catch { return false; }
    }

    private static bool TryInt32(JsonElement root, string name, out int value) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value) || SetDefault(out value);
    private static bool TryInt64(JsonElement root, string name, out long value) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value) || SetDefault(out value);
    private static bool TryString(JsonElement root, string name, out string? value) { value = root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null; return value is not null; }
    private static bool SetDefault<T>(out T value) { value = default!; return false; }

    private static void Validate(ConnectionKeyPayload p)
    {
        if (p.Version != 1 || !IsBase64Url(p.KeyId, 22) || !IsBase64Url(p.ServerInstanceId, 22) ||
            !IsBase64Url(p.Token, 43) || !IsBase64Url(p.Otp, 43) || p.VisualStudioPid <= 0 || p.ServerPid <= 0 ||
            p.VisualStudioStartTicks <= 0 || p.ExpiresAt <= 0 || !IsExactLoopbackUrl(p.Url)) throw new ArgumentException(InvalidMessage);
    }

    private static bool IsExactLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/mcp" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        return value == $"http://127.0.0.1:{uri.Port}/mcp";
    }
}


