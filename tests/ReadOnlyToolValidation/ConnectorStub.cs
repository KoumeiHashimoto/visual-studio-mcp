namespace VisualStudioMcpServer;

/// <summary>Injects connector failures while exercising the actual tool entry points and STA dispatcher.</summary>
internal static class VisualStudioConnector
{
    public static bool Available { get; set; } = true;
    public static Exception? Failure { get; set; }
    public static bool FailPreflight { get; set; }
    public static int OperationCount { get; set; }
    public static SolutionInfo? Solution { get; set; } = new();
    public static List<ErrorItem> Errors { get; set; } = new();
    public static List<string> Panes { get; set; } = new();
    public static List<VisualStudioMcpServer.Output.OutputPaneInfo> OutputPanes { get; set; } = new();
    public static VisualStudioMcpServer.Output.OutputPaneTextSnapshot? OutputSnapshot { get; set; }
    public static string? Content { get; set; } = "output";

    public static object? GetActiveVS()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Connector preflight must run on STA.");
        if (FailPreflight && Failure != null) throw Failure;
        return Available ? new object() : null;
    }

    private static T Read<T>(T value)
    {
        OperationCount++;
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Connector reads must run on STA.");
        if (Failure != null) throw Failure;
        return value;
    }

    public static SolutionInfo? GetSolutionInfo() => Read(Solution);
    public static List<ErrorItem> GetErrorListItems() => Read(Errors);
    public static List<string> GetOutputPaneNames() => Read(Panes);
    public static List<VisualStudioMcpServer.Output.OutputPaneInfo> GetOutputPanes() => Read(OutputPanes);
    public static VisualStudioMcpServer.Output.OutputPaneTextSnapshot? ReadOutputPaneSnapshot(string paneGuid, int maxLines, int maxCharacters) => Read(OutputSnapshot);
    public static string? ReadOutputPane(string paneName, int maxLines) => Read(Content);
}

/// <summary>Represents the solution payload used by the production formatter.</summary>
internal sealed class SolutionInfo
{
    public string SolutionName { get; set; } = "Sample.sln";
    public string SolutionPath { get; set; } = "Sample.sln";
    public List<string> Projects { get; set; } = new() { "Sample" };
    public string? WorkspaceKind { get; set; }
    public string? WorkspaceRoot { get; set; }
    public string? CMakeMetadataStatus { get; set; }
    public string? CMakeMetadataReason { get; set; }
    public string? ActiveConfigurationStatus { get; set; }
    public string? ActiveConfigurationReason { get; set; }
    public string? StartupTargetStatus { get; set; }
    public string? StartupTargetReason { get; set; }
}

/// <summary>Represents error fields used by the production formatter.</summary>
internal sealed class ErrorItem
{
    public string Severity { get; set; } = "Error";
    public string Description { get; set; } = "Sample error";
    public override string ToString() => Description;
}
