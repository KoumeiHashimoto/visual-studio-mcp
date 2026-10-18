using System.Buffers.Binary;
using System.Text;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Coordination;
using VisualStudioMcpServer.Observation;

if (args.Length == 3 && args[0] == "--owner-probe")
{
    using var probeOwner = TargetMutationOwner.TryAcquire(int.Parse(args[1]), long.Parse(args[2]));
    Console.WriteLine(probeOwner is null ? "denied" : "acquired");
    await Console.Out.FlushAsync();
    if (probeOwner is null) Environment.Exit(2);
    Thread.Sleep(Timeout.Infinite);
}

if (args.Length == 6 && args[0] == "--interop-client")
{
    // 親 repository の net472 host 検証から起動される companion 役。実 pipe 上で v2 の往復とイベントを確認する。
    Console.OutputEncoding = new UTF8Encoding(false);
    var interopSecret = Convert.FromBase64String(args[2]);
    using var interopPipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
    await interopPipe.ConnectAsync(10000);
    var self = Process.GetCurrentProcess();
    var interopHandshake = await ProviderClient.HandshakeAsync(interopPipe, interopSecret, int.Parse(args[3]), long.Parse(args[4]),
        self.Id, self.StartTime.ToUniversalTime().Ticks, args[5], "77777777777747778777777777777777", CancellationToken.None);
    CryptographicOperations.ZeroMemory(interopSecret);
    await using var interopChannel = new ProviderChannel(interopPipe, interopHandshake);
    var modeEvent = new TaskCompletionSource<ProviderEventNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
    interopChannel.EventReceived = notification => modeEvent.TrySetResult(notification);
    var probe = await interopChannel.SendAsync("capabilities.probe", new TargetOnlyRequestBody
    {
        Target = new ProviderTarget { VisualStudioPid = int.Parse(args[3]), VisualStudioStartTicks = long.Parse(args[4]) }
    }, TimeSpan.FromSeconds(5));
    var probeResult = await probe.Result.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"probe {probeResult.Outcome} {probeResult.DispatchState} {probeResult.Body}");
    var notification = await modeEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"event {notification.Operation} {((DebuggerModeChangedEventBody)notification.Body).ProviderSequence} {notification.HistoryGap}");
    await interopChannel.SendGoodbyeAsync();
    Console.WriteLine($"done {interopChannel.Violations}");
    return;
}

// 両 repository の検証で同じ値を検査する。catalog を変えたら版を上げ、両方を同時に更新する。
const string ExpectedCatalogHash = "cb847c6f323a206531b1c12e3e5d37ce7c867504a7dd8d463a86b02e1589337b";
const string ExpectedSessionKeyHex = "85b5f15f2406437ea7058f5f04ec5980fbeaa465122e0642f1c094b4f435fbde";
const string ExpectedResponseProof = "3FD5QGOUGmYyEDHEAW2bZQlemT7W48XWclHB8ZX2SI4=";
const string Epoch = "22222222222242228222222222222222";
const string ExpectedHelloProof = "ktml8W09ZUToqC2glKVR9Y2p0Ruw2WUsKPgikbTZa3M=";

var passed = 0;
var secret = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

// ---- catalog と既知解ベクトル ----
Check(ProviderProtocol.CurrentVersion == 2, "protocol version is 2");
Check(ProviderCatalog.Hash == ExpectedCatalogHash, "catalog hash matches the frozen literal");
Check(ProviderCatalog.CanonicalText.StartsWith("vsdmcp-provider-catalog-v2\ncapabilities.probe|read|c2v|65536|262144\n", StringComparison.Ordinal) &&
    !ProviderCatalog.CanonicalText.EndsWith('\n'), "canonical catalog text is header-prefixed, sorted and newline-joined");
Check(ProviderCatalog.Entries.Count(static entry => entry.Kind != ProviderOperationKind.Event) == 10 &&
    ProviderCatalog.Entries.Count(static entry => entry.Kind == ProviderOperationKind.Event) == 4, "catalog has 10 operations and 4 events");
Check(ProviderCatalog.Entries.Single(static entry => entry.Operation == "execution.dispatch").Kind == ProviderOperationKind.Execution &&
    ProviderCatalog.Entries.Count(static entry => entry.Kind == ProviderOperationKind.Execution) == 1, "execution.dispatch is the only execution operation");
var nonceKat = Convert.ToBase64String(secret);
var sessionKeyKat = ProviderProtocol.DeriveSessionKey(secret, nonceKat, Epoch, ProviderCatalog.Hash);
Check(Convert.ToHexString(sessionKeyKat).ToLowerInvariant() == ExpectedSessionKeyHex, "session key derivation matches known answer");
var katFrame = new ProviderFrame
{
    Type = ProviderFrameType.Response, Sequence = 7, RequestId = "33333333333343338333333333333333",
    CorrelationId = "11111111111141118111111111111111", Operation = "capabilities.probe", Outcome = "ok",
    DispatchState = "dispatched", VisualStudioPid = 101, VisualStudioStartTicks = 638900000000000000, ServerPid = 202,
    ServerStartTicks = 638900000000000001, ServerInstanceId = "AAAAAAAAAAAAAAAAAAAAAA", ProviderEpoch = Epoch,
    DeadlineUnixMilliseconds = 1800000000000, Body = "{\"text\":\"日本語\"}"
};
Check(ProviderProtocol.CreateSessionProof(sessionKeyKat, "v2c", katFrame) == ExpectedResponseProof, "body-bound session proof matches known answer");

// ---- proof の束縛 ----
var signed = katFrame with { Proof = ExpectedResponseProof };
Check(ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed), "session proof verifies");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed with { Body = "{\"text\":\"日本誤\"}" }), "body tampering is detected");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "c2v", signed), "reflected frame fails the direction label");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed with { CorrelationId = "44444444444444448444444444444444" }), "swapped correlationId is detected");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed with { Operation = "debugger.getState" }), "swapped operation is detected");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed with { Sequence = 8 }), "sequence is bound to the proof");
Check(!ProviderProtocol.VerifySessionProof(sessionKeyKat, "v2c", signed with { DispatchState = "unknown" }), "dispatch state is bound to the proof");
var otherEpochKey = ProviderProtocol.DeriveSessionKey(secret, nonceKat, Guid.NewGuid().ToString("N"), ProviderCatalog.Hash);
Check(!ProviderProtocol.VerifySessionProof(otherEpochKey, "v2c", signed), "frame from another epoch cannot verify");

var challenge = Frame(ProviderFrameType.Challenge);
var clientProof = ProviderProtocol.CreateProof(secret, "client", challenge);
Check(ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = clientProof }), "client proof accepted");
Check(!ProviderProtocol.VerifyProof(secret, "server", challenge with { Proof = clientProof }), "proof purpose is bound");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { ProviderEpoch = Guid.NewGuid().ToString("N"), Proof = clientProof }), "proof is bound to provider epoch");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { ServerInstanceId = "BBBBBBBBBBBBBBBBBBBBBB", Proof = clientProof }), "proof is bound to server instance");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Type = ProviderFrameType.Hello, Proof = clientProof }), "proof is bound to message type");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { CatalogHash = new string('0', 64), Proof = clientProof }), "handshake proof is bound to catalog hash");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = Convert.ToBase64String(new byte[32]) }), "incorrect secret proof rejected");
Check(!ProviderProtocol.VerifyProof(secret, "client", challenge with { Proof = "not-base64" }), "malformed proof rejected");
// Hello の本文（ownerFence）も handshake proof に束縛する。両 repository で同じ既知解を検査する。
var helloKat = Frame(ProviderFrameType.Hello) with
{
    DeadlineUnixMilliseconds = 1800000000000, Body = "{\"ownerFence\":\"55555555555545558555555555555555\"}"
};
Check(ProviderProtocol.CreateProof(secret, "client", helloKat) == ExpectedHelloProof, "hello proof binds the body and matches known answer");
Check(!ProviderProtocol.VerifyProof(secret, "client", helloKat with
{
    Proof = ExpectedHelloProof, Body = "{\"ownerFence\":\"66666666666646668666666666666666\"}"
}), "swapped hello ownerFence fails the handshake proof");
Check(ProviderProtocol.IsUuidN(challenge.ProviderEpoch), "provider epoch uses lowercase UUID-N form");
Check(!ProviderProtocol.IsUuidN("ABCDEFABCDEF4ABC8DEFABCDEFABCDEF"), "uppercase UUID rejected");
Check(!ProviderProtocol.IsUuidN("11111111111151118111111111111111"), "non-v4 UUID rejected");
Check(ProviderProtocol.MaxQueuedRequests == 8, "queue ceiling matches frozen contract");

// ---- session の方向と sequence ----
using (var companionSession = new ProviderSession(sessionKeyKat, isCompanion: true))
using (var providerSession = new ProviderSession(sessionKeyKat, isCompanion: false))
{
    var request = Session(ProviderFrameType.Request) with { Operation = "capabilities.probe", Body = TargetBody() };
    var first = companionSession.Seal(request);
    companionSession.CommitSent(first);
    var second = companionSession.Seal(request with { RequestId = Guid.NewGuid().ToString("N") });
    companionSession.CommitSent(second);
    Check(first.Sequence == 1 && second.Sequence == 2, "per-direction sequence starts at 1 and increments");
    providerSession.Open(first);
    Violation(() => providerSession.Open(first), "duplicate sequence is a violation");
    using (var gapSession = new ProviderSession(sessionKeyKat, isCompanion: false))
        Violation(() => gapSession.Open(second), "sequence gap is a violation");
    providerSession.Open(second);
    var response = providerSession.Seal(Session(ProviderFrameType.Response) with
    {
        CorrelationId = first.RequestId, Operation = "capabilities.probe", Outcome = "ok", DispatchState = "dispatched", Body = "{}"
    });
    using (var receiver = new ProviderSession(sessionKeyKat, isCompanion: false))
        Violation(() => receiver.Open(response), "response received by the provider is a wrong-direction violation");
    using (var receiver = new ProviderSession(sessionKeyKat, isCompanion: true))
        Violation(() => receiver.Open(first), "reflected request is rejected by its sender");
    Check(Throws(() => companionSession.Seal(Session(ProviderFrameType.Response) with
    {
        CorrelationId = first.RequestId, Operation = "capabilities.probe", Outcome = "ok", DispatchState = "dispatched", Body = "{}"
    })), "companion cannot send provider-direction frames");
}

// ---- envelope と本文の境界 ----
var bytes = ProviderProtocol.Serialize(Session(ProviderFrameType.Ping), ProviderProtocol.MaxRequestBytes);
var framed = new byte[bytes.Length + 4];
BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), bytes.Length);
bytes.CopyTo(framed, 4);
var decoded = await ProviderProtocol.ReadAsync(new MemoryStream(framed), ProviderProtocol.MaxRequestBytes, CancellationToken.None);
Check(decoded.Type == ProviderFrameType.Ping && decoded.Sequence == 1, "length-prefixed frame round trips");
await ThrowsAsync<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(ProviderProtocol.MaxRequestBytes + 1)),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "oversized request rejected from prefix");
await ThrowsAsync<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(ProviderProtocol.MaxResponseBytes + 1)),
    ProviderProtocol.MaxResponseBytes, CancellationToken.None), "oversized response rejected from prefix");
await ThrowsAsync<EndOfStreamException>(ProviderProtocol.ReadAsync(new MemoryStream(new byte[] { 1, 2, 3 }),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "partial prefix rejected");
await ThrowsAsync<EndOfStreamException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(10).Concat(new byte[] { 1, 2 }).ToArray()),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "partial payload rejected");
await ThrowsAsync<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(2).Concat(new byte[] { (byte)'{', (byte)'}' }).ToArray()),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "malformed envelope rejected");
var unknownMember = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).TrimEnd('}') + ",\"extra\":1}");
await ThrowsAsync<InvalidDataException>(ProviderProtocol.ReadAsync(new MemoryStream(Prefix(unknownMember.Length).Concat(unknownMember).ToArray()),
    ProviderProtocol.MaxRequestBytes, CancellationToken.None), "unknown envelope member rejected");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Ping) with { ProtocolVersion = 1 }, ProviderProtocol.MaxRequestBytes)) &&
    ThrowsType<ProviderProtocolMismatchException>(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Ping) with { ProtocolVersion = 1 }, ProviderProtocol.MaxRequestBytes)),
    "protocol version 1 is a mismatch");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Request) with { Operation = "workspace.anything", Body = "{}" }, ProviderProtocol.MaxRequestBytes)), "unknown operation rejected");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Request) with { Operation = "debugger.modeChanged", Body = "{}" }, ProviderProtocol.MaxRequestBytes)), "event name cannot be a request");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Request) with { Operation = "capabilities.probe", Body = "{}", Sequence = 0 }, ProviderProtocol.MaxRequestBytes)), "session frame with sequence 0 rejected");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Cancel) with { CorrelationId = "", }, ProviderProtocol.MaxRequestBytes)), "cancel without correlation rejected");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Response) with { CorrelationId = Epoch, Operation = "capabilities.probe", Outcome = "ok", DispatchState = "sometimes", Body = "{}" }, ProviderProtocol.MaxRequestBytes)), "invalid dispatch state rejected");
Check(Throws(() => ProviderProtocol.Serialize(Frame(ProviderFrameType.Ready) with { ImplementedOperations = ["capabilities.probe", "capabilities.probe"] }, ProviderProtocol.MaxHandshakeBytes)), "duplicate implemented operation rejected");
Check(Throws(() => ProviderProtocol.Serialize(Frame(ProviderFrameType.Ready) with { ImplementedOperations = ["debugger.modeChanged"] }, ProviderProtocol.MaxHandshakeBytes)), "event cannot be an implemented operation");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Ping) with { RequestId = "A".PadRight(32, '0') }, ProviderProtocol.MaxRequestBytes)), "noncanonical request ID rejected");
Check(Throws(() => ProviderProtocol.Serialize(Session(ProviderFrameType.Ping) with { ServerInstanceId = "invalid instance value" }, ProviderProtocol.MaxRequestBytes)), "invalid server instance rejected");

Check(ProviderBodyRules.TryValidateRequest("documents.list", "{\"target\":" + TargetJson() + ",\"scope\":\"open\",\"pageSize\":100,\"cursor\":null}", out _), "documents.list body accepted at its page ceiling");
Check(!ProviderBodyRules.TryValidateRequest("documents.list", "{\"target\":" + TargetJson() + ",\"scope\":\"open\",\"pageSize\":101,\"cursor\":null}", out _), "page size above 100 is invalid_request");
Check(!ProviderBodyRules.TryValidateRequest("documents.read", "{\"target\":" + TargetJson() + ",\"documentId\":\"d1\",\"expectedBufferVersion\":null,\"startLine\":1,\"lineCount\":501,\"maxCharacters\":10}", out _), "line count above 500 is invalid_request");
Check(!ProviderBodyRules.TryValidateRequest("capabilities.probe", "{}", out _), "missing target is invalid_request");
Violation(() => ProviderBodyRules.TryValidateRequest("capabilities.probe", "{\"target\":" + TargetJson() + ",\"extra\":true}", out _), "unknown body member is a violation");
Violation(() => ProviderBodyRules.TryValidateRequest("capabilities.probe", "[]", out _), "non-object body is a violation");
Violation(() => ProviderProtocol.ValidateObjectBody("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":1}}}}}}}"), "body deeper than 6 is a violation");
Violation(() => ProviderBodyRules.ParseEvent("debugger.modeChanged", "{\"providerSequence\":0,\"state\":\"design\",\"debugSessionId\":null,\"processSetSnapshotId\":null,\"launchOperationId\":null,\"observedAtUtc\":\"2026-10-18T00:00:00Z\"}"), "mode event sequence 0 is a violation");
Violation(() => ProviderBodyRules.ParseError("{\"code\":\"internal_error\",\"message\":\"" + new string('x', 513) + "\"}"), "error message above 512 characters is a violation");

// ---- 所有権の照合（fake reconcile 応答） ----
Check(ProviderOwnershipReconciler.Evaluate(Reconcile(true, 0, true, false, "design")).Release, "quarantine releases when every reconcile condition holds");
Check(!ProviderOwnershipReconciler.Evaluate(Reconcile(false, 0, true, false, "design")).Release, "quarantine stays when not releasable");
Check(!ProviderOwnershipReconciler.Evaluate(Reconcile(true, 1, true, false, "design")).Release, "quarantine stays with unsettled earlier-epoch work");
Check(!ProviderOwnershipReconciler.Evaluate(Reconcile(true, 0, false, false, "design")).Release, "quarantine stays when previous owner exit is unproven");
Check(!ProviderOwnershipReconciler.Evaluate(Reconcile(true, 0, true, true, "design")).Release, "quarantine stays while a build is busy");
Check(!ProviderOwnershipReconciler.Evaluate(Reconcile(true, 0, true, false, "unknown")).Release, "quarantine stays when debugger state is unknown");
var refused = ProviderOwnershipReconciler.Evaluate(new ProviderCallResult(Epoch, "ownership.reconcile", "error", "unsupported_operation", "notDispatched", null, null));
Check(!refused.Release && refused.Reason == "user_action_required", "unsupported reconcile keeps quarantine with user_action_required");

// ---- 所有者 lease ----
var current = Process.GetCurrentProcess();
var currentStartTicks = current.StartTime.ToUniversalTime().Ticks;
var ownerIdentityTicks = currentStartTicks;
using var ownerObserver = new Mutex(false, TargetMutationOwner.GetMutexName(current.Id, ownerIdentityTicks));
using (var firstOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
{
    Check(firstOwner is not null && ProviderProtocol.IsUuidN(firstOwner.OwnerGeneration), "first companion acquires per-target mutation owner with a UUID-N fence");
    Check(TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks) is null, "second companion is denied while owner lease is held");
    using var competingProcess = StartOwnerProbe(current.Id, ownerIdentityTicks);
    var competingResult = await competingProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(competingResult == "denied", "separate companion process is denied while owner lease is held");
    await competingProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
using (var replacementOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
    Check(replacementOwner is not null, "replacement can acquire after confirmed owner exit/release");

// ---- named pipe 上の handshake ----
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
    var observations = new List<string>();
    var clientTask = ProviderClient.RunAsync(loadedBootstrap, serverInstance, clientCancellation.Token,
        observation => { lock (observations) observations.Add(observation.Reason ?? string.Empty); });
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
        Nonce = nonce,
        CatalogHash = ProviderCatalog.Hash
    };
    await ProviderProtocol.WriteAsync(pipe, challengeFrame, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    var hello = await ProviderProtocol.ReadAsync(pipe, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    Check(hello.Type == ProviderFrameType.Hello && hello.CatalogHash == ExpectedCatalogHash &&
        ProviderProtocol.VerifyProof(pipeSecret, "client", hello), "named-pipe challenge receives catalog-bound client proof");
    Check(ProviderProtocol.ParseBody<ProviderOwnerHelloBody>(hello.Body) is not null, "hello carries the owner fence body");
    var ready = challengeFrame with { Type = ProviderFrameType.Ready, ImplementedOperations = ["capabilities.probe"] };
    ready = ready with { Proof = ProviderProtocol.CreateProof(pipeSecret, "server", ready) };
    await ProviderProtocol.WriteAsync(pipe, ready, ProviderProtocol.MaxHandshakeBytes, clientCancellation.Token);
    var pipeSessionKey = ProviderProtocol.DeriveSessionKey(pipeSecret, nonce, epoch, ProviderCatalog.Hash);
    using var pipeSession = new ProviderSession(pipeSessionKey, isCompanion: false);
    var identity = challengeFrame with { Nonce = string.Empty, CatalogHash = string.Empty };
    var ping = pipeSession.Seal(identity with { Type = ProviderFrameType.Ping, RequestId = Guid.NewGuid().ToString("N"),
        DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds() });
    pipeSession.CommitSent(ping);
    await ProviderProtocol.WriteAsync(pipe, ping, ProviderProtocol.MaxRequestBytes, clientCancellation.Token);
    var pong = await ProviderProtocol.ReadAsync(pipe, ProviderProtocol.MaxResponseBytes, clientCancellation.Token);
    pipeSession.Open(pong);
    Check(pong.Type == ProviderFrameType.Pong && pong.RequestId == ping.RequestId && pong.Sequence == 1,
        "named-pipe ping receives session-authenticated pong");
    Check(ProviderClient.ActiveChannel?.ImplementedOperations.Contains("capabilities.probe") == true, "ready implementedOperations reach the channel");
    var goodbye = pipeSession.Seal(identity with { Type = ProviderFrameType.Goodbye, RequestId = Guid.NewGuid().ToString("N"),
        DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds() });
    pipeSession.CommitSent(goodbye);
    await ProviderProtocol.WriteAsync(pipe, goodbye, ProviderProtocol.MaxRequestBytes, clientCancellation.Token);
    await clientTask;
    Check(ProviderClient.ActiveChannel is null, "channel is not reused after goodbye");
    lock (observations) Check(observations.Contains("provider_closed_channel"), "goodbye reports a closed provider channel");
}

{
    var (server, client) = await CreatePairAsync();
    await using var _server = server;
    await using var _client = client;
    var handshake = ProviderClient.HandshakeAsync(client, pipeSecret, current.Id, currentStartTicks, current.Id, currentStartTicks,
        serverInstance, null, CancellationToken.None);
    var mismatched = new ProviderFrame
    {
        Type = ProviderFrameType.Challenge, RequestId = Guid.NewGuid().ToString("N"), VisualStudioPid = current.Id,
        VisualStudioStartTicks = currentStartTicks, ServerPid = current.Id, ServerStartTicks = currentStartTicks,
        ServerInstanceId = serverInstance, ProviderEpoch = epoch,
        DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(), Nonce = "bm9uY2U=",
        CatalogHash = new string('f', 64)
    };
    await ProviderProtocol.WriteAsync(server, mismatched, ProviderProtocol.MaxHandshakeBytes, CancellationToken.None);
    await ThrowsAsync<ProviderProtocolMismatchException>(handshake, "catalog hash mismatch fails the handshake as a protocol mismatch");
}
CryptographicOperations.ZeroMemory(pipeSecret);

// ---- channel: 正常応答・キュー・取消 ----
ProviderChannel.ResponseGrace = TimeSpan.FromMilliseconds(300);
await using (var harness = await Harness.CreateAsync(sessionKeyKat, ["capabilities.probe", "execution.dispatch", "ownership.reconcile"]))
{
    var call = await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadAsync();
    Check(request.Type == ProviderFrameType.Request && request.Operation == "capabilities.probe" && request.Sequence == 1 &&
        request.DeadlineUnixMilliseconds <= DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(), "request carries operation, sequence and bounded deadline");
    await harness.Provider.RespondAsync(request, "ok", "dispatched", "{\"providerVersion\":\"fake\"}");
    var result = await call.Result.WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.IsOk && result.Body == "{\"providerVersion\":\"fake\"}", "fake capabilities.probe round trips");

    var unsupported = await (await harness.Channel.SendAsync("documents.read", new DocumentsReadRequestBody
    {
        Target = Target().Target, DocumentId = "d1", StartLine = 1, LineCount = 1, MaxCharacters = 10
    }, TimeSpan.FromSeconds(5))).Result;
    Check(unsupported.Code == "unsupported_operation" && unsupported.DispatchState == "notDispatched", "operation missing from Ready is unsupported without writing");
    var wrongTarget = await (await harness.Channel.SendAsync("capabilities.probe", new TargetOnlyRequestBody
    {
        Target = new ProviderTarget { VisualStudioPid = 999, VisualStudioStartTicks = 1 }
    }, TimeSpan.FromSeconds(5))).Result;
    Check(wrongTarget.Code == "target_mismatch", "request for another IDE identity is rejected before writing");

    var calls = new List<ProviderPendingCall>();
    for (var index = 0; index < 8; index++)
        calls.Add(await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5)));
    var ninth = await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var ninthResult = await ninth.Result.WaitAsync(TimeSpan.FromMilliseconds(200));
    Check(ninthResult.Code == "provider_busy" && ninthResult.DispatchState == "notDispatched", "ninth outstanding request fails with provider_busy immediately");
    var received = new List<ProviderFrame>();
    for (var index = 0; index < 8; index++) received.Add(await harness.Provider.ReadAsync());
    Check(received.Select(static frame => frame.Sequence).SequenceEqual(Enumerable.Range(2, 8).Select(static value => (ulong)value)),
        "only eight requests reach the wire, gap-free");

    Check(await harness.Channel.CancelAsync(calls[0].RequestId), "cancel is written for a pending request");
    var cancel = await harness.Provider.ReadAsync();
    Check(cancel.Type == ProviderFrameType.Cancel && cancel.CorrelationId == calls[0].RequestId && cancel.Body.Length == 0, "cancel frame correlates to its request");
    await harness.Provider.RespondAsync(received[0], "error", "notDispatched", "{\"code\":\"cancelled\",\"message\":\"\"}");
    var cancelled = await calls[0].Result.WaitAsync(TimeSpan.FromSeconds(5));
    Check(cancelled.Code == "cancelled" && cancelled.DispatchState == "notDispatched", "cancel before dispatch settles as cancelled");
    for (var index = 1; index < 8; index++)
        await harness.Provider.RespondAsync(received[index], "ok", "dispatched", "{}");
    await Task.WhenAll(calls.Select(static item => item.Result)).WaitAsync(TimeSpan.FromSeconds(5));
    Check(harness.Channel.OutstandingRequests == 0 && !harness.Channel.IsTerminated, "queue drains after responses");
}

// ---- channel: timeout と遅延応答 ----
await using (var harness = await Harness.CreateAsync(sessionKeyKat, ["capabilities.probe", "execution.dispatch"]))
{
    var read = await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromMilliseconds(200));
    var readRequest = await harness.Provider.ReadAsync();
    var readResult = await read.Result.WaitAsync(TimeSpan.FromSeconds(5));
    Check(readResult.Code == "provider_timeout", "read without response settles as provider_timeout");
    await harness.Provider.RespondAsync(readRequest, "ok", "dispatched", "{}");

    var settled = new TaskCompletionSource<ProviderExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Channel.ExecutionSettled = record => settled.TrySetResult(record);
    var operationId = Guid.NewGuid().ToString("N");
    var execution = await harness.Channel.SendAsync("execution.dispatch", Dispatch(operationId), TimeSpan.FromMilliseconds(200));
    var executionRequest = await harness.Provider.ReadAsync();
    var executionResult = await execution.Result.WaitAsync(TimeSpan.FromSeconds(5));
    Check(executionResult.Code == "result_unknown" && harness.Channel.OutstandingRequests == 1 &&
        harness.Channel.ExecutionRecords.Single().State == "uncertain", "execution without response is result_unknown and stays pending");
    Check(harness.Channel.LateResponses == 1 && !harness.Channel.IsTerminated, "late read response is discarded without violation");
    await harness.Provider.RespondAsync(executionRequest, "ok", "dispatched", "{\"state\":\"accepted\"}");
    var record = await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(record.OperationId == operationId && record.State == "accepted" && harness.Channel.OutstandingRequests == 0,
        "late execution response settles the same retained record");
    await harness.Provider.RespondAsync(executionRequest, "ok", "dispatched", "{\"state\":\"accepted\"}");
    await harness.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(harness.Channel.TerminationReason == ProviderChannel.ProtocolViolation, "second response for the same request is a violation");
    Check(!harness.Provider.HasPendingInput(), "no request was replayed");
}

// ---- channel: 契約違反で切断 ----
await ViolationCase("response without a pending request", async harness =>
    await harness.Provider.RespondAsync(harness.Provider.Identity with { RequestId = Guid.NewGuid().ToString("N"), Operation = "capabilities.probe" },
        "ok", "dispatched", "{}"));
await ViolationCase("response with a swapped operation", async harness =>
{
    await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadAsync();
    await harness.Provider.RespondAsync(request with { Operation = "ownership.reconcile" }, "ok", "dispatched", "{}");
});
await ViolationCase("tampered response body", async harness =>
{
    await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadAsync();
    var sealedFrame = harness.Provider.SealResponse(request, "ok", "dispatched", "{\"a\":1}");
    await harness.Provider.WriteRawAsync(sealedFrame with { Body = "{\"a\":2}" });
});
await ViolationCase("reflected request frame", async harness =>
{
    await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadRawAsync();
    await harness.Provider.WriteRawAsync(request);
});
await ViolationCase("sequence gap", async harness =>
{
    harness.Provider.SkipSequence();
    await harness.Provider.WriteRawAsync(harness.Provider.SealEvent("provider.eventsDropped", "{\"droppedCount\":1,\"firstProviderSequence\":0,\"lastProviderSequence\":0}"));
});
await ViolationCase("duplicate sequence", async harness =>
{
    var frame = harness.Provider.SealEvent("provider.eventsDropped", "{\"droppedCount\":1,\"firstProviderSequence\":0,\"lastProviderSequence\":0}");
    await harness.Provider.WriteRawAsync(frame);
    await harness.Provider.WriteRawAsync(frame);
});
await ViolationCase("wrong-direction cancel from provider", async harness =>
{
    var frame = harness.Provider.Identity with
    {
        Type = ProviderFrameType.Cancel, Sequence = 1, RequestId = Guid.NewGuid().ToString("N"),
        CorrelationId = Guid.NewGuid().ToString("N"), DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds()
    };
    await harness.Provider.WriteRawAsync(frame with { Proof = harness.Provider.ProofFor(frame) });
});
await ViolationCase("malformed response body", async harness =>
{
    await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadAsync();
    await harness.Provider.RespondAsync(request, "ok", "dispatched", "{\"a\":");
});
await ViolationCase("over-limit event frame", async harness =>
    await harness.Provider.EventAsync("provider.featureAvailability",
        "{\"features\":[{\"featureKey\":\"" + new string('k', 17000) + "\",\"availability\":\"supported\",\"reason\":\"r\",\"observedAtUtc\":\"2026-10-18T00:00:00Z\"}]}"));
await ViolationCase("successful response that was not dispatched", async harness =>
{
    await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var request = await harness.Provider.ReadAsync();
    await harness.Provider.RespondAsync(request, "ok", "notDispatched", "{}");
});

// ---- channel: 切断時の no replay ----
await using (var harness = await Harness.CreateAsync(sessionKeyKat, ["capabilities.probe", "execution.dispatch"]))
{
    var read = await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5));
    var execution = await harness.Channel.SendAsync("execution.dispatch", Dispatch(Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(20));
    await harness.Provider.ReadAsync();
    await harness.Provider.ReadAsync();
    await harness.Provider.DisposeAsync();
    var readResult = await read.Result.WaitAsync(TimeSpan.FromSeconds(5));
    var executionResult = await execution.Result.WaitAsync(TimeSpan.FromSeconds(5));
    Check(readResult.Code == "provider_disconnected" && executionResult.Code == "result_unknown" &&
        harness.Channel.ExecutionRecords.Single().State == "uncertain", "disconnect fails reads and keeps executions as result_unknown");
    var after = await (await harness.Channel.SendAsync("capabilities.probe", Target(), TimeSpan.FromSeconds(5))).Result;
    Check(after.Code == "provider_disconnected" && after.DispatchState == "notDispatched", "channel loss is terminal; later requests are not written");
}

// ---- channel: イベント ----
await using (var harness = await Harness.CreateAsync(sessionKeyKat, ["execution.dispatch"]))
{
    var events = new List<ProviderEventNotification>();
    var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Channel.EventReceived = notification =>
    {
        lock (events) { events.Add(notification); if (events.Count == 5) four.TrySetResult(); }
    };
    await harness.Provider.EventAsync("debugger.modeChanged", Mode(1));
    await harness.Provider.EventAsync("debugger.modeChanged", Mode(2));
    await harness.Provider.EventAsync("debugger.modeChanged", Mode(4));
    await harness.Provider.EventAsync("provider.eventsDropped", "{\"droppedCount\":3,\"firstProviderSequence\":5,\"lastProviderSequence\":7}");
    await harness.Provider.EventAsync("execution.observed", "{\"operationId\":\"" + Guid.NewGuid().ToString("N") + "\",\"state\":\"observed\",\"code\":null,\"debugSessionId\":null,\"processSetSnapshotId\":null}");
    await harness.Provider.EventAsync("debugger.modeChanged", Mode(8));
    await four.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);
    lock (events)
    {
        Check(!events[0].HistoryGap && !events[1].HistoryGap && events[2].HistoryGap, "mode-change providerSequence gap is a history gap, not a violation");
        Check(events[3].HistoryGap && events[4].HistoryGap, "eventsDropped marks the following mode history as a gap");
    }
    Check(harness.Channel.IgnoredObservations == 1 && !harness.Channel.IsTerminated, "unknown execution.observed is counted and ignored");

    await harness.Provider.PingAsync();
    var pong = await harness.Provider.ReadAsync();
    Check(pong.Type == ProviderFrameType.Pong && pong.Sequence == 1, "ping after Ready receives a sequenced pong");
}

// ---- ProviderRuntime: mode event を観測 store へ、execution.observed を coordinator へ振り分ける ----
var routedObservations = new List<ExecutionObservedEventBody>();
ProviderRuntime.ExecutionObserved = (_, body) => { lock (routedObservations) routedObservations.Add(body); };
await using (var harness = await Harness.CreateAsync(sessionKeyKat, ["execution.dispatch"], ProviderRuntime.Attach))
{
    var store = ProviderRuntime.ObservationStore!;
    Check(store.ProviderEpoch == harness.Channel.Identity.ProviderEpoch && ReferenceEquals(ProviderRuntime.Channel, harness.Channel),
        "runtime attaches a per-epoch observation store before the read loop starts");
    var session = Guid.NewGuid().ToString("N");
    var launchOperation = Guid.NewGuid().ToString("N");
    await harness.Channel.SendAsync("execution.dispatch", Dispatch(launchOperation), TimeSpan.FromSeconds(20));
    await harness.Provider.ReadAsync();
    await harness.Provider.EventAsync("debugger.modeChanged", ModeFor(1, "running", session, launchOperation));
    await harness.Provider.EventAsync("debugger.modeChanged", ModeFor(3, "break", session, launchOperation));
    await harness.Provider.EventAsync("provider.eventsDropped", "{\"droppedCount\":3,\"firstProviderSequence\":4,\"lastProviderSequence\":6}");
    await harness.Provider.EventAsync("debugger.modeChanged", ModeFor(7, "design", session, launchOperation));
    await harness.Provider.EventAsync("execution.observed", "{\"operationId\":\"" + launchOperation + "\",\"state\":\"observed\",\"code\":\"debug_session_started\",\"debugSessionId\":\"" + session + "\",\"processSetSnapshotId\":null}");
    Check(await store.WaitForProviderSequenceAsync(7, TimeSpan.FromSeconds(5), CancellationToken.None), "mode events reach the observation store");
    var observations = store.Snapshot();
    Check(observations.Select(static item => item.State).SequenceEqual([DebuggerObservedState.Running, DebuggerObservedState.Break, DebuggerObservedState.Design]) &&
        observations.Select(static item => item.Sequence).SequenceEqual([1L, 3L, 5L]) &&
        observations.All(item => item.DebugSessionId == session && item.LaunchOperationId == launchOperation),
        "providerSequence gaps and eventsDropped become single public gap markers without duplicate marking");
    var expiredWait = await store.WaitAsync(DebuggerWaitDesiredState.Design, 1, session, null, TimeSpan.FromSeconds(1), CancellationToken.None);
    var freshWait = await store.WaitAsync(DebuggerWaitDesiredState.Design, 4, session, null, TimeSpan.FromSeconds(1), CancellationToken.None);
    Check(expiredWait.Status == DebuggerObservationWaitStatus.EventHistoryExpired && freshWait.Status == DebuggerObservationWaitStatus.Matched,
        "waits spanning a provider gap expire and fresh waits match the terminal design event");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline) { lock (routedObservations) if (routedObservations.Count > 0) break; await Task.Delay(20); }
    lock (routedObservations)
        Check(routedObservations.Count == 1 && routedObservations[0].OperationId == launchOperation, "execution.observed is routed to the execution observer");
    ProviderRuntime.Detach(harness.Channel);
    Check(store.IsClosed && ProviderRuntime.ObservationStore is null && ProviderRuntime.Channel is null,
        "detaching the channel closes the store so waits end as provider_disconnected");
}
ProviderRuntime.ExecutionObserved = null;

// ---- debugger.getState 応答本文の検査 ----
var validState = JsonSerializer.Serialize(new DebuggerGetStateResponseBody
{
    State = "break", DebugSessionId = Guid.NewGuid().ToString("N"), ProcessSetSnapshotId = Guid.NewGuid().ToString("N"),
    Processes = [new DebuggerProcessIdentity { ProcessId = 10, StartTicks = 5, Name = "App.exe" }], CurrentThreadAvailable = true,
    SolutionPath = "C:\\fixture\\App.sln", Configuration = "Debug", Platform = "Any CPU", ProviderSequence = 3,
    StartupSnapshot = new StartupSnapshotBody
    {
        SnapshotId = Guid.NewGuid().ToString("N"), StartupProjects = [new StartupProjectIdentity { UniqueName = "App\\App.csproj", ProviderKind = "csharp-sdk" }],
        ProviderKind = "csharp-sdk", ProfileStatus = "default", LaunchSupported = true, BuildMayOccur = true
    }
}, ProviderProtocol.BodyJsonOptions);
Check(ProviderExecutionBodies.ParseGetState(validState).Processes.Single().Name == "App.exe", "typed debugger.getState body is accepted");
Violation(() => ProviderExecutionBodies.ParseGetState(validState.Replace("\"state\":\"break\"", "\"state\":\"paused\"")), "unknown debugger state is a violation");
Violation(() => ProviderExecutionBodies.ParseGetState(JsonSerializer.Serialize(new DebuggerGetStateResponseBody
{
    State = "running", Processes = Enumerable.Range(1, 33).Select(static pid => new DebuggerProcessIdentity { ProcessId = pid, Name = "p" }).ToArray()
}, ProviderProtocol.BodyJsonOptions)), "more than 32 debugged processes is a violation");
Violation(() => ProviderExecutionBodies.ParseGetState("{\"state\":\"design\",\"debugSessionId\":\"not-a-uuid\"}"), "non-UUID session identity is a violation");
Violation(() => ProviderExecutionBodies.ParseGetState("{\"state\":\"design\",\"extra\":1}"), "unknown getState member is a violation");

// ---- 所有者 lease の abandoned quarantine と解除 ----
using (var crashedOwner = StartOwnerProbe(current.Id, ownerIdentityTicks))
{
    Check(await crashedOwner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "acquired",
        "separate companion process acquires owner lease");
    crashedOwner.Kill(entireProcessTree: true);
    await crashedOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
using (var fencedOwner = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
{
    Check(fencedOwner is not null && !fencedOwner.IsOwner && fencedOwner.IsQuarantined, "abandoned owner lease remains quarantined after companion crash");
    Thread.Sleep(200);
    Check(!fencedOwner!.IsOwner, "no timer clears the quarantine");
    var decision = ProviderOwnershipReconciler.Evaluate(Reconcile(true, 0, true, false, "design"));
    Check(decision.Release && fencedOwner.ReleaseQuarantine() && fencedOwner.IsOwner && !fencedOwner.IsQuarantined,
        "successful reconcile releases quarantine into ownership");
}
using (var afterRelease = TargetMutationOwner.TryAcquire(current.Id, ownerIdentityTicks))
    Check(afterRelease is not null && afterRelease.IsOwner, "released owner lease is cleanly released at shutdown");

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

async Task ViolationCase(string name, Func<Harness, Task> action)
{
    await using var harness = await Harness.CreateAsync(sessionKeyKat, ["capabilities.probe", "ownership.reconcile"]);
    await action(harness);
    await harness.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(harness.Channel.TerminationReason == ProviderChannel.ProtocolViolation && harness.Channel.Violations == 1,
        $"{name} closes the channel as a protocol violation");
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
    ProviderEpoch = Epoch,
    DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(),
    Nonce = Convert.ToBase64String(secret),
    CatalogHash = ProviderCatalog.Hash
};

ProviderFrame Session(ProviderFrameType type) => Frame(type) with
{
    Sequence = 1,
    RequestId = Guid.NewGuid().ToString("N"),
    Nonce = string.Empty,
    CatalogHash = string.Empty
};

static TargetOnlyRequestBody Target() => new()
{
    Target = new ProviderTarget { VisualStudioPid = 101, VisualStudioStartTicks = 638900000000000000, WorkspacePath = "C:\\fixture" }
};

static string TargetJson() => "{\"visualStudioPid\":101,\"visualStudioStartTicks\":638900000000000000,\"workspacePath\":null}";

static string TargetBody() => "{\"target\":" + TargetJson() + "}";

static ExecutionDispatchRequestBody Dispatch(string operationId) => new()
{
    Target = Target().Target,
    OperationId = operationId,
    Action = "startDebugging",
    PolicyFlags = ["allowLaunch"],
    OwnerFence = "55555555555545558555555555555555"
};

static string ModeFor(ulong sequence, string state, string session, string? launchOperation) =>
    "{\"providerSequence\":" + sequence + ",\"state\":\"" + state + "\",\"debugSessionId\":\"" + session + "\",\"processSetSnapshotId\":null,\"launchOperationId\":" +
    (launchOperation is null ? "null" : "\"" + launchOperation + "\"") + ",\"observedAtUtc\":\"2026-10-18T00:00:00Z\"}";

static string Mode(ulong sequence) =>
    "{\"providerSequence\":" + sequence + ",\"state\":\"running\",\"debugSessionId\":\"s1\",\"processSetSnapshotId\":null,\"launchOperationId\":null,\"observedAtUtc\":\"2026-10-18T00:00:00Z\"}";

static ProviderCallResult Reconcile(bool releasable, int unsettled, bool exited, bool buildBusy, string debuggerState)
{
    var body = JsonSerializer.Serialize(new OwnershipReconcileResponseBody
    {
        QuarantineReleasable = releasable,
        UnsettledOperations = Enumerable.Range(0, unsettled).Select(static _ => new OwnershipReconcileOperation
        {
            OperationId = Guid.NewGuid().ToString("N"), Action = "startDebugging", DispatchState = "unknown"
        }).ToArray(),
        PreviousOwnerExited = exited,
        BuildBusy = buildBusy,
        DebuggerState = debuggerState
    }, ProviderProtocol.BodyJsonOptions);
    return new ProviderCallResult(Epoch, "ownership.reconcile", "ok", null, "dispatched", body, null);
}

static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> CreatePairAsync()
{
    var name = "provider-validation-" + Guid.NewGuid().ToString("N");
    var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1 << 20, 1 << 20);
    var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
    return (server, client);
}

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

void Violation(Action action, string name)
{
    try { action(); }
    catch (ProviderProtocolViolationException) { passed++; Console.WriteLine($"PASS {name}"); return; }
    throw new InvalidOperationException($"Expected violation: {name}");
}

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ProviderProtocolException) { return true; }
}

static bool ThrowsType<TException>(Action action) where TException : Exception
{
    try { action(); return false; }
    catch (TException) { return true; }
    catch (Exception) { return false; }
}

async Task ThrowsAsync<TException>(Task operation, string name) where TException : Exception
{
    try { await operation; }
    catch (TException) { passed++; Console.WriteLine($"PASS {name}"); return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}: {name}");
}

/// <summary>検証用の偽 VSIX provider。session key で frame を封印し、companion channel と pipe で接続する。</summary>
sealed class FakeProvider : IAsyncDisposable
{
    private readonly byte[] _key;
    private readonly ProviderSession _session;
    private Task<int>? _peek;
    private readonly byte[] _peekBuffer = new byte[1];

    internal FakeProvider(Stream stream, byte[] key, ProviderFrame identity)
    {
        Stream = stream;
        _key = key;
        Identity = identity;
        _session = new ProviderSession(key, isCompanion: false);
    }

    internal Stream Stream { get; }
    internal ProviderFrame Identity { get; }

    internal async Task<ProviderFrame> ReadRawAsync()
    {
        var frame = await ProviderProtocol.ReadAsync(Stream, ProviderProtocol.MaxRequestBytes, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        return frame;
    }

    internal async Task<ProviderFrame> ReadAsync()
    {
        var frame = await ReadRawAsync();
        _session.Open(frame);
        return frame;
    }

    internal bool HasPendingInput()
    {
        _peek ??= Stream.ReadAsync(_peekBuffer, 0, 1);
        return _peek.Wait(300) && _peek.Result > 0;
    }

    internal ProviderFrame SealResponse(ProviderFrame request, string outcome, string dispatchState, string body)
    {
        var frame = _session.Seal(Identity with
        {
            Type = ProviderFrameType.Response, RequestId = Guid.NewGuid().ToString("N"), CorrelationId = request.RequestId,
            Operation = request.Operation, Outcome = outcome, DispatchState = dispatchState,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(), Body = body
        });
        _session.CommitSent(frame);
        return frame;
    }

    internal ProviderFrame SealEvent(string operation, string body)
    {
        var frame = _session.Seal(Identity with
        {
            Type = ProviderFrameType.Event, RequestId = Guid.NewGuid().ToString("N"), Operation = operation,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(), Body = body
        });
        _session.CommitSent(frame);
        return frame;
    }

    internal void SkipSequence()
    {
        var skipped = _session.Seal(Identity with
        {
            Type = ProviderFrameType.Goodbye, RequestId = Guid.NewGuid().ToString("N"),
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()
        });
        _session.CommitSent(skipped);
    }

    internal string ProofFor(ProviderFrame frame) => ProviderProtocol.CreateSessionProof(_key, "v2c", frame with { Proof = string.Empty });

    internal Task RespondAsync(ProviderFrame request, string outcome, string dispatchState, string body) =>
        WriteRawAsync(SealResponse(request, outcome, dispatchState, body));

    internal Task EventAsync(string operation, string body) => WriteRawAsync(SealEvent(operation, body));

    internal Task PingAsync()
    {
        var frame = _session.Seal(Identity with
        {
            Type = ProviderFrameType.Ping, RequestId = Guid.NewGuid().ToString("N"),
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()
        });
        _session.CommitSent(frame);
        return WriteRawAsync(frame);
    }

    internal async Task WriteRawAsync(ProviderFrame frame)
    {
        try { await ProviderProtocol.WriteAsync(Stream, frame, ProviderProtocol.MaxResponseBytes, CancellationToken.None); }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _session.Dispose();
    }
}

sealed class Harness : IAsyncDisposable
{
    private readonly Stream _client;

    private Harness(Stream client, ProviderChannel channel, FakeProvider provider)
    {
        _client = client;
        Channel = channel;
        Provider = provider;
    }

    internal ProviderChannel Channel { get; }
    internal FakeProvider Provider { get; }

    internal static async Task<Harness> CreateAsync(byte[] key, string[] implemented, Action<ProviderChannel>? beforeStart = null)
    {
        var name = "provider-validation-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1 << 20, 1 << 20);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));
        var identity = new ProviderFrame
        {
            Type = ProviderFrameType.Ping, RequestId = Guid.NewGuid().ToString("N"), VisualStudioPid = 101,
            VisualStudioStartTicks = 638900000000000000, ServerPid = 202, ServerStartTicks = 638900000000000001,
            ServerInstanceId = "AAAAAAAAAAAAAAAAAAAAAA", ProviderEpoch = "22222222222242228222222222222222"
        };
        var channel = new ProviderChannel(client,
            new ProviderHandshakeResult(identity, new ProviderSession(key, isCompanion: true), implemented), beforeStart);
        return new Harness(client, channel, new FakeProvider(server, key, identity));
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        await Provider.DisposeAsync();
        await _client.DisposeAsync();
    }
}
