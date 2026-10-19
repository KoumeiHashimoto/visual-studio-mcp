using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Execution;

/// <summary>
/// 認証済み provider channel 上の execution.readTarget / execution.dispatch を使う本番 adapter。
/// channel がない、または VSIX が操作を実装していない場合は provider 不在として dispatch 前に止める。
/// 結果不明の dispatch は再送せず、execution.observed による確定を待つ。
/// </summary>
internal sealed class ProviderExecutionAdapter : IExecutionAdapter
{
    // channel は deadline + 2 秒で結果不明として確定する。coordinator の待機より先に確定させるための余裕。
    private static readonly TimeSpan ChannelSettleMargin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimumDispatchDeadline = TimeSpan.FromSeconds(1);

    private readonly Func<ProviderChannel?> _channel;
    private readonly Func<(int ProcessId, long StartTicks)?> _target;
    private readonly Func<string?> _serverInstanceId;
    private readonly Func<Guid?> _serverEpoch;
    private readonly Func<string?> _ownerFence;

    internal ProviderExecutionAdapter(Func<ProviderChannel?> channel, Func<(int ProcessId, long StartTicks)?> target,
        Func<string?> serverInstanceId, Func<Guid?> serverEpoch, Func<string?> ownerFence)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _serverInstanceId = serverInstanceId ?? throw new ArgumentNullException(nameof(serverInstanceId));
        _serverEpoch = serverEpoch ?? throw new ArgumentNullException(nameof(serverEpoch));
        _ownerFence = ownerFence ?? throw new ArgumentNullException(nameof(ownerFence));
    }

    public async Task<ExecutionTargetSnapshot> ReadTargetSnapshotAsync(TimeSpan remaining, CancellationToken cancellationToken)
    {
        var target = _target() ?? throw new InvalidOperationException("The bound Visual Studio identity is unavailable.");
        var serverInstanceId = _serverInstanceId() ?? string.Empty;
        var serverEpoch = _serverEpoch() ?? Guid.Empty;
        var channel = _channel();
        if (channel is null || channel.IsTerminated || !channel.ImplementedOperations.Contains("execution.readTarget"))
            return Unavailable(target, serverInstanceId, serverEpoch);

        var call = await channel.SendAsync("execution.readTarget", new TargetOnlyRequestBody { Target = Target(target) }, remaining)
            .ConfigureAwait(false);
        var result = await call.Result.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsOk)
        {
            // provider 自体が使えない場合は provider 不在として扱い、それ以外の失敗は対象を確認できないものとして止める。
            if (result.Code is ProviderErrorCodes.UnsupportedOperation or ProviderErrorCodes.ProviderDisconnected or
                ProviderErrorCodes.ProviderUnavailable)
                return Unavailable(target, serverInstanceId, serverEpoch);
            if (result.Code is ProviderErrorCodes.ProviderTimeout or ProviderErrorCodes.UiThreadTimeout)
                throw new TimeoutException("The provider target snapshot did not complete within its deadline.");
            throw new InvalidOperationException("The provider target snapshot failed: " + result.Code);
        }
        var body = ProviderExecutionBodies.ParseReadTarget(result.Body!);
        // VSIX が返した identity と、channel の identity・server 世代を照合する。食い違いは stale_target になる。
        var identityMatches = body.ServerInstanceId == channel.Identity.ServerInstanceId &&
            body.ProviderEpoch == channel.Identity.ProviderEpoch;
        return new ExecutionTargetSnapshot(body.TargetProcessId, body.TargetProcessStartTicks,
            identityMatches ? body.ServerInstanceId : string.Empty, identityMatches ? serverEpoch : Guid.Empty,
            body.SolutionPath, body.Configuration, body.Platform, body.StartupSnapshotId, body.DebugSessionId,
            body.ProcessSetSnapshotId, body.State, body.ProviderAvailable, body.BuildMayOccur, body.SessionAgentOwned,
            body.LaunchBlockedCode, body.LaunchBlockedReason);
    }

    public async Task<ExecutionDispatchObservation> DispatchAsync(ExecutionRequest request, string operationId, TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        var target = _target();
        var channel = _channel();
        if (target is null || channel is null || channel.IsTerminated || !channel.ImplementedOperations.Contains("execution.dispatch"))
            return NotDispatched("requires_vsix_provider", "The authenticated VSIX provider does not serve execution.dispatch.");
        var fence = _ownerFence();
        if (!ProviderProtocol.IsUuidN(fence))
            return NotDispatched("target_owner_busy", "This companion does not hold the Visual Studio mutation owner.");

        var deadline = remaining - ChannelSettleMargin;
        if (deadline < MinimumDispatchDeadline) deadline = MinimumDispatchDeadline;
        var call = await channel.SendAsync("execution.dispatch", new ExecutionDispatchRequestBody
        {
            Target = Target(target.Value),
            OperationId = operationId,
            Action = ActionName(request.Action),
            ExpectedStartupSnapshotId = request.ExpectedStartupSnapshotId,
            ExpectedDebugSessionId = request.ExpectedDebugSessionId,
            ExpectedProcessSetSnapshotId = request.ExpectedProcessSetSnapshotId,
            PolicyFlags = PolicyFlags(request),
            OwnerFence = fence!
        }, deadline).ConfigureAwait(false);
        var result = await call.Result.ConfigureAwait(false);
        return Map(request.Action, result);
    }

    /// <summary>provider の応答を coordinator の観測へ写す。結果不明は再送せず uncertain として保持させる。</summary>
    internal static ExecutionDispatchObservation Map(ExecutionAction action, ProviderCallResult result)
    {
        if (!result.IsOk)
        {
            if (result.DispatchState == ProviderDispatchState.NotDispatched)
                return NotDispatched(result.Code ?? ProviderErrorCodes.InternalError,
                    Bounded(result.Message) ?? "The provider rejected the action before dispatch.");
            if (result.DispatchState == ProviderDispatchState.Dispatched && result.Code is not ProviderErrorCodes.ResultUnknown)
                return new ExecutionDispatchObservation(ExecutionState.Failed, result.Code ?? ProviderErrorCodes.InternalError,
                    Bounded(result.Message) ?? "The provider call returned a failure.", false);
            return new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                "The action may have started; query this operation without replaying it.", true);
        }

        ExecutionDispatchResponseBody body;
        try { body = ProviderExecutionBodies.ParseDispatch(result.Body!); }
        catch (ProviderProtocolException)
        {
            return new ExecutionDispatchObservation(ExecutionState.Uncertain, "result_unknown",
                "The provider dispatch response was invalid; the action may have started.", true);
        }
        return body.State switch
        {
            // no-debug 起動は debugger session を作らない。起動受理と dispatch の後始末が確定した時点で完了とし、
            // アプリの終了は追跡しない（ProcessTrackingStatus で明示する）。
            "accepted" when action == ExecutionAction.StartWithoutDebugging && body.LaunchAccepted =>
                new ExecutionDispatchObservation(ExecutionState.Observed, body.Code,
                    $"Launch without debugging was accepted; process tracking: {body.ProcessTrackingStatus}.", true),
            "accepted" => new ExecutionDispatchObservation(ExecutionState.Accepted, body.Code,
                $"The provider accepted the action; process tracking: {body.ProcessTrackingStatus}.", body.MayHaveChanged,
                body.DebugSessionId, body.ProcessSetSnapshotId),
            "failed" => new ExecutionDispatchObservation(ExecutionState.Failed, body.Code,
                "The provider reported that the action failed.", body.MayHaveChanged, body.DebugSessionId, body.ProcessSetSnapshotId),
            _ => new ExecutionDispatchObservation(ExecutionState.Uncertain, body.Code,
                "The provider could not confirm the action outcome; query this operation without replaying it.", true,
                body.DebugSessionId, body.ProcessSetSnapshotId)
        };
    }

    /// <summary>execution.observed を coordinator の観測へ写す。停止の process ごとの結果はそのまま保持する。</summary>
    internal static ExecutionDispatchObservation MapObserved(ExecutionObservedEventBody body)
    {
        var outcomes = MapOutcomes(body.ProcessOutcomes);
        return body.State switch
        {
            "observed" => new ExecutionDispatchObservation(ExecutionState.Observed, body.Code ?? "observed",
                outcomes is null ? "The provider observed the requested debugger transition." : OutcomeMessage(outcomes),
                true, body.DebugSessionId, body.ProcessSetSnapshotId, outcomes),
            "failed" => new ExecutionDispatchObservation(ExecutionState.Failed, body.Code ?? "failed",
                "The provider observed that the action failed.", true, body.DebugSessionId, body.ProcessSetSnapshotId, outcomes),
            _ => new ExecutionDispatchObservation(ExecutionState.Uncertain, body.Code ?? "result_unknown",
                "The provider could not confirm the action outcome.", true, body.DebugSessionId, body.ProcessSetSnapshotId, outcomes)
        };
    }

    private static IReadOnlyList<ExecutionProcessOutcome>? MapOutcomes(ProcessOutcomeBody[]? outcomes) =>
        outcomes?.Select(static outcome => new ExecutionProcessOutcome(outcome.ProcessId, outcome.StartTicks, outcome.Outcome,
            outcome.ExitCode)).ToArray();

    /// <summary>debugger の終了と process ごとの結果を分けて述べる。detached は生存の観測であり、停止の主張ではない。</summary>
    internal static string OutcomeMessage(IReadOnlyList<ExecutionProcessOutcome> outcomes)
    {
        var exited = outcomes.Count(static outcome => outcome.Outcome == "exited");
        var detached = outcomes.Count(static outcome => outcome.Outcome == "detached");
        var unknown = outcomes.Count - exited - detached;
        return $"The debugger session ended. Per-process outcomes: {exited} exited, {detached} detached (still running after the bounded wait), {unknown} unknown.";
    }

    internal static string ActionName(ExecutionAction action) => action switch
    {
        ExecutionAction.StartDebugging => "startDebugging",
        ExecutionAction.StartWithoutDebugging => "startWithoutDebugging",
        _ => "stopDebugging"
    };

    internal static string[] PolicyFlags(ExecutionRequest request)
    {
        var flags = new List<string>(4);
        if (request.AllowDebuggerControl) flags.Add("allowDebuggerControl");
        if (request.AllowLaunch) flags.Add("allowLaunch");
        if (request.AllowBuild) flags.Add("allowBuild");
        if (request.AllowStopExistingSession) flags.Add("allowStopExistingSession");
        return flags.ToArray();
    }

    private static ProviderTarget Target((int ProcessId, long StartTicks) target) => new()
    {
        VisualStudioPid = target.ProcessId,
        VisualStudioStartTicks = target.StartTicks
    };

    private static ExecutionTargetSnapshot Unavailable((int ProcessId, long StartTicks) target, string serverInstanceId, Guid serverEpoch) =>
        new(target.ProcessId, target.StartTicks, serverInstanceId, serverEpoch, null, null, null, null, null, null, "unknown",
            ProviderAvailable: false, BuildMayOccur: true, SessionAgentOwned: false);

    private static ExecutionDispatchObservation NotDispatched(string code, string message) =>
        new(ExecutionState.Failed, code, message, false);

    private static string? Bounded(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= 512 ? value : value[..512];
}
