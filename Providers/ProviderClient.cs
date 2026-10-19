using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VisualStudioMcpServer.Capabilities;
using VisualStudioMcpServer.Coordination;

namespace VisualStudioMcpServer.Providers;

internal sealed class ProviderBootstrap
{
    public string PipeName { get; init; } = string.Empty;
    public string SecretBase64 { get; init; } = string.Empty;
    public int VisualStudioPid { get; init; }
    public long VisualStudioStartTicks { get; init; }
}

/// <summary>handshake 完了時点の接続情報。session key は <see cref="ProviderSession"/> だけが保持する。</summary>
internal sealed record ProviderHandshakeResult(ProviderFrame Identity, ProviderSession Session,
    IReadOnlyCollection<string> ImplementedOperations);

internal static class ProviderClient
{
    private static readonly JsonSerializerOptions BootstrapJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private static ProviderChannel? _activeChannel;

    /// <summary>現在の provider epoch の channel。切断後は null（同一 server での再接続はしない）。</summary>
    internal static ProviderChannel? ActiveChannel => Volatile.Read(ref _activeChannel);

    /// <summary>channel の読み取り開始前に呼ぶ hook。event の受け口をここで設定し、最初の event を取り逃さない。</summary>
    internal static Action<ProviderChannel>? ChannelAttached { get; set; }

    /// <summary>channel の終端後に呼ぶ hook。待機中の観測を終えるために使う。</summary>
    internal static Action<ProviderChannel>? ChannelDetached { get; set; }

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
                CryptographicOperations.ZeroMemory(secret);
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

    /// <summary>capabilities.probe の間隔。capability の鮮度上限（5 分）より十分短くする。</summary>
    internal static readonly TimeSpan CapabilityProbeInterval = TimeSpan.FromMinutes(2);

    /// <summary>Ready 後の実装済み operation と probe 結果の通知先（capability 文書への反映）。</summary>
    internal static Action<string, IReadOnlyCollection<string>>? ImplementedOperationsObserved { get; set; }
    internal static Action<string, CapabilitiesProbeResponseBody>? CapabilitiesProbed { get; set; }

    internal static async Task RunAsync(ProviderBootstrap bootstrap, string serverInstanceId,
        CancellationToken cancellationToken, Action<ProviderCapabilityObservation>? statusChanged = null)
    {
        var secret = Convert.FromBase64String(bootstrap.SecretBase64);
        ProviderFrame? authenticatedIdentity = null;
        ProviderChannel? channel = null;
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
            ProviderHandshakeResult handshake;
            try
            {
                handshake = await HandshakeAsync(stream, secret, bootstrap.VisualStudioPid, bootstrap.VisualStudioStartTicks,
                    Environment.ProcessId, serverStartTicks, serverInstanceId, TargetMutationOwner.CurrentOwnerFence,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderProtocolMismatchException)
            {
                Report("disconnected", "unsupported", ProviderErrorCodes.ProviderProtocolMismatch);
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }

            authenticatedIdentity = handshake.Identity;
            channel = new ProviderChannel(stream, handshake, ChannelAttached);
            Volatile.Write(ref _activeChannel, channel);
            Report("connected", "supported", "authenticated_provider_channel_connected_feature_probes_unavailable", authenticatedIdentity);

            // quarantine 中は必ず照合する。quarantine でなくても VSIX が照合を実装していれば送り、
            // 前 owner の未照合記録を VSIX 側で解消できるようにする（解除判断は VSIX の記録と公開状態だけで行う）。
            var quarantined = TargetMutationOwner.IsCurrentQuarantined;
            if (quarantined || channel.ImplementedOperations.Contains("ownership.reconcile"))
            {
                var target = new ProviderTarget
                {
                    VisualStudioPid = bootstrap.VisualStudioPid,
                    VisualStudioStartTicks = bootstrap.VisualStudioStartTicks
                };
                var decision = await ProviderOwnershipReconciler.ReconcileAsync(channel, target, null, quarantined).ConfigureAwait(false);
                if (quarantined && decision.Release) TargetMutationOwner.TryReleaseCurrentQuarantine();
            }

            var epoch = authenticatedIdentity.ProviderEpoch ?? string.Empty;
            ImplementedOperationsObserved?.Invoke(epoch, channel.ImplementedOperations.ToArray());
            var probeLoop = channel.ImplementedOperations.Contains("capabilities.probe")
                ? ProbeCapabilitiesAsync(channel, new ProviderTarget
                {
                    VisualStudioPid = bootstrap.VisualStudioPid,
                    VisualStudioStartTicks = bootstrap.VisualStudioStartTicks
                }, epoch)
                : Task.CompletedTask;

            using (cancellationToken.Register(static state => ((ProviderChannel)state!).Terminate(null), channel))
                await channel.Completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (channel.TerminationReason == ProviderChannel.ClosedByPeer)
            {
                Report("disconnected", "unknown", "provider_closed_channel", authenticatedIdentity);
                return;
            }
            throw new InvalidDataException("The provider channel failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Report("stopped", "unknown", "provider_client_stopped", authenticatedIdentity);
            throw;
        }
        catch (ProviderProtocolMismatchException) { throw; }
        catch
        {
            Report("disconnected", "unknown", ProviderErrorCodes.ProviderDisconnected, authenticatedIdentity);
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeChannel, null, channel);
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            if (channel is not null) ChannelDetached?.Invoke(channel);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// channel が開いている間、capabilities.probe を定期的に送り、capability 文書の provider 観測を更新する。
    /// probe の失敗は channel を切断せず、次の周期で再試行する（再送ではなく新しい読み取り要求）。
    /// </summary>
    private static async Task ProbeCapabilitiesAsync(ProviderChannel channel, ProviderTarget target, string epoch)
    {
        while (!channel.Completion.IsCompleted)
        {
            try
            {
                var call = await channel.SendAsync("capabilities.probe", new TargetOnlyRequestBody { Target = target },
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var result = await call.Result.ConfigureAwait(false);
                if (result.IsOk && result.Body is not null)
                    CapabilitiesProbed?.Invoke(epoch, ProviderBodyRules.ParseCapabilitiesProbeResponse(result.Body));
            }
            catch (Exception) when (!channel.Completion.IsCompleted) { /* 次の周期で再試行する。 */ }
            catch (Exception) { return; }
            if (await Task.WhenAny(channel.Completion, Task.Delay(CapabilityProbeInterval)).ConfigureAwait(false) == channel.Completion) return;
        }
    }

    /// <summary>
    /// companion 側の handshake。Challenge の版・catalogHash を検査し、Hello に ownerFence を載せ、Ready の proof を確認して
    /// session key を導出する。bootstrap secret の消去は呼び出し側が行う。
    /// </summary>
    internal static async Task<ProviderHandshakeResult> HandshakeAsync(Stream stream, byte[] secret,
        int visualStudioPid, long visualStudioStartTicks, int serverPid, long serverStartTicks, string serverInstanceId,
        string? ownerFence, CancellationToken cancellationToken)
    {
        var challenge = await ProviderProtocol.ReadAsync(stream, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
        if (challenge.Type != ProviderFrameType.Challenge)
            throw new ProviderProtocolViolationException("The provider handshake did not start with a challenge.");
        if (challenge.CatalogHash != ProviderCatalog.Hash)
            throw new ProviderProtocolMismatchException("The provider catalog hash does not match.");
        if (challenge.VisualStudioPid != visualStudioPid || challenge.VisualStudioStartTicks != visualStudioStartTicks ||
            challenge.ServerPid != serverPid || challenge.ServerStartTicks != serverStartTicks ||
            challenge.ServerInstanceId != serverInstanceId ||
            challenge.DeadlineUnixMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidDataException("The provider challenge identity or deadline is invalid.");

        var hello = challenge with
        {
            Type = ProviderFrameType.Hello,
            Proof = string.Empty,
            Body = ProviderProtocol.SerializeBody(new ProviderOwnerHelloBody
            {
                OwnerFence = ProviderProtocol.IsUuidN(ownerFence) ? ownerFence : null
            })
        };
        hello = hello with { Proof = ProviderProtocol.CreateProof(secret, "client", hello) };
        await ProviderProtocol.WriteAsync(stream, hello, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
        var ready = await ProviderProtocol.ReadAsync(stream, ProviderProtocol.MaxHandshakeBytes, cancellationToken).ConfigureAwait(false);
        if (ready.CatalogHash != ProviderCatalog.Hash)
            throw new ProviderProtocolMismatchException("The provider catalog hash does not match.");
        if (!Matches(ready, challenge, ProviderFrameType.Ready) || !ProviderProtocol.VerifyProof(secret, "server", ready))
            throw new InvalidDataException("The provider host proof is invalid.");

        var sessionKey = ProviderProtocol.DeriveSessionKey(secret, challenge.Nonce, challenge.ProviderEpoch, ProviderCatalog.Hash);
        try
        {
            var identity = challenge with { Nonce = string.Empty, Proof = string.Empty, CatalogHash = string.Empty };
            return new ProviderHandshakeResult(identity, new ProviderSession(sessionKey, isCompanion: true),
                ready.ImplementedOperations.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(sessionKey); }
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

/// <summary>companion 側で確定した 1 要求の結果。channel のエラーは MCP protocol error にしない。</summary>
internal sealed record ProviderCallResult(string RequestId, string Operation, string Outcome, string? Code,
    string DispatchState, string? Body, string? Message)
{
    internal bool IsOk => Outcome == ProviderOutcome.Ok;
}

internal sealed record ProviderPendingCall(string RequestId, Task<ProviderCallResult> Result);

/// <summary>execution 要求の保持記録。timeout/切断後も結果不確定として残し、再送しない。</summary>
internal sealed class ProviderExecutionRecord
{
    internal ProviderExecutionRecord(string requestId, string operationId)
    {
        RequestId = requestId;
        OperationId = operationId;
    }

    internal string RequestId { get; }
    internal string OperationId { get; }
    internal string State { get; set; } = "pending";
    internal string? Code { get; set; }
    internal string DispatchState { get; set; } = ProviderDispatchState.Unknown;
}

internal sealed record ProviderEventNotification(string Operation, object Body, bool HistoryGap);

/// <summary>
/// Ready 後の companion 側 channel。単一 writer と単一 reader loop を持ち、最大 8 件の未完了要求を管理する。
/// 切断は終端であり、保留中・待機中の要求はどの epoch にも再送しない。
/// </summary>
internal sealed class ProviderChannel : IAsyncDisposable
{
    internal const string ClosedByPeer = "closed_by_peer";
    internal const string ProtocolViolation = "protocol_violation";
    internal const string ChannelFailed = "channel_failed";
    internal const string LocalStop = "local_stop";
    private const int MaxExpiredReads = 64;
    private const int MaxRetainedExecutions = 64;

    private readonly Stream _stream;
    private readonly ProviderSession _session;
    private readonly ProviderFrame _identity;
    private readonly HashSet<string> _implementedOperations;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly LinkedList<(string RequestId, string Operation)> _expiredReads = new();
    private readonly Dictionary<string, ProviderExecutionRecord> _executions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _lastModeSequence;
    private bool _modeGapPending;
    private int _terminated;
    private int _violations;
    private int _lateResponses;
    private int _ignoredObservations;

    internal ProviderChannel(Stream stream, ProviderHandshakeResult handshake, Action<ProviderChannel>? beforeStart = null)
    {
        _stream = stream;
        _session = handshake.Session;
        _identity = handshake.Identity;
        _implementedOperations = new HashSet<string>(handshake.ImplementedOperations, StringComparer.Ordinal);
        // 読み取り loop の開始前に受け口を設定させ、Ready 直後の event を取り逃さない。
        beforeStart?.Invoke(this);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>この channel の identity（VS PID/開始時刻、server identity、provider epoch）。</summary>
    internal ProviderFrame Identity => _identity;

    /// <summary>応答待ち超過の猶予。既定は契約どおり 2 秒（検証では短縮できる）。</summary>
    internal static TimeSpan ResponseGrace { get; set; } = TimeSpan.FromSeconds(2);

    internal Task Completion => _completion.Task;
    internal string? TerminationReason { get; private set; }
    internal bool IsTerminated => Volatile.Read(ref _terminated) != 0;
    internal IReadOnlyCollection<string> ImplementedOperations => _implementedOperations;
    internal int Violations => Volatile.Read(ref _violations);
    internal int LateResponses => Volatile.Read(ref _lateResponses);
    internal int IgnoredObservations => Volatile.Read(ref _ignoredObservations);
    internal int OutstandingRequests { get { lock (_sync) return _pending.Count; } }
    internal Action<ProviderEventNotification>? EventReceived { get; set; }
    internal Action<ProviderExecutionRecord>? ExecutionSettled { get; set; }
    internal Action<string>? Log { get; set; }

    internal IReadOnlyList<ProviderExecutionRecord> ExecutionRecords
    {
        get { lock (_sync) return _executions.Values.ToArray(); }
    }

    /// <summary>
    /// 要求を書き込み、結果待ちの task を返す。9 件目は書き込まずに provider_busy で即時に返し、空きを待たない。
    /// </summary>
    internal async Task<ProviderPendingCall> SendAsync<TBody>(string operation, TBody body, TimeSpan timeout)
    {
        var requestId = Guid.NewGuid().ToString("N");
        if (!ProviderCatalog.TryGet(operation, out var entry) || entry.Kind == ProviderOperationKind.Event)
            throw new ArgumentException("The provider operation is not a catalog request.", nameof(operation));
        if (IsTerminated)
            return Immediate(requestId, operation, ProviderErrorCodes.ProviderDisconnected);
        if (!_implementedOperations.Contains(operation))
            return Immediate(requestId, operation, ProviderErrorCodes.UnsupportedOperation);

        var bodyText = ProviderProtocol.SerializeBody(body);
        ProviderTarget? target;
        try
        {
            if (!ProviderBodyRules.TryValidateRequest(operation, bodyText, out target))
                return Immediate(requestId, operation, ProviderErrorCodes.InvalidRequest);
        }
        catch (ProviderProtocolViolationException) { return Immediate(requestId, operation, ProviderErrorCodes.InvalidRequest); }
        if (target!.VisualStudioPid != _identity.VisualStudioPid || target.VisualStudioStartTicks != _identity.VisualStudioStartTicks)
            return Immediate(requestId, operation, ProviderErrorCodes.TargetMismatch);

        var maxDeadline = TimeSpan.FromMilliseconds(entry.MaxDeadlineMilliseconds);
        if (timeout <= TimeSpan.Zero || timeout > maxDeadline) timeout = maxDeadline;
        var deadline = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds();
        var pending = new Pending(requestId, operation, entry.Kind);
        lock (_sync)
        {
            if (_pending.Count >= ProviderProtocol.MaxQueuedRequests)
                return Immediate(requestId, operation, ProviderErrorCodes.ProviderBusy);
            if (entry.Kind == ProviderOperationKind.Execution)
            {
                pending.Record = new ProviderExecutionRecord(requestId,
                    ProviderProtocol.ParseBody<ExecutionDispatchRequestBody>(bodyText).OperationId);
                _executions[requestId] = pending.Record;
                if (_executions.Count > MaxRetainedExecutions)
                {
                    // 確定済みの古い記録だけを捨てる。未確定の記録は保持し続ける。
                    var settled = _executions.FirstOrDefault(static item => item.Value.State is not ("pending" or "uncertain"));
                    if (settled.Key is not null) _executions.Remove(settled.Key);
                }
            }
            _pending[requestId] = pending;
        }

        var frame = _identity with
        {
            Type = ProviderFrameType.Request,
            RequestId = requestId,
            Operation = operation,
            DeadlineUnixMilliseconds = deadline,
            Body = bodyText
        };
        // 書き込みが詰まっても期限で確定できるよう、書き込み前に応答待ちの timer を開始する。
        var due = DateTimeOffset.FromUnixTimeMilliseconds(deadline) - DateTimeOffset.UtcNow + ResponseGrace;
        _ = Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, _lifetime.Token)
            .ContinueWith(_ => OnResponseTimeout(requestId), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        bool written;
        try { written = await WriteFrameAsync(frame, entry.MaxRequestBytes).ConfigureAwait(false); }
        catch (InvalidDataException)
        {
            // 送信前の上限超過。書き込んでいないので dispatch はない。
            lock (_sync)
            {
                _pending.Remove(requestId);
                _executions.Remove(requestId);
            }
            return Immediate(requestId, operation, ProviderErrorCodes.InvalidRequest);
        }
        if (!written)
        {
            // 書き込み中に channel が終端した。Terminate が pending を確定済み。
            return new ProviderPendingCall(requestId, pending.Completion.Task);
        }
        Log?.Invoke($"provider request sent operation={operation} requestId={requestId} direction=c2v bytes={bodyText.Length}");
        return new ProviderPendingCall(requestId, pending.Completion.Task);
    }

    /// <summary>未 dispatch の要求だけに効く取消を送る。dispatch 済みなら VSIX が無視し、元の応答が届く。</summary>
    internal async Task<bool> CancelAsync(string requestId)
    {
        lock (_sync)
            if (!_pending.TryGetValue(requestId, out var pending) || pending.TimedOut) return false;
        var frame = _identity with
        {
            Type = ProviderFrameType.Cancel,
            RequestId = Guid.NewGuid().ToString("N"),
            CorrelationId = requestId,
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()
        };
        return await WriteFrameAsync(frame, ProviderProtocol.MaxRequestBytes).ConfigureAwait(false);
    }

    internal async Task SendGoodbyeAsync()
    {
        var frame = _identity with
        {
            Type = ProviderFrameType.Goodbye,
            RequestId = Guid.NewGuid().ToString("N"),
            DeadlineUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds()
        };
        await WriteFrameAsync(frame, ProviderProtocol.MaxRequestBytes).ConfigureAwait(false);
        Terminate(LocalStop);
    }

    /// <summary>
    /// channel を終端する。保留中の read は provider_disconnected、execution は result_unknown とし、記録を残す。
    /// </summary>
    internal void Terminate(string? reason)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        TerminationReason = reason ?? LocalStop;
        if (reason == ProtocolViolation) Interlocked.Increment(ref _violations);
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (IOException) { }
        Pending[] pending;
        lock (_sync)
        {
            pending = _pending.Values.ToArray();
            _pending.Clear();
            _expiredReads.Clear();
        }
        foreach (var item in pending)
        {
            if (item.Kind == ProviderOperationKind.Execution)
            {
                item.Record!.State = "uncertain";
                item.Record.Code = ProviderErrorCodes.ResultUnknown;
                item.Completion.TrySetResult(new ProviderCallResult(item.RequestId, item.Operation, ProviderOutcome.Error,
                    ProviderErrorCodes.ResultUnknown, ProviderDispatchState.Unknown, null, null));
            }
            else
            {
                item.Completion.TrySetResult(new ProviderCallResult(item.RequestId, item.Operation, ProviderOutcome.Error,
                    ProviderErrorCodes.ProviderDisconnected, ProviderDispatchState.Unknown, null, null));
            }
        }
        Log?.Invoke($"provider channel terminated reason={TerminationReason} pending={pending.Length}");
        _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Terminate(null);
        await _completion.Task.ConfigureAwait(false);
        _session.Dispose();
    }

    private ProviderPendingCall Immediate(string requestId, string operation, string code) =>
        new(requestId, Task.FromResult(new ProviderCallResult(requestId, operation, ProviderOutcome.Error, code,
            ProviderDispatchState.NotDispatched, null, null)));

    private async Task<bool> WriteFrameAsync(ProviderFrame frame, int maximumBytes)
    {
        try { await _writeLock.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (IsTerminated) return false;
            // sequence は書き込み順に割り当てる。上限超過は sequence を消費する前に検出する。
            var sealedFrame = _session.Seal(frame);
            var payload = ProviderProtocol.Serialize(sealedFrame, maximumBytes);
            _session.CommitSent(sealedFrame);
            await ProviderProtocol.WritePayloadAsync(_stream, payload, _lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Terminate(ChannelFailed);
            return false;
        }
        finally { _writeLock.Release(); }
    }

    private void OnResponseTimeout(string requestId)
    {
        Pending? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out pending) || pending.TimedOut) return;
            if (pending.Kind == ProviderOperationKind.Execution)
            {
                // execution は request ID を保留したまま結果不確定として記録を残す。
                pending.TimedOut = true;
                pending.Record!.State = "uncertain";
                pending.Record.Code = ProviderErrorCodes.ResultUnknown;
            }
            else
            {
                _pending.Remove(requestId);
                _expiredReads.AddLast((requestId, pending.Operation));
                if (_expiredReads.Count > MaxExpiredReads) _expiredReads.RemoveFirst();
            }
        }
        var code = pending.Kind == ProviderOperationKind.Execution ? ProviderErrorCodes.ResultUnknown : ProviderErrorCodes.ProviderTimeout;
        pending.Completion.TrySetResult(new ProviderCallResult(requestId, pending.Operation, ProviderOutcome.Error, code,
            ProviderDispatchState.Unknown, null, null));
        Log?.Invoke($"provider request timed out operation={pending.Operation} requestId={requestId} code={code}");
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!IsTerminated)
            {
                var (frame, length) = await ProviderProtocol.ReadWithLengthAsync(_stream, ProviderProtocol.MaxResponseBytes,
                    _lifetime.Token).ConfigureAwait(false);
                if (frame.VisualStudioPid != _identity.VisualStudioPid ||
                    frame.VisualStudioStartTicks != _identity.VisualStudioStartTicks || frame.ServerPid != _identity.ServerPid ||
                    frame.ServerStartTicks != _identity.ServerStartTicks || frame.ServerInstanceId != _identity.ServerInstanceId ||
                    frame.ProviderEpoch != _identity.ProviderEpoch)
                    throw new ProviderProtocolViolationException("The provider frame identity does not match the channel.");
                _session.Open(frame);
                switch (frame.Type)
                {
                    case ProviderFrameType.Ping:
                        if (length > ProviderProtocol.MaxRequestBytes)
                            throw new ProviderProtocolViolationException("The provider ping exceeds its limit.");
                        await WriteFrameAsync(frame with { Type = ProviderFrameType.Pong, Proof = string.Empty },
                            ProviderProtocol.MaxRequestBytes).ConfigureAwait(false);
                        break;
                    case ProviderFrameType.Goodbye:
                        Terminate(ClosedByPeer);
                        return;
                    case ProviderFrameType.Event:
                        if (length > ProviderCatalog.MaxEventBytes)
                            throw new ProviderProtocolViolationException("The provider event exceeds its limit.");
                        HandleEvent(frame);
                        break;
                    case ProviderFrameType.Response:
                        HandleResponse(frame, length);
                        break;
                    default:
                        throw new ProviderProtocolViolationException("The provider frame type is not valid here.");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ProviderProtocolException)
        {
            // 破損した stream は再同期せず、以後何も書かずに閉じる。
            Terminate(ProtocolViolation);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Terminate(ChannelFailed);
        }
    }

    private void HandleResponse(ProviderFrame frame, int length)
    {
        ProviderCatalog.TryGet(frame.Operation, out var entry);
        if (length > entry.MaxResponseBytes)
            throw new ProviderProtocolViolationException("The provider response exceeds its catalog limit.");
        Pending? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(frame.CorrelationId, out pending))
            {
                var expired = _expiredReads.First;
                while (expired is not null && expired.Value.RequestId != frame.CorrelationId) expired = expired.Next;
                if (expired is null)
                    throw new ProviderProtocolViolationException("The provider response does not answer a pending request.");
                if (expired.Value.Operation != frame.Operation)
                    throw new ProviderProtocolViolationException("The provider response operation does not match its request.");
                // timeout 済み read への遅延応答は破棄する。
                _expiredReads.Remove(expired);
                Interlocked.Increment(ref _lateResponses);
                Log?.Invoke($"provider late read response discarded operation={frame.Operation} requestId={frame.CorrelationId}");
                return;
            }
            if (pending.Operation != frame.Operation)
                throw new ProviderProtocolViolationException("The provider response operation does not match its request.");
        }

        var result = ParseResult(frame);
        lock (_sync) _pending.Remove(frame.CorrelationId);
        if (pending.Record is not null)
        {
            pending.Record.State = result.IsOk ? "accepted" : (result.DispatchState == ProviderDispatchState.NotDispatched ? "failed" : "uncertain");
            pending.Record.Code = result.Code;
            pending.Record.DispatchState = result.DispatchState;
        }
        if (pending.TimedOut)
        {
            // timeout 済み execution への遅延応答は同じ記録だけを確定し、再送しない。
            Interlocked.Increment(ref _lateResponses);
            ExecutionSettled?.Invoke(pending.Record!);
        }
        else
        {
            pending.Completion.TrySetResult(result);
        }
        Log?.Invoke($"provider response operation={frame.Operation} requestId={frame.CorrelationId} direction=v2c outcome={frame.Outcome} code={result.Code} dispatchState={frame.DispatchState} bytes={length}");
    }

    private static ProviderCallResult ParseResult(ProviderFrame frame)
    {
        if (frame.Outcome == ProviderOutcome.Ok)
        {
            if (frame.DispatchState != ProviderDispatchState.Dispatched)
                throw new ProviderProtocolViolationException("A successful provider response must be dispatched.");
            if (frame.Operation == "ownership.reconcile") ProviderBodyRules.ParseReconcileResponse(frame.Body);
            else if (frame.Operation == "capabilities.probe") ProviderBodyRules.ParseCapabilitiesProbeResponse(frame.Body);
            else ProviderProtocol.ValidateObjectBody(frame.Body);
            return new ProviderCallResult(frame.CorrelationId, frame.Operation, ProviderOutcome.Ok, null,
                frame.DispatchState, frame.Body, null);
        }
        var error = ProviderBodyRules.ParseError(frame.Body);
        return new ProviderCallResult(frame.CorrelationId, frame.Operation, ProviderOutcome.Error, error.Code,
            frame.DispatchState, null, error.Message);
    }

    private void HandleEvent(ProviderFrame frame)
    {
        var body = ProviderBodyRules.ParseEvent(frame.Operation, frame.Body);
        var gap = false;
        switch (body)
        {
            case DebuggerModeChangedEventBody mode:
                lock (_sync)
                {
                    // providerSequence の欠落は違反ではなく履歴 gap として記録する。
                    if (mode.ProviderSequence <= _lastModeSequence)
                        throw new ProviderProtocolViolationException("The provider mode sequence moved backwards.");
                    gap = _modeGapPending || mode.ProviderSequence != _lastModeSequence + 1;
                    _modeGapPending = false;
                    _lastModeSequence = mode.ProviderSequence;
                }
                break;
            case ProviderEventsDroppedEventBody:
                lock (_sync) _modeGapPending = true;
                gap = true;
                break;
            case ExecutionObservedEventBody observed:
            {
                ProviderExecutionRecord? record;
                lock (_sync) record = _executions.Values.FirstOrDefault(item => item.OperationId == observed.OperationId);
                if (record is null)
                {
                    // 期限切れの可能性があるため、未知の operation ID は数えるだけにする。
                    Interlocked.Increment(ref _ignoredObservations);
                    return;
                }
                record.State = observed.State;
                record.Code = observed.Code;
                ExecutionSettled?.Invoke(record);
                break;
            }
        }
        Log?.Invoke($"provider event operation={frame.Operation} direction=v2c gap={gap}");
        EventReceived?.Invoke(new ProviderEventNotification(frame.Operation, body, gap));
    }

    private sealed class Pending(string requestId, string operation, ProviderOperationKind kind)
    {
        internal string RequestId { get; } = requestId;
        internal string Operation { get; } = operation;
        internal ProviderOperationKind Kind { get; } = kind;
        internal TaskCompletionSource<ProviderCallResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ProviderExecutionRecord? Record { get; set; }
        internal bool TimedOut { get; set; }
    }
}

internal sealed record ProviderReconcileDecision(bool Release, string Reason);

/// <summary>abandoned owner の quarantine 解除判断。条件をすべて満たす場合だけ解除し、timer/retry/再接続では解除しない。</summary>
internal static class ProviderOwnershipReconciler
{
    internal static async Task<ProviderReconcileDecision> ReconcileAsync(ProviderChannel channel, ProviderTarget target,
        string? previousServerInstanceId, bool quarantined = false)
    {
        var call = await channel.SendAsync("ownership.reconcile", new OwnershipReconcileRequestBody
        {
            Target = target,
            PreviousServerInstanceId = previousServerInstanceId,
            Quarantined = quarantined
        }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return Evaluate(await call.Result.ConfigureAwait(false));
    }

    internal static ProviderReconcileDecision Evaluate(ProviderCallResult result)
    {
        if (!result.IsOk || result.Body is null) return new ProviderReconcileDecision(false, "user_action_required");
        OwnershipReconcileResponseBody response;
        try { response = ProviderBodyRules.ParseReconcileResponse(result.Body); }
        catch (ProviderProtocolException) { return new ProviderReconcileDecision(false, "user_action_required"); }
        var release = response.QuarantineReleasable && response.UnsettledOperations.Length == 0 &&
            response.PreviousOwnerExited && !response.BuildBusy && response.DebuggerState is "design" or "break";
        return new ProviderReconcileDecision(release, release ? "reconciled" : "user_action_required");
    }
}
