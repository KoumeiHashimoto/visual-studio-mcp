namespace VisualStudioMcpServer.Providers;

// ---- debugger.getState / execution.readTarget / execution.dispatch の応答本文（wire は camelCase）----
// 親 repository の src/VisualStudioDebugMcp.Vsix/Providers/ProviderExecutionContracts.cs と同じ member を持つ。

internal sealed record DebuggerProcessIdentity
{
    public int ProcessId { get; init; }
    public long? StartTicks { get; init; }
    public string Name { get; init; } = string.Empty;
}

internal sealed record StartupProjectIdentity
{
    public string UniqueName { get; init; } = string.Empty;
    public string ProviderKind { get; init; } = string.Empty;
}

internal sealed record StartupSnapshotBody
{
    public string SnapshotId { get; init; } = string.Empty;
    public StartupProjectIdentity[] StartupProjects { get; init; } = [];
    public string ProviderKind { get; init; } = string.Empty;
    public string ProfileStatus { get; init; } = string.Empty;
    public bool LaunchSupported { get; init; }
    public string? UnsupportedCode { get; init; }
    public string? UnsupportedReason { get; init; }
    public bool BuildMayOccur { get; init; }
}

internal sealed record DebuggerGetStateResponseBody
{
    public string State { get; init; } = "unknown";
    public string? DebugSessionId { get; init; }
    public string? ProcessSetSnapshotId { get; init; }
    public DebuggerProcessIdentity[] Processes { get; init; } = [];
    public bool ProcessesTruncated { get; init; }
    public bool CurrentThreadAvailable { get; init; }
    public string? SolutionPath { get; init; }
    public string? Configuration { get; init; }
    public string? Platform { get; init; }
    public StartupSnapshotBody? StartupSnapshot { get; init; }
    public ulong ProviderSequence { get; init; }
    public string? LaunchOperationId { get; init; }
    public bool SessionAgentOwned { get; init; }
}

/// <summary>execution 系の応答本文を型で解析し、catalog の per-field 上限を検査する。違反は契約違反として扱う。</summary>
internal static class ProviderExecutionBodies
{
    internal const int MaxProcesses = 32;
    internal const int MaxStartupProjects = 16;

    internal static DebuggerGetStateResponseBody ParseGetState(string body)
    {
        var value = ProviderProtocol.ParseBody<DebuggerGetStateResponseBody>(body);
        if (!IsState(value.State) || !IsOptionalId(value.DebugSessionId) || !IsOptionalId(value.ProcessSetSnapshotId) ||
            !IsOptionalId(value.LaunchOperationId) || value.Processes is not { Length: <= MaxProcesses } ||
            !value.Processes.All(static process => process is not null && process.ProcessId > 0 &&
                (process.StartTicks is null || process.StartTicks > 0) && process.Name is { Length: <= 260 }) ||
            !IsOptionalText(value.SolutionPath, 1024) || !IsOptionalText(value.Configuration, 256) ||
            !IsOptionalText(value.Platform, 256) || (value.StartupSnapshot is not null && !IsValidStartup(value.StartupSnapshot)))
            throw new ProviderProtocolViolationException("The debugger.getState response is out of range.");
        return value;
    }

    internal static bool IsState(string? value) => value is "design" or "running" or "break" or "unknown";

    internal static bool IsOptionalId(string? value) => value is null || ProviderProtocol.IsUuidN(value);

    internal static bool IsOptionalText(string? value, int maxLength) => value is null || value.Length <= maxLength;

    private static bool IsValidStartup(StartupSnapshotBody startup) =>
        ProviderProtocol.IsUuidN(startup.SnapshotId) && startup.StartupProjects is { Length: <= MaxStartupProjects } &&
        startup.StartupProjects.All(static project => project is not null && project.UniqueName is { Length: > 0 and <= 260 } &&
            ProviderBodyRules.IsToken(project.ProviderKind, 64)) &&
        ProviderBodyRules.IsToken(startup.ProviderKind, 64) && ProviderBodyRules.IsToken(startup.ProfileStatus, 64) &&
        ProviderBodyRules.IsOptionalToken(startup.UnsupportedCode, 64) && ProviderBodyRules.IsOptionalToken(startup.UnsupportedReason, 128);
}
