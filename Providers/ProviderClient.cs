using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VisualStudioMcpServer.Capabilities;

namespace VisualStudioMcpServer.Providers;

internal sealed class ProviderBootstrap
{
    public string PipeName { get; init; } = string.Empty;
    public string SecretBase64 { get; init; } = string.Empty;
    public int VisualStudioPid { get; init; }
    public long VisualStudioStartTicks { get; init; }
}

internal static class ProviderClient
{
    private static readonly JsonSerializerOptions BootstrapJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static ProviderBootstrap? ReadBootstrap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > ProviderProtocol.MaxHandshakeBytes)
            throw new InvalidDataException("The provider bootstrap file is missing or exceeds its size limit.");
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            try
            {
                var value = JsonSerializer.Deserialize<ProviderBootstrap>(bytes, BootstrapJson)
                    ?? throw new InvalidDataException("The provider bootstrap file is empty.");
                if (string.IsNullOrWhiteSpace(value.PipeName) || value.PipeName.Length > 128 ||
                    value.PipeName.Contains('\\') || value.PipeName.Contains('/') ||
                    value.VisualStudioPid <= 0 || value.VisualStudioStartTicks <= 0)
                    throw new InvalidDataException("The provider bootstrap identity is invalid.");
                var secret = Convert.FromBase64String(value.SecretBase64);
                if (secret.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(secret);
                    throw new InvalidDataException("The provider bootstrap secret has an invalid size.");
                }
                return value;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (JsonException) { throw new InvalidDataException("The provider bootstrap file is malformed."); }
        finally
        {
            try { File.Delete(fullPath); } catch (IOException) { }
        }
    }

    internal static async Task RunAsync(ProviderBootstrap bootstrap, string serverInstanceId,
        CancellationToken cancellationToken, Action<ProviderCapabilityObservation>? statusChanged = null)
    {
        var secret = Convert.FromBase64String(bootstrap.SecretBase64);
        ProviderFrame? authenticatedChallenge = null;
        void Report(string state, string availability, string reason, ProviderFrame? frame = null)
        {
            var observation = new ProviderCapabilityObservation(state, availability,
                frame is null ? null : "visual-studio-in-process-provider",
                null,
                "provider_software_version_not_supplied",
                frame?.ProtocolVersion, frame?.ProviderEpoch,
                DateTimeOffset.UtcNow, reason);
            statusChanged?.Invoke(observation);
        }
        try
        {
            using var parent = Process.GetProcessById(bootstrap.VisualStudioPid);
            if (parent.StartTime.ToUniversalTime().Ticks != bootstrap.VisualStudioStartTicks)
                throw new InvalidDataException("The provider parent process identity is stale.");

            using var stream = await ConnectAsync(bootstrap.PipeName, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(stream.SafePipeHandle, out var serverPid) || serverPid != bootstrap.VisualStudioPid)
                throw new InvalidDataException("The provider pipe server process does not match the owning Visual Studio instance.");
            using (var server = Process.GetProcessById((int)serverPid))
                if (server.StartTime.ToUniversalTime().Ticks != bootstrap.VisualStudioStartTicks)
                    throw new InvalidDataException("The provider pipe server process start time is stale.");

            var serverStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            var challenge = await ProviderProtocol.ReadAsync(stream, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
            if (challenge.Type != ProviderFrameType.Challenge || challenge.VisualStudioPid != bootstrap.VisualStudioPid ||
                challenge.VisualStudioStartTicks != bootstrap.VisualStudioStartTicks || challenge.ServerPid != Environment.ProcessId ||
                challenge.ServerStartTicks != serverStartTicks || challenge.ServerInstanceId != serverInstanceId ||
                challenge.DeadlineUnixMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new InvalidDataException("The provider challenge identity or deadline is invalid.");

            var hello = Copy(challenge, ProviderFrameType.Hello);
            hello = hello with { Proof = ProviderProtocol.CreateProof(secret, "client", hello) };
            await ProviderProtocol.WriteAsync(stream, hello, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
            var ready = await ProviderProtocol.ReadAsync(stream, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
            if (!Matches(ready, challenge, ProviderFrameType.Ready) || !ProviderProtocol.VerifyProof(secret, "server", ready))
                throw new InvalidDataException("The provider host proof is invalid.");
            authenticatedChallenge = challenge;
            Report("connected", "supported", "authenticated_provider_channel_connected_feature_probes_unavailable", challenge);

            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ProviderProtocol.ReadAsync(stream, ProviderProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);
                if (frame.Type == ProviderFrameType.Goodbye)
                {
                    Report("disconnected", "unknown", "provider_closed_channel", challenge);
                    return;
                }
                if (frame.Type != ProviderFrameType.Ping || frame.VisualStudioPid != bootstrap.VisualStudioPid ||
                    frame.VisualStudioStartTicks != bootstrap.VisualStudioStartTicks || frame.ServerPid != Environment.ProcessId ||
                    frame.ServerStartTicks != serverStartTicks || frame.ServerInstanceId != serverInstanceId ||
                    frame.ProviderEpoch != challenge.ProviderEpoch || frame.DeadlineUnixMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    throw new InvalidDataException("The provider request identity or type is invalid.");
                await ProviderProtocol.WriteAsync(stream, Copy(frame, ProviderFrameType.Pong),
                    ProviderProtocol.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                Report("connected", "supported", "authenticated_provider_channel_connected_feature_probes_unavailable", challenge);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report("stopped", "unknown", "provider_client_stopped", authenticatedChallenge);
            throw;
        }
        catch
        {
            Report("disconnected", "unknown", "provider_channel_failed", authenticatedChallenge);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await stream.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                return stream;
            }
            catch (TimeoutException) { stream.Dispose(); }
            catch (IOException) { stream.Dispose(); }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The provider host did not accept a connection within 12 seconds.");
    }

    private static ProviderFrame Copy(ProviderFrame source, ProviderFrameType type) => source with
    {
        Type = type,
        Proof = string.Empty
    };

    private static bool Matches(ProviderFrame actual, ProviderFrame expected, ProviderFrameType type) =>
        actual.Type == type && actual.ProtocolVersion == ProviderProtocol.CurrentVersion &&
        actual.RequestId == expected.RequestId && actual.VisualStudioPid == expected.VisualStudioPid &&
        actual.VisualStudioStartTicks == expected.VisualStudioStartTicks && actual.ServerPid == expected.ServerPid &&
        actual.ServerStartTicks == expected.ServerStartTicks && actual.ServerInstanceId == expected.ServerInstanceId &&
        actual.ProviderEpoch == expected.ProviderEpoch && actual.Nonce == expected.Nonce &&
        actual.DeadlineUnixMilliseconds == expected.DeadlineUnixMilliseconds &&
        actual.DeadlineUnixMilliseconds >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
