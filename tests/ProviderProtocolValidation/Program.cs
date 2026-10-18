using System.Buffers.Binary;
using System.Text;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Coordination;

if (args.Length == 3 && args[0] == "--owner-probe")
{
    using var probeOwner = TargetMutationOwner.TryAcquire(int.Parse(args[1]), long.Parse(args[2]));
    Console.WriteLine(probeOwner is null ? "denied" : "acquired");
    await Console.Out.FlushAsync();
    if (probeOwner is null) Environment.Exit(2);
    Thread.Sleep(Timeout.Infinite);
}

var passed = 0;
var secret = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
var challenge = Frame(ProviderFrameType.Challenge);
var clientProof = ProviderProtocol.CreateProof(secret, "client", challenge);
Check(ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = clientProof }), "client proof accepted");
Check(!ProviderProtocol.VerifyProof(secret, "server", challenge with { Proof = clientProof }), "proof purpose is bound");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { ProviderEpoch = Guid.NewGuid().ToString("N"), Proof = clientProof }), "proof is bound to provider epoch");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { ServerInstanceId = "BBBBBBBBBBBBBBBBBBBBBB", Proof = clientProof }), "proof is bound to server instance");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Type = ProviderFrameType.Ping, Proof = clientProof }), "proof is bound to message type");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = Convert.ToBase64String(new byte[32]) }), "incorrect secret proof rejected");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = "not-base64" }), "malformed proof rejected");
Check(ProviderProtocol.IsUuidN(challenge.ProviderEpoch), "provider epoch uses lowercase UUID-N form");
Check(!ProviderProtocol.IsUuidN("ABCDEFABCDEF4ABC8DEFABCDEFABCDEF"), "uppercase UUID rejected");
Check(!ProviderProtocol.IsUuidN("11111111111151118111111111111111"), "non-v4 UUID rejected");
Check(ProviderProtocol.MaxQueuedRequests == 8, "queue ceiling matches frozen contract");

var bytes = ProviderProtocol.Serialize(Frame(ProviderFrameType.Ping), ProviderProtocol.MaxRequestBytes);
var framed = new byte[bytes.Length + 4];
BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), bytes.Length);
bytes.CopyTo(framed, 4);
var decoded = await ProviderProtocol.ReadAsync(new MemoryStream(framed), ProviderProtocol.MaxRequestBytes, CancellationToken.None);
Check(decoded.Type == ProviderFrameType.Ping && decoded.RequestId == challenge.RequestId, "length-prefixed request round trips");

var response = Frame(ProviderFrameType.Pong);
using var output = new MemoryStream();
await ProviderProtocol.WriteAsync(output, response, ProviderProtocol.MaxResponseBytes, CancellationToken.None);
output.Position = 0;
var decodedResponse = await ProviderProtocol.ReadAsync(output, ProviderProtocol.MaxResponseBytes, CancellationToken.None);
Check(decodedResponse.Type == ProviderFrameType.Pong, "bounded response round trips");

await Throws<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(ProviderProtocol.MaxRequestBytes + 1)),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "oversized request rejected from prefix");
await Throws<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(ProviderProtocol.MaxResponseBytes + 1)),
    ProviderProtocol.MaxResponseBytes, CancellationToken.None), "oversized response rejected from prefix");
await Throws<EndOfStreamException>(ProviderProtocol.ReadAsync(new MemoryStream(new byte[] { 1, 2, 3 }),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "partial prefix rejected");
await Throws<EndOfStreamException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(10).Concat(new byte[] { 1, 2 }).ToArray()),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "partial payload rejected");
await Throws<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(2).Concat(new byte[] { (byte)'{', (byte)'}' }).ToArray()),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "malformed envelope rejected");
await Throws<InvalidDataException>(Task.Run(() => ProviderProtocol.Serialize(Frame(ProviderFrameType.Ping) with { ProtocolVersion = 2 }, ProviderProtocol.MaxRequestBytes)),
    "unsupported protocol version rejected");
await Throws<InvalidDataException>(Task.Run(() => ProviderProtocol.Serialize(Frame(ProviderFrameType.Ping) with { RequestId = "A".PadRight(32, '0') }, ProviderProtocol.MaxRequestBytes)),
    "noncanonical request ID rejected");
await Throws<InvalidDataException>(Task.Run(() => ProviderProtocol.Serialize(Frame(ProviderFrameType.Ping) with { ServerInstanceId = "invalid instance value" }, ProviderProtocol.MaxRequestBytes)),
    "invalid server instance rejected");

var current = Process.GetCurrentProcess();
var currentStartTicks = current.StartTime.ToUniversalTime().Ticks;
var ownerIdentityTicks = currentStartTicks;
using var ownerObserver = new Mutex(false, TargetMutationOwner.GetMutexName(current.Id, ownerIdentityTicks));
using (var firstOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
{
    Check(firstOwner is not null, "first companion acquires per-target mutation owner");
    Check(TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks) is null, "second companion is denied while owner lease is held");
    using var competingProcess = StartOwnerProbe(current.Id, ownerIdentityTicks);
    var competingResult = await competingProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(competingResult == "denied", "separate companion process is denied while owner lease is held");
    await competingProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
using (var replacementOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
    Check(replacementOwner is not null, "replacement can acquire after confirmed owner exit/release");
var pipeName = "provider-validation-" + Guid.NewGuid().ToString("N");
var bootstrapPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
var pipeSecret = Enumerable.Range(32, 32).Select(static value => (byte)value).ToArray();
var bootstrapJson = JsonSerializer.Serialize(new ProviderBootstrap
{
    PipeName = pipeName,
    SecretBase64 = Convert.ToBase64String(pipeSecret),
    VisualStudioPid = current.Id,
    VisualStudioStartTicks = currentStartTicks
}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
await File.WriteAllTextAsync(bootstrapPath, bootstrapJson);
var loadedBootstrap = ProviderClient.ReadBootstrap(bootstrapPath)!;
Check(loadedBootstrap.PipeName == pipeName && !File.Exists(bootstrapPath), "bootstrap is bounded, loaded, and deleted");
var serverInstance = "CCCCCCCCCCCCCCCCCCCCCC";
var epoch = Guid.NewGuid().ToString("N");
await using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                 PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
using (var clientCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    var clientTask = ProviderClient.RunAsync(loadedBootstrap, serverInstance, clientCancellation.Token);
    await pipe.WaitForConnectionAsync(clientCancellation.Token);
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var challengeFrame = new ProviderFrame
    {
        Type = ProviderFrameType.Challenge,
        RequestId = Guid.NewGuid().ToString("N"),
        VisualStudioPid = current.Id,
        VisualStudioStartTicks = currentStartTicks,
        ServerPid = current.Id,
        ServerStartTicks = currentStartTicks,
        ServerInstanceId = serverInstance,
        ProviderEpoch = epoch,
        DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(),
        Nonce = nonce
    };
    await ProviderProtocol.WriteAsync(pipe, challengeFrame, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    var hello = await ProviderProtocol.ReadAsync(pipe, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    Check(hello.Type == ProviderFrameType.Hello && ProviderProtocol.VerifyProof(pipeSecret, "client", hello),
        "named-pipe challenge receives identity-bound client proof");
    var ready = challengeFrame with { Type = ProviderFrameType.Ready,
        Proof = ProviderProtocol.CreateProof(pipeSecret, "server", challengeFrame with { Type = ProviderFrameType.Ready }) };
    await ProviderProtocol.WriteAsync(pipe, ready, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    var ping = challengeFrame with { Type = ProviderFrameType.Ping, RequestId = Guid.NewGuid().ToString("N"),
        DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(), Nonce = string.Empty };
    await ProviderProtocol.WriteAsync(pipe, ping, ProviderProtocol.MaxRequestBytes, clientCancellation.Token);
    var pong = await ProviderProtocol.ReadAsync(pipe, ProviderProtocol.MaxResponseBytes, clientCancellation.Token);
    Check(pong.Type == ProviderFrameType.Pong && pong.RequestId == ping.RequestId, "named-pipe ping receives matching pong");
    await ProviderProtocol.WriteAsync(pipe, ping with { Type = ProviderFrameType.Goodbye }, ProviderProtocol.MaxRequestBytes, clientCancellation.Token);
    await clientTask;
}
CryptographicOperations.ZeroMemory(pipeSecret);

using (var crashedOwner = StartOwnerProbe(current.Id, ownerIdentityTicks))
{
    Check(await crashedOwner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "acquired",
        "separate companion process acquires owner lease");
    crashedOwner.Kill(entireProcessTree: true);
    await crashedOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
using (var fencedOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
    Check(fencedOwner is not null && !fencedOwner.IsOwner, "abandoned owner lease remains quarantined after companion crash");

Console.WriteLine($"Passed {passed} provider protocol checks.");

Process StartOwnerProbe(int processId, long startTicks)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("--owner-probe");
    start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    start.ArgumentList.Add(startTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return Process.Start(start) ?? throw new InvalidOperationException("The owner probe process did not start.");
}

ProviderFrame Frame(ProviderFrameType type) => new()
{
    Type = type,
    RequestId = "11111111111141118111111111111111",
    VisualStudioPid = 101,
    VisualStudioStartTicks = 638900000000000000,
    ServerPid = 202,
    ServerStartTicks = 638900000000000001,
    ServerInstanceId = "AAAAAAAAAAAAAAAAAAAAAA",
    ProviderEpoch = "22222222222242228222222222222222",
    DeadlineUnixMilliseconds = type is ProviderFrameType.Ping or ProviderFrameType.Pong
        ? DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds() : 0,
    Nonce = type is ProviderFrameType.Challenge or ProviderFrameType.Hello or ProviderFrameType.Ready
        ? Convert.ToBase64String(secret) : string.Empty
};

byte[] Prefix(int length)
{
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
    return prefix;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine($"PASS {name}");
}

async Task Throws<TException>(Task operation, string name) where TException : Exception
{
    try { await operation; }
    catch (TException) { passed++; Console.WriteLine($"PASS {name}"); return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}: {name}");
}
