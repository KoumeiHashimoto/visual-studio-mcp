using EnvDTE80;

namespace VisualStudioMcpServer.Diagnostics;

/// <summary>
/// Maps the public Error List severity enum to the legacy string values returned by this server.
/// </summary>
internal static class ErrorListSeverityMapper
{
    internal static string ToLegacySeverity(vsBuildErrorLevel errorLevel) => errorLevel switch
    {
        vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
        vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
        vsBuildErrorLevel.vsBuildErrorLevelLow => "Message",
        _ => "Unknown"
    };
}
