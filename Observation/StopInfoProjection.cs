using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Observation;

/// <summary>v1.4: 例外で停止した break の公開用情報。説明文は allowSensitiveRead があるときだけ値を持つ。</summary>
internal sealed record StopExceptionInfo(
    string Kind,
    string Category,
    string TypeName,
    int? Code,
    string? Description,
    string DescriptionAvailability,
    bool Truncated);

/// <summary>v1.4: session が自然に終わったときの process ごとの結果。</summary>
internal sealed record StopProcessOutcome(int ProcessId, long? StartTicks, string Outcome, int? ExitCode);

/// <summary>観測（provider の mode event）から公開用の停止情報を作る。説明文は方針に従い、既定では返さない。</summary>
internal static class StopInfoProjection
{
    internal const int MaximumDescriptionLength = 1024;

    internal static StopExceptionInfo? Exception(ExceptionInfoBody? body, bool allowSensitiveRead)
    {
        if (body is null) return null;
        var hasDescription = !string.IsNullOrEmpty(body.Description);
        string? description = null;
        string availability;
        var truncated = body.Truncated;
        if (!hasDescription) availability = "unavailable";
        else if (!allowSensitiveRead) availability = "requires_allowSensitiveRead";
        else
        {
            availability = "available";
            description = body.Description!;
            if (description.Length > MaximumDescriptionLength)
            {
                description = description[..MaximumDescriptionLength];
                truncated = true;
            }
        }
        return new StopExceptionInfo(body.Kind, Cap(body.Category, 128), Cap(body.TypeName, 256), body.Code, description,
            availability, truncated);
    }

    internal static IReadOnlyList<StopProcessOutcome>? Outcomes(IReadOnlyList<ProcessOutcomeBody>? outcomes) =>
        outcomes?.Take(32).Select(static outcome => new StopProcessOutcome(outcome.ProcessId, outcome.StartTicks, outcome.Outcome,
            outcome.ExitCode)).ToList();

    /// <summary>provider 経由の状態に対する可否。provider がなければ unsupported、break で理由が記録済みなら available。</summary>
    internal static string Availability(bool providerBacked, string? state, DebuggerObservation? observation)
    {
        if (!providerBacked) return "unsupported";
        return state == "break" && observation?.StopReason is { } reason && reason != "unknown" ? "available" : "unknown";
    }

    /// <summary>get_debugger_state に載せる停止情報。EventSequence に対応する観測と、直近に自然終了した session から作る。</summary>
    internal sealed record StopInfoView(
        string Availability,
        string? StopReason,
        StopExceptionInfo? Exception,
        string? LastSessionId,
        IReadOnlyList<StopProcessOutcome>? LastSessionProcessOutcomes);

    internal static StopInfoView View(bool providerBacked, string? state, long? eventSequence, string? currentSessionId,
        DebuggerObservationStore? store, bool allowSensitiveRead)
    {
        if (!providerBacked || store is null) return new StopInfoView("unsupported", null, null, null, null);
        var observation = eventSequence is { } sequence ? store.FindBySequence(sequence) : null;
        string? reason = null;
        StopExceptionInfo? exception = null;
        if (state == "break" && observation is { State: DebuggerObservedState.Break })
        {
            reason = observation.StopReason;
            exception = Exception(observation.Exception, allowSensitiveRead);
        }
        string? lastId = null;
        IReadOnlyList<StopProcessOutcome>? outcomes = null;
        if (store.LastEndedSession is { } ended && (currentSessionId is null || currentSessionId != ended.DebugSessionId))
        {
            lastId = ended.DebugSessionId;
            outcomes = Outcomes(ended.ProcessOutcomes);
        }
        return new StopInfoView(Availability(true, state, observation), reason, exception, lastId, outcomes);
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
}
