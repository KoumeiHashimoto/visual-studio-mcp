using VisualStudioMcpServer.Observation;

namespace VisualStudioMcpServer.Tools;

/// <summary>v1.4: get_break_snapshot の結果。frame と locals は既存の get_call_stack / get_debugger_locals と同じ形。</summary>
internal sealed record BreakSnapshotResponse
{
    public bool Success { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? TargetProcessId { get; init; }
    public string? State { get; init; }
    public string? DebugSessionId { get; init; }
    public long? EventSequence { get; init; }
    public string? StopReason { get; init; }
    public StopExceptionInfo? Exception { get; init; }
    public int? CurrentThreadId { get; init; }
    public List<DebuggerFrameInfo>? Frames { get; init; }
    public int? TotalFrameCount { get; init; }
    public List<DebuggerVariableInfo>? Locals { get; init; }
    public int? LocalsTotalCount { get; init; }
    public string? LocalsAvailability { get; init; }
    /// <summary>読み取りの間に break の世代が変わった。frame と locals は返さない。</summary>
    public bool Changed { get; init; }
    public bool Partial { get; init; }
    public bool Truncated { get; init; }
    /// <summary>available: provider の世代（EventSequence）を返せる。unavailable: provider がなく世代照合できない。</summary>
    public string GenerationAvailability { get; init; } = "unavailable";
}

/// <summary>
/// v1.4: break の世代（観測 store の公開 sequence）による照合。選択・locals・式評価は、利用者が取得した
/// 世代と現在の世代が同じときだけ続ける。引数を省略した呼び出しは従来どおり（世代を検査しない）。
/// </summary>
internal static class BreakGeneration
{
    internal const string StaleCode = "stale_break_generation";
    internal const string UnavailableCode = "generation_unavailable";
    internal const int MaxSnapshotFrames = 16;
    internal const int MaxSnapshotLocals = 40;
    // break 入場の通知は IDE の状態より少し遅れ得る。観測側が break を記録するまで待つ上限。
    internal static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(25);

    /// <summary>expectedEventSequence の照合。通れば null、拒否なら (code, message)。</summary>
    internal static (string Code, string Message)? Guard(long? expected, DebuggerObservationStore? store)
    {
        if (expected is null) return null;
        if (store is null || store.IsClosed)
            return (UnavailableCode, "Break generations require the authenticated VSIX provider; omit expectedEventSequence or connect the provider.");
        if (expected.Value != store.CurrentSequence)
            return (StaleCode, "The debugger moved since EventSequence was read; read the stack or break snapshot again.");
        return null;
    }

    /// <summary>操作の直前と直後に同じ世代であることを確かめる。変わっていれば拒否（code, message）。</summary>
    internal static (string Code, string Message)? Recheck(long? expected, DebuggerObservationStore? store) =>
        expected is null ? null : Guard(expected, store);

    /// <summary>
    /// 現在の break の世代。IDE が break を報告していて、観測が break をまだ記録していなければ短時間だけ待つ。
    /// 待っても break が記録されなければ最新の sequence をそのまま返す（理由は unknown になる）。
    /// </summary>
    internal static (long? Sequence, DebuggerObservation? Observation) Current(DebuggerObservationStore? store, bool settle,
        Func<TimeSpan, Task>? delay = null)
    {
        if (store is null) return (null, null);
        var waited = TimeSpan.Zero;
        while (true)
        {
            var (latest, current) = store.LatestWithSequence();
            if (!settle || latest is { State: DebuggerObservedState.Break } || waited >= SettleWait)
                return (current, latest is { State: DebuggerObservedState.Break } ? latest : null);
            if (delay is null) Thread.Sleep(SettleStep); else delay(SettleStep).GetAwaiter().GetResult();
            waited += SettleStep;
        }
    }

    /// <summary>
    /// 1 回の呼び出しで state・stack・locals を読む。世代は読み取りの前後で同じでなければならない。
    /// 呼び出し側が STA 上で同期的に呼ぶ。式評価もフレーム選択もしない。
    /// </summary>
    internal static BreakSnapshotResponse Build(int maxFrames, bool includeLocals, int maxLocals, bool allowSensitiveRead,
        DebuggerObservationStore? store, Func<DebuggerInspectionResponse> readStack,
        Func<DebuggerFrameInfo, int, int, int, DebuggerInspectionResponse> readLocals, Func<TimeSpan, Task>? delay = null)
    {
        maxFrames = Math.Clamp(maxFrames, 1, MaxSnapshotFrames);
        maxLocals = Math.Clamp(maxLocals, 0, MaxSnapshotLocals);
        var availability = store is null || store.IsClosed ? "unavailable" : "available";
        var (sequence, observation) = Current(availability == "available" ? store : null, settle: true, delay);

        var stack = readStack();
        if (!stack.Success)
            return new BreakSnapshotResponse
            {
                Success = false, Code = stack.Code, Message = stack.Message, TargetProcessId = stack.TargetProcessId,
                State = stack.DebuggerMode, GenerationAvailability = availability
            };

        var frames = stack.Frames.Take(maxFrames).ToList();
        var total = stack.TotalCount ?? frames.Count;
        List<DebuggerVariableInfo>? locals = null;
        int? localsTotal = null;
        string? localsAvailability = includeLocals ? "unavailable" : "not_requested";
        var truncated = stack.Truncated || total > frames.Count;
        if (includeLocals && maxLocals > 0 && frames.Count > 0 && !string.IsNullOrWhiteSpace(frames[0].Function))
        {
            var read = readLocals(frames[0], total, stack.CurrentThreadId ?? 0, maxLocals);
            if (read.Success)
            {
                locals = read.Locals;
                localsTotal = read.TotalCount;
                localsAvailability = "available";
                truncated |= read.Truncated;
            }
        }
        else if (includeLocals && maxLocals == 0) localsAvailability = "not_requested";

        // 読み取りの間に世代が進んだ（step・continue など）。混ざった状態を返さない。
        // 通知は IDE の状態より少し遅れ得るため、短く待ってから世代を再確認する。
        if (availability == "available")
        {
            if (delay is null) Thread.Sleep(SettleStep); else delay(SettleStep).GetAwaiter().GetResult();
        }
        var after = availability == "available" ? store!.CurrentSequence : (long?)null;
        if (availability == "available" && after != sequence)
            return new BreakSnapshotResponse
            {
                Success = true, Code = "ok", Message = "The debugger moved while the snapshot was being read; read it again.",
                TargetProcessId = stack.TargetProcessId, State = stack.DebuggerMode, EventSequence = sequence,
                Changed = true, Partial = true, GenerationAvailability = availability
            };

        return new BreakSnapshotResponse
        {
            Success = true, Code = "ok", Message = $"Read {frames.Count} frame(s)" + (locals is null ? "." : $" and {locals.Count} local(s)."),
            TargetProcessId = stack.TargetProcessId, State = stack.DebuggerMode,
            DebugSessionId = observation?.DebugSessionId, EventSequence = sequence,
            StopReason = observation?.StopReason,
            Exception = StopInfoProjection.Exception(observation?.Exception, allowSensitiveRead),
            CurrentThreadId = stack.CurrentThreadId, Frames = frames, TotalFrameCount = total,
            Locals = locals, LocalsTotalCount = localsTotal, LocalsAvailability = localsAvailability,
            Truncated = truncated, GenerationAvailability = availability
        };
    }
}
