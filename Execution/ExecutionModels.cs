using System.Text.Json;

namespace VisualStudioMcpServer.Execution;

internal enum ExecutionAction { StartDebugging, StartWithoutDebugging, StopDebugging }
internal enum ExecutionState { Validating, Accepted, Observed, Failed, Uncertain }

internal sealed record ExecutionRequest(
    string RequestId,
    ExecutionAction Action,
    int TargetProcessId,
    long TargetProcessStartUtcTicks,
    string ServerInstanceId,
    Guid ServerEpoch,
    string ExpectedSolutionPath,
    string? ExpectedStartupSnapshotId,
    string? ExpectedDebugSessionId,
    string? ExpectedProcessSetSnapshotId,
    bool AllowDebuggerControl = false,
    bool AllowLaunch = false,
    bool AllowBuild = false,
    bool AllowStopExistingSession = false,
    DateTimeOffset? ExecutionDeadlineUtc = null,
    int CallerWaitTimeoutMilliseconds = 10_000,
    string? ProjectUniqueName = null,
    ExecutionLaunchOverrides? LaunchOverrides = null,
    bool AllowLaunchOverrides = false);

/// <summary>
/// v1.4: 1 回限りの起動上書き。値（引数・作業ディレクトリ・環境変数）はログ・応答・台帳へ出さないため ToString を伏せる。
/// request ID の衝突検出には <see cref="Hash"/>（正準形の SHA-256）だけを使う。
/// </summary>
internal sealed record ExecutionLaunchOverrides(
    string? Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment,
    string Hash)
{
    public override string ToString() => "ExecutionLaunchOverrides(redacted)";
}

internal sealed record ExecutionTargetSnapshot(
    int TargetProcessId,
    long TargetProcessStartUtcTicks,
    string ServerInstanceId,
    Guid ServerEpoch,
    string? SolutionPath,
    string? Configuration,
    string? Platform,
    string? StartupSnapshotId,
    string? DebugSessionId,
    string? ProcessSetSnapshotId,
    string State,
    bool ProviderAvailable,
    bool BuildMayOccur,
    bool SessionAgentOwned,
    string? LaunchBlockedCode = null,
    string? LaunchBlockedReason = null);

internal sealed record ExecutionDispatchObservation(
    ExecutionState State,
    string Code,
    string Message,
    bool MayHaveChanged,
    string? DebugSessionId = null,
    string? ProcessSetSnapshotId = null,
    IReadOnlyList<ExecutionProcessOutcome>? ProcessOutcomes = null);

/// <summary>
/// stop 後の process ごとの結果。PID と開始時刻で同一 instance を表し、PID だけでは判定しない。
/// Outcome は exited / detached / unknown。ExitCode は終了を観測し公開 API で取得できた場合だけ値を持つ。
/// </summary>
internal sealed record ExecutionProcessOutcome(int ProcessId, long? StartTicks, string Outcome, int? ExitCode);

internal sealed record ExecutionOperationResult(
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
    IReadOnlyList<ExecutionProcessOutcome>? ProcessOutcomes = null);

internal sealed record ExecutionPolicy(bool AllowDebuggerControl, bool AllowLaunch, bool AllowBuild, bool AllowStopExistingSession)
{
    public static ExecutionPolicy From(ExecutionRequest request) => new(
        request.AllowDebuggerControl, request.AllowLaunch, request.AllowBuild, request.AllowStopExistingSession);
}

internal static class ExecutionRequestIdentity
{
    public static bool IsValidRequestId(string? requestId) =>
        requestId is { Length: >= 1 and <= 128 } && requestId.All(c => c is >= (char)0x21 and <= (char)0x7e);

    // The caller's wait budget changes only how long it waits for this record, not the action.
    public static string Fingerprint(ExecutionRequest request)
    {
        var normalized = new
        {
            request.Action,
            request.TargetProcessId,
            request.TargetProcessStartUtcTicks,
            request.ServerInstanceId,
            ServerEpoch = request.ServerEpoch.ToString("N"),
            Solution = NormalizePath(request.ExpectedSolutionPath),
            request.ExpectedStartupSnapshotId,
            request.ExpectedDebugSessionId,
            request.ExpectedProcessSetSnapshotId,
            // v1.4: project 指定と上書きの hash（値そのものは入れない）。
            request.ProjectUniqueName,
            OverrideHash = request.LaunchOverrides?.Hash,
            request.AllowLaunchOverrides,
            Policy = ExecutionPolicy.From(request),
            ExecutionDeadlineUtc = request.ExecutionDeadlineUtc?.ToUniversalTime().Ticks
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
    }

    private static string NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (root is not null && full.Length <= root.Length) return OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }
        catch { return path.Trim(); }
    }
}

internal sealed class ExecutionDispatchException(string message, bool executionStarted, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool ExecutionStarted { get; } = executionStarted;
}
