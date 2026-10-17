namespace VisualStudioMcpServer.Tools;

/// <summary>Defines stable machine-readable debugger metadata independent of the IDE locale.</summary>
internal static class DebuggerMetadata
{
    /// <summary>Maps supported debugger language labels; all other labels remain explicitly unknown.</summary>
    public static string NormalizeLanguage(string? displayName) => displayName?.Trim().ToLowerInvariant() switch
    {
        "c++" or "c/c++" => "cpp",
        "c#" or "csharp" => "csharp",
        "basic" or "visual basic" or "vb" or "vb.net" => "basic",
        "f#" or "fsharp" => "fsharp",
        "javascript" or "jscript" => "javascript",
        "typescript" => "typescript",
        "python" => "python",
        "sql" or "t-sql" => "sql",
        _ => "unknown"
    };
}
