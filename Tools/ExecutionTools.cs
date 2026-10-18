using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using VisualStudioMcpServer.Capabilities;
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
}

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
