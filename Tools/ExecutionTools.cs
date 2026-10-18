using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using VisualStudioMcpServer.Capabilities;
using VisualStudioMcpServer.Execution;
using VisualStudioMcpServer.Observation;
using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// debugger 状態・観測・起動停止の tool。HTTP は明示登録、stdio は assembly 走査で同じ定義を一度だけ公開する。
/// </summary>
[McpServerToolType]
public sealed class ExecutionHttpTools
{
    /// <summary>新しい debugger 遷移を読み取り専用で待ちます。</summary>
    [McpServerTool]
    [Description("Waits read-only for a fresh debugger transition after afterSequence. Bind exactly one of expectedDebugSessionId or operationId. Never pauses, resumes, or evaluates. Requires the VSIX provider.")]
    public static Task<string> WaitForDebuggerState(
        [Description("Desired state: running, break, design, or any_change.")] string desiredState,
        [Description("EventSequence from a fresh get_debugger_state or launch response; only later transitions qualify.")] long afterSequence,
        [Description("Debugger session ID to bind. Supply exactly one of expectedDebugSessionId or operationId.")] string? expectedDebugSessionId = null,
        [Description("Accepted start_debugging operation ID to bind before its session ID is known.")] string? operationId = null,
        [Description("Observation timeout in milliseconds, clamped to 1000-30000. Defaults to 10000.")] int timeoutMilliseconds = 10_000,
        CancellationToken cancellationToken = default) =>
        DebuggerObservationTools.WaitForDebuggerStateAsync(desiredState, afterSequence, expectedDebugSessionId, operationId,
            timeoutMilliseconds, cancellationToken);

    /// <summary>bound IDE の debugger 状態を返します。</summary>
    [McpServerTool]
    [Description("Gets debugger state only from an explicitly bound Visual Studio process and matching absolute solution path. With the VSIX provider, JSON adds session, process-set, startup snapshot and EventSequence fields; operationId returns only that retained launch/stop operation.")]
    public static Task<string> GetDebuggerState(
        [Description("Output format: 'text' for human-readable or 'json' for structured data. Defaults to 'text'.")]
        string format = "text",
        [Description("Required absolute path of the solution expected in the bound Visual Studio instance.")]
        string? expectedSolutionPath = null,
        [Description("Optional operation ID from start_debugging, start_without_debugging, or stop_debugging. Unknown IDs are never replaced by another operation.")]
        string? operationId = null,
        CancellationToken cancellationToken = default) =>
        ExecutionToolImplementation.GetDebuggerStateAsync(format, expectedSolutionPath, operationId, cancellationToken);

    /// <summary>design mode から単一 startup project を debug 起動します。</summary>
    [McpServerTool]
    [Description("Starts debugging the single supported startup project from design mode only, through the guarded VSIX provider. Requires allowDebuggerControl=true, allowBuild=true when a build may occur, a client requestId, and the startup snapshot ID from a fresh get_debugger_state. Paused sessions are never continued; use control_debugger.")]
    public static Task<string> StartDebugging(
        [Description("Absolute path of the solution expected in the bound Visual Studio instance.")] string expectedSolutionPath,
        [Description("StartupSnapshot.SnapshotId from a fresh get_debugger_state(format='json').")] string expectedStartupSnapshotId,
        [Description("Client-issued idempotency key: 1-128 printable ASCII characters. Repeat it to query the same operation; never reuse it for a different request.")] string requestId,
        [Description("Must be true to start a debugger session.")] bool allowDebuggerControl = false,
        [Description("Must be true when the launch may build. The provider currently reports that a build may occur.")] bool allowBuild = false,
        [Description("Caller wait in milliseconds (1-30000). A shorter wait returns execution_pending without cancelling the operation.")] int timeoutMilliseconds = 10_000,
        CancellationToken cancellationToken = default) =>
        ExecutionToolImplementation.ExecuteAsync(ExecutionAction.StartDebugging, requestId, expectedSolutionPath, expectedStartupSnapshotId,
            null, null, allowDebuggerControl, false, allowBuild, false, timeoutMilliseconds, cancellationToken);

    /// <summary>design mode から単一 startup project を debug なしで起動します。</summary>
    [McpServerTool]
    [Description("Starts the single supported startup project without debugging from design mode, through the guarded VSIX provider (DebugLaunch with DBGLAUNCH_NoDebug). Requires allowLaunch=true (allowDebuggerControl does not authorize it), allowBuild=true when a build may occur, a client requestId, and a fresh startup snapshot ID. No debugger session is created and the launched process is not tracked.")]
    public static Task<string> StartWithoutDebugging(
        [Description("Absolute path of the solution expected in the bound Visual Studio instance.")] string expectedSolutionPath,
        [Description("StartupSnapshot.SnapshotId from a fresh get_debugger_state(format='json').")] string expectedStartupSnapshotId,
        [Description("Client-issued idempotency key: 1-128 printable ASCII characters. Repeat it to query the same operation; never reuse it for a different request.")] string requestId,
        [Description("Must be true to launch without debugging.")] bool allowLaunch = false,
        [Description("Must be true when the launch may build. The provider currently reports that a build may occur.")] bool allowBuild = false,
        [Description("Caller wait in milliseconds (1-30000). A shorter wait returns execution_pending without cancelling the operation.")] int timeoutMilliseconds = 10_000,
        CancellationToken cancellationToken = default) =>
        ExecutionToolImplementation.ExecuteAsync(ExecutionAction.StartWithoutDebugging, requestId, expectedSolutionPath, expectedStartupSnapshotId,
            null, null, false, allowLaunch, allowBuild, false, timeoutMilliseconds, cancellationToken);

    /// <summary>指定した debugger session と process set だけを停止します。</summary>
    [McpServerTool]
    [Description("Stops the exact current debugger session through the guarded VSIX provider (Debugger.Stop). Requires allowDebuggerControl=true, the DebugSessionId and ProcessSetSnapshotId from a fresh get_debugger_state, a client requestId, and allowStopExistingSession=true for a session this server did not launch. Reports debugger end separately from process exit; it does not stop the MCP server or applications started without debugging.")]
    public static Task<string> StopDebugging(
        [Description("Absolute path of the solution expected in the bound Visual Studio instance.")] string expectedSolutionPath,
        [Description("DebugSessionId from a fresh get_debugger_state(format='json').")] string expectedDebugSessionId,
        [Description("ProcessSetSnapshotId from the same get_debugger_state response.")] string expectedProcessSetSnapshotId,
        [Description("Client-issued idempotency key: 1-128 printable ASCII characters. Repeat it to query the same operation; never reuse it for a different request.")] string requestId,
        [Description("Must be true to stop a debugger session.")] bool allowDebuggerControl = false,
        [Description("Must be true to stop a session that this server did not launch.")] bool allowStopExistingSession = false,
        [Description("Caller wait in milliseconds (1-30000). A shorter wait returns execution_pending without cancelling the operation.")] int timeoutMilliseconds = 10_000,
        CancellationToken cancellationToken = default) =>
        ExecutionToolImplementation.ExecuteAsync(ExecutionAction.StopDebugging, requestId, expectedSolutionPath, null,
            expectedDebugSessionId, expectedProcessSetSnapshotId, allowDebuggerControl, false, false, allowStopExistingSession,
            timeoutMilliseconds, cancellationToken);
}

/// <summary>起動・停止 tool の応答。coordinator の結果に、待機に使う公開 sequence と provider epoch を加える。</summary>
internal sealed record ExecutionToolResponse(
    bool Success,
    string Code,
    string Message,
    string? OperationId,
    string State,
    bool MayHaveChanged,
    int? TargetProcessId,
    string? ServerInstanceId,
    Guid? ServerEpoch,
    string? DebugSessionId,
    string? ProcessSetSnapshotId,
    string Action,
    string RequestId,
    long? EventSequence,
    string? ProviderEpoch);

internal sealed record DebuggerWaitResult(
    bool Success,
    string Code,
    string Message,
    int? TargetProcessId,
    string? ServerInstanceId,
    string? ProviderEpoch,
    string DesiredState,
    long AfterSequence,
    long? EventSequence,
    string? State,
    string? DebugSessionId,
    string? LaunchOperationId,
    DateTimeOffset? ObservedAtUtc,
    long? OldestAvailableSequence,
    long? CurrentSequence,
    string StopReasonAvailability = "unsupported");

/// <summary>wait_for_debugger_state の実装。workspace lease・UI thread・COM dispatcher を占有しない。</summary>
internal static class DebuggerObservationTools
{
    internal const int MinimumWaitMilliseconds = 1_000;
    internal const int MaximumWaitMilliseconds = 30_000;

    internal static async Task<string> WaitForDebuggerStateAsync(string desiredState, long afterSequence,
        string? expectedDebugSessionId, string? operationId, int timeoutMilliseconds, CancellationToken cancellationToken,
        DebuggerObservationStore? storeOverride = null, int? targetProcessIdOverride = null)
    {
        var normalized = (desiredState ?? string.Empty).Trim().ToLowerInvariant();
        int? targetPid = targetProcessIdOverride;
        if (targetPid is null && VisualStudioConnector.TryGetTargetProcessIdentity(out var pid, out _)) targetPid = pid;
        DebuggerWaitResult Fail(string code, string message, DebuggerObservationStore? store = null, long? oldest = null) =>
            new(false, code, message, targetPid, CapabilityRuntimeContext.ServerInstanceId, store?.ProviderEpoch, normalized,
                afterSequence, null, null, expectedDebugSessionId, operationId, null, oldest, store?.CurrentSequence);

        if (targetPid is null)
            return Serialize(Fail("target_identity_required", "Bind to the invoking Visual Studio process before waiting for debugger state."));
        DebuggerWaitDesiredState desired;
        switch (normalized)
        {
            case "running": desired = DebuggerWaitDesiredState.Running; break;
            case "break": desired = DebuggerWaitDesiredState.Break; break;
            case "design": desired = DebuggerWaitDesiredState.Design; break;
            case "any_change": desired = DebuggerWaitDesiredState.AnyChange; break;
            default: return Serialize(Fail("invalid_argument", "desiredState must be running, break, design, or any_change."));
        }
        if ((expectedDebugSessionId is null) == (operationId is null))
            return Serialize(Fail("invalid_argument", "Supply exactly one of expectedDebugSessionId or operationId."));
        if (expectedDebugSessionId is not null && !ProviderProtocol.IsUuidN(expectedDebugSessionId) ||
            operationId is not null && !ProviderProtocol.IsUuidN(operationId))
            return Serialize(Fail("invalid_argument", "Debugger session and operation IDs are lowercase 32-character UUID values."));
        if (afterSequence < 0)
            return Serialize(Fail("invalid_cursor", "afterSequence must be a non-negative sequence from a fresh response."));
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, MinimumWaitMilliseconds, MaximumWaitMilliseconds));

        var store = storeOverride ?? ProviderRuntime.ObservationStore;
        if (store is null)
            return Serialize(Fail("requires_vsix_provider", "Debugger transition observation requires the authenticated VSIX provider."));

        var result = await store.WaitAsync(desired, afterSequence, expectedDebugSessionId, operationId, timeout, cancellationToken)
            .ConfigureAwait(false);
        var observation = result.Observation;
        var (success, code, message) = result.Status switch
        {
            DebuggerObservationWaitStatus.Matched => (true, "ok", "A fresh matching debugger transition was observed."),
            DebuggerObservationWaitStatus.EventHistoryExpired => (false, "event_history_expired",
                "Transitions after afterSequence are no longer fully retained; read get_debugger_state again."),
            DebuggerObservationWaitStatus.SessionChanged => (false, "session_changed",
                "A different debugger session replaced the requested session."),
            DebuggerObservationWaitStatus.InvalidCursor => (false, "invalid_cursor",
                "afterSequence is ahead of the current provider sequence."),
            DebuggerObservationWaitStatus.WaiterLimit => (false, "waiter_capacity_exceeded",
                "The bounded set of concurrent debugger waiters is full."),
            DebuggerObservationWaitStatus.TimedOut => (false, "observation_timeout",
                "No matching transition was observed before the observation timeout; the program was not affected."),
            DebuggerObservationWaitStatus.Cancelled => (false, "cancelled", "The caller cancelled the wait; the program was not affected."),
            _ => (false, "provider_disconnected", "The provider channel closed; waits cannot complete on this server instance.")
        };
        return Serialize(new DebuggerWaitResult(success, code, message, targetPid, CapabilityRuntimeContext.ServerInstanceId,
            store.ProviderEpoch, normalized, afterSequence, observation?.Sequence, observation is null ? null : StateName(observation.State),
            observation?.DebugSessionId ?? expectedDebugSessionId, observation?.LaunchOperationId ?? operationId,
            observation?.ObservedAtUtc, result.OldestAvailableSequence, store.CurrentSequence));
    }

    internal static string StateName(DebuggerObservedState state) => state switch
    {
        DebuggerObservedState.Running => "running",
        DebuggerObservedState.Break => "break",
        DebuggerObservedState.Design => "design",
        _ => "unknown"
    };

    // 各 field は構築時に上限があるため、直列化した JSON を切り詰めない。
    private static string Serialize(DebuggerWaitResult result) => JsonConvert.SerializeObject(result, Formatting.None);
}

/// <summary>
/// get_debugger_state の共通実装。既存の DTE 読み取り（Mode/State などの既存 field）を保ち、
/// provider があれば debugger.getState の session・process set・startup snapshot を追加 field として返す。
/// </summary>
internal static class ExecutionToolImplementation
{
    private static readonly TimeSpan ProviderReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SequenceCatchUp = TimeSpan.FromSeconds(1);

    /// <summary>
    /// HTTP と stdio の起動・停止をすべて同じ coordinator へ渡す。束縛のない呼び出し、許可のない呼び出し、provider のない呼び出しは
    /// dispatch 前に拒否される。応答の EventSequence は dispatch 前の観測位置で、wait_for_debugger_state の afterSequence に使える。
    /// </summary>
    internal static async Task<string> ExecuteAsync(ExecutionAction action, string? requestId, string? expectedSolutionPath,
        string? expectedStartupSnapshotId, string? expectedDebugSessionId, string? expectedProcessSetSnapshotId,
        bool allowDebuggerControl, bool allowLaunch, bool allowBuild, bool allowStopExistingSession, int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var target = ExecutionRuntime.Target();
        var store = ProviderRuntime.ObservationStore;
        long? eventSequence = store?.CurrentSequence;
        var request = new ExecutionRequest(requestId ?? string.Empty, action, target?.ProcessId ?? 0, target?.StartTicks ?? 0,
            CapabilityRuntimeContext.ServerInstanceId ?? string.Empty, CapabilityRuntimeContext.ServerEpoch ?? Guid.Empty,
            expectedSolutionPath ?? string.Empty, Blank(expectedStartupSnapshotId), Blank(expectedDebugSessionId),
            Blank(expectedProcessSetSnapshotId), allowDebuggerControl, allowLaunch, allowBuild, allowStopExistingSession,
            CallerWaitTimeoutMilliseconds: timeoutMilliseconds);
        var result = await ExecutionRuntime.Coordinator.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new ExecutionToolResponse(result.Success, result.Code, result.Message, result.OperationId, result.State,
            result.MayHaveChanged, result.TargetProcessId, result.ServerInstanceId, result.ServerEpoch, result.DebugSessionId,
            result.ProcessSetSnapshotId, ProviderExecutionAdapter.ActionName(action), Bound(requestId), eventSequence, store?.ProviderEpoch);
        return JsonConvert.SerializeObject(response, Formatting.None);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Bound(string? value) => value is null ? string.Empty : value.Length <= 128 ? value : value[..128];

    internal static async Task<string> GetDebuggerStateAsync(string format, string? expectedSolutionPath, string? operationId,
        CancellationToken cancellationToken)
    {
        DebuggerState state;
        try { state = StaComDispatcher.Invoke(() => VisualStudioConnector.GetDebuggerState(expectedSolutionPath), TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { state = DebuggerState.Unavailable("com_timeout", "The bound Visual Studio debugger state did not respond within the inspection deadline."); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            state = DebuggerState.Unavailable("com_error", $"Visual Studio rejected debugger inspection (HRESULT 0x{ex.HResult:X8}).");
        }
        catch { state = DebuggerState.Unavailable("target_unavailable", "The bound Visual Studio debugger state is unavailable."); }

        if (state.Code == "ok") await EnrichFromProviderAsync(state, expectedSolutionPath!, cancellationToken).ConfigureAwait(false);
        else state.ProviderStateReason = "debugger_state_unavailable";
        if (operationId is not null) state.Operation = QueryOperation(operationId);

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) return SerializeBounded(state);
        return state.Message.Length <= 4096 ? state.Message : state.Message[..4096];
    }

    /// <summary>保持中の operation だけを照会する。不明・期限切れ・別世代の ID は operation_not_found のまま返す。</summary>
    internal static ExecutionOperationResult QueryOperation(string operationId)
    {
        var target = ExecutionRuntime.Target();
        var serverInstanceId = CapabilityRuntimeContext.ServerInstanceId;
        var serverEpoch = CapabilityRuntimeContext.ServerEpoch;
        if (!ProviderProtocol.IsUuidN(operationId))
            return new ExecutionOperationResult(false, "invalid_argument", "operationId must be a lowercase 32-character UUID value.",
                null, "not_started", false, target?.ProcessId, serverInstanceId, serverEpoch, null, null);
        if (target is null || serverInstanceId is null || serverEpoch is null)
            return new ExecutionOperationResult(false, "operation_not_found", "The operation is unknown for this server generation.",
                null, "not_started", false, target?.ProcessId, serverInstanceId, serverEpoch, null, null);
        return ExecutionRuntime.Coordinator.Query(operationId, target.Value.ProcessId, target.Value.StartTicks, serverInstanceId,
            serverEpoch.Value);
    }

    private static async Task EnrichFromProviderAsync(DebuggerState state, string expectedSolutionPath, CancellationToken cancellationToken)
    {
        var channel = ProviderRuntime.Channel;
        var store = ProviderRuntime.ObservationStore;
        var target = ExecutionRuntime.Target();
        if (channel is null || channel.IsTerminated || target is null)
        {
            state.ProviderStateAvailability = "requires_vsix_provider";
            state.ProviderStateReason = "provider_channel_unavailable";
            return;
        }
        state.ProviderEpoch = channel.Identity.ProviderEpoch;
        if (!channel.ImplementedOperations.Contains("debugger.getState"))
        {
            state.ProviderStateAvailability = "unsupported";
            state.ProviderStateReason = ProviderErrorCodes.UnsupportedOperation;
            return;
        }
        var call = await channel.SendAsync("debugger.getState", new DebuggerGetStateRequestBody
        {
            Target = new ProviderTarget { VisualStudioPid = target.Value.ProcessId, VisualStudioStartTicks = target.Value.StartTicks },
            IncludeStartupSnapshot = true
        }, ProviderReadTimeout).ConfigureAwait(false);
        var result = await call.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsOk)
        {
            state.ProviderStateAvailability = "unknown";
            state.ProviderStateReason = result.Code;
            return;
        }
        DebuggerGetStateResponseBody body;
        try { body = ProviderExecutionBodies.ParseGetState(result.Body!); }
        catch (ProviderProtocolException)
        {
            state.ProviderStateAvailability = "unknown";
            state.ProviderStateReason = "invalid_provider_response";
            return;
        }
        if (body.SolutionPath is null || !SamePath(body.SolutionPath, expectedSolutionPath))
        {
            // DTE の読み取り後に solution が変わった。古い snapshot を混ぜずに状態不明として返す。
            state.ProviderStateAvailability = "unknown";
            state.ProviderStateReason = "solution_mismatch";
            return;
        }

        state.ProviderStateAvailability = "supported";
        state.ProviderStateReason = null;
        state.ProviderSequence = body.ProviderSequence;
        state.DebugSessionId = body.DebugSessionId;
        state.LaunchOperationId = body.LaunchOperationId;
        state.SessionAgentOwned = body.DebugSessionId is null ? null : body.SessionAgentOwned;
        state.ProcessSetSnapshotId = body.ProcessSetSnapshotId;
        state.Processes = body.Processes.Select(static process => new DebuggedProcessInfo
        {
            ProcessId = process.ProcessId, StartTicks = process.StartTicks, Name = process.Name
        }).ToList();
        state.ProcessesTruncated = body.ProcessesTruncated;
        state.CurrentThreadAvailable = body.CurrentThreadAvailable;
        state.Configuration = body.Configuration;
        state.Platform = body.Platform;
        if (body.StartupSnapshot is { } startup)
        {
            state.StartupSnapshot = new StartupSnapshotInfo
            {
                SnapshotId = startup.SnapshotId,
                StartupProjects = startup.StartupProjects.Select(static project => new StartupProjectInfo
                {
                    UniqueName = project.UniqueName, ProviderKind = project.ProviderKind
                }).ToList(),
                ProviderKind = startup.ProviderKind,
                ProfileStatus = startup.ProfileStatus,
                LaunchSupported = startup.LaunchSupported,
                UnsupportedCode = startup.UnsupportedCode,
                UnsupportedReason = startup.UnsupportedReason,
                BuildMayOccur = startup.BuildMayOccur
            };
        }
        if (store is not null && store.ProviderEpoch == channel.Identity.ProviderEpoch)
        {
            // 状態に反映済みの mode event まで観測 store が追いつくのを短時間だけ待ち、同じ時点の公開 sequence を返す。
            await store.WaitForProviderSequenceAsync(body.ProviderSequence, SequenceCatchUp, cancellationToken).ConfigureAwait(false);
            if (store.TryGetSequenceForProviderSequence(body.ProviderSequence, out var sequence)) state.EventSequence = sequence;
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string SerializeBounded(DebuggerState state)
    {
        const int maxBytes = 64 * 1024;
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;
        // 上限を超える場合は追加 field を外した既存の小さな形にし、JSON を途中で切らない。
        var bounded = DebuggerState.Unavailable("response_too_large", "Debugger state exceeded the response size limit.", state.TargetProcessId,
            state.SolutionPath is { Length: > 4096 } ? state.SolutionPath[..4096] : state.SolutionPath);
        bounded.Mode = state.Mode.Length <= 64 ? state.Mode : state.Mode[..64];
        bounded.State = state.State.Length <= 64 ? state.State : state.State[..64];
        json = JsonConvert.SerializeObject(bounded, Formatting.Indented);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
            throw new InvalidOperationException("Debugger state result schema exceeds response size limit.");
        return json;
    }
}
