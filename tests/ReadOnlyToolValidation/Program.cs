using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using VisualStudioMcpServer;
using VisualStudioMcpServer.Tools;
using VisualStudioMcpServer.Diagnostics;
using EnvDTE80;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("These checks exercise the Windows COM STA dispatcher.");

var tools = new (string Name, Func<string> Call)[]
{
    ("get_solution_info", () => GetSolutionInfoTool.GetSolutionInfo("json")),
    ("get_error_list", () => GetErrorListTool.GetErrorList("json")),
    ("get_diagnostics", () => Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: Path.GetFullPath("Sample.sln")))),
    ("list_output_panes", () => ListOutputPanesTool.ListOutputPanes()),
    ("read_output_pane", () => ReadOutputPaneTool.ReadOutputPane("Build"))
};
const string privateDetail = "PRIVATE_SENTINEL /private/path token=DO_NOT_LOG";
var exceptions = new (Exception Exception, string Code)[]
{
    (new TimeoutException(privateDetail), "com_timeout"),
    (new InvalidComObjectException(privateDetail), "dte_disconnected"),
    (new COMException(privateDetail, unchecked((int)0x80010108)), "dte_disconnected"),
    (new COMException(privateDetail, unchecked((int)0x800401FD)), "dte_disconnected"),
    (new COMException(privateDetail, unchecked((int)0x800706BA)), "dte_disconnected"),
    (new COMException(privateDetail, unchecked((int)0x80010001)), "com_busy"),
    (new COMException(privateDetail, unchecked((int)0x8001010A)), "com_busy"),
    (new COMException(privateDetail, unchecked((int)0x80004005)), "com_error"),
    (new InvalidOperationException(privateDetail), "operation_failed")
};
var assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    assertions++;
}
void Error(string result, string expectedCode)
{
    var json = JObject.Parse(result);
    Check(json.Value<bool>("Success") == false, "Failure must have Success=false.");
    Check(json.Value<string>("Code") == expectedCode, $"Expected {expectedCode}.");
    Check(!string.IsNullOrWhiteSpace(json.Value<string>("Message")), "Actionable message required.");
    Check(result.Length < 2048 && !result.Contains("PRIVATE_SENTINEL"), "Response must be bounded and sanitized.");
}

foreach (var tool in tools)
{
    VisualStudioConnector.Available = false;
    VisualStudioConnector.OperationCount = 0;
    Error(tool.Call(), "dte_unavailable");
    Check(VisualStudioConnector.OperationCount == 0, "Missing DTE must prevent the read.");
    VisualStudioConnector.Available = true;

    foreach (var failure in exceptions)
    {
        foreach (var preflight in new[] { false, true })
        {
            VisualStudioConnector.Failure = failure.Exception;
            VisualStudioConnector.FailPreflight = preflight;
            using var diagnostic = new StringWriter();
            var previousError = Console.Error;
            string result;
            try
            {
                Console.SetError(diagnostic);
                result = tool.Call();
            }
            finally { Console.SetError(previousError); }
            Error(result, failure.Code);
            var log = diagnostic.ToString();
            Check(log.Contains(tool.Name) && log.Contains(failure.Exception.GetType().Name) && log.Contains($"0x{failure.Exception.HResult:X8}"), "Safe diagnostic identity required.");
            Check(!log.Contains("PRIVATE_SENTINEL") && !log.Contains("/private/path") && !log.Contains("DO_NOT_LOG"), "Diagnostic must not expose exception content.");
        }
    }
    VisualStudioConnector.Failure = null;
    VisualStudioConnector.FailPreflight = false;
    Console.WriteLine($"PASS guarded failures and sanitization: {tool.Name}");
}

var solutionJson = JObject.Parse(GetSolutionInfoTool.GetSolutionInfo("json"));
Check(solutionJson.Value<string>("SolutionName") == "Sample.sln" && solutionJson["Success"] == null, "Successful solution JSON shape must remain unchanged.");
Check(GetSolutionInfoTool.GetSolutionInfo().StartsWith("Current Solution:\n"), "Successful solution text must remain unchanged.");
VisualStudioConnector.Solution = new SolutionInfo
{
    SolutionName = "OpenFolderFixture",
    SolutionPath = string.Empty,
    Projects = new List<string>(),
    WorkspaceKind = "openFolder",
    WorkspaceRoot = "C:\\fixtures\\open-folder",
    CMakeMetadataStatus = "unsupported",
    CMakeMetadataReason = "fixture CMake metadata reason",
    ActiveConfigurationStatus = "unsupported",
    ActiveConfigurationReason = "fixture active configuration reason",
    StartupTargetStatus = "unsupported",
    StartupTargetReason = "fixture startup target reason"
};
var workspaceText = GetSolutionInfoTool.GetSolutionInfo();
Check(workspaceText.Contains("Current Open Folder Workspace:") && workspaceText.Contains("Workspace kind: openFolder") &&
    workspaceText.Contains("Workspace root: C:\\fixtures\\open-folder") &&
    workspaceText.Contains("CMake metadata: unsupported (fixture CMake metadata reason)") &&
    workspaceText.Contains("Active configuration: unsupported (fixture active configuration reason)") &&
    workspaceText.Contains("Startup target: unsupported (fixture startup target reason)"),
    "Open Folder text must preserve workspace and unsupported metadata.");
var workspaceJson = JObject.Parse(GetSolutionInfoTool.GetSolutionInfo("json"));
Check(workspaceJson.Value<string>("WorkspaceKind") == "openFolder" &&
    workspaceJson.Value<string>("WorkspaceRoot") == "C:\\fixtures\\open-folder" &&
    workspaceJson.Value<string>("CMakeMetadataStatus") == "unsupported" &&
    workspaceJson.Value<string>("CMakeMetadataReason") == "fixture CMake metadata reason" &&
    workspaceJson.Value<string>("ActiveConfigurationStatus") == "unsupported" &&
    workspaceJson.Value<string>("ActiveConfigurationReason") == "fixture active configuration reason" &&
    workspaceJson.Value<string>("StartupTargetStatus") == "unsupported" &&
    workspaceJson.Value<string>("StartupTargetReason") == "fixture startup target reason",
    "Open Folder JSON must preserve workspace and unsupported metadata.");
Check(JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedWorkspaceRoot: "C:\\fixtures\\open-folder")))
    .Value<string>("Code") == "unsupported", "DTE diagnostics must explicitly reject Open Folder.");
VisualStudioConnector.Solution = null;
Error(GetSolutionInfoTool.GetSolutionInfo(), "no_solution");
Check(GetErrorListTool.GetErrorList() == "No errors or warnings in the Error List.", "Empty error list text must remain unchanged.");
Check(ErrorListSeverityMapper.ToLegacySeverity(vsBuildErrorLevel.vsBuildErrorLevelHigh) == "Error" &&
    ErrorListSeverityMapper.ToLegacySeverity(vsBuildErrorLevel.vsBuildErrorLevelMedium) == "Warning" &&
    ErrorListSeverityMapper.ToLegacySeverity(vsBuildErrorLevel.vsBuildErrorLevelLow) == "Message" &&
    ErrorListSeverityMapper.ToLegacySeverity((vsBuildErrorLevel)0) == "Unknown",
    "Error List severity must come from the public enum, with unknown values left unknown.");
VisualStudioConnector.Errors.Add(new VisualStudioMcpServer.ErrorItem());
Check(JArray.Parse(GetErrorListTool.GetErrorList("json")).Count == 1, "Successful error JSON must remain an array.");
var expectedSolutionPath = Path.GetFullPath("Sample.sln");
VisualStudioConnector.Solution = new SolutionInfo { SolutionPath = expectedSolutionPath };
VisualStudioConnector.Errors = new List<VisualStudioMcpServer.ErrorItem>
{
    new VisualStudioMcpServer.ErrorItem { Severity = "Error", Description = "first", FileName = "C:\\fixtures\\a.cs" },
    new VisualStudioMcpServer.ErrorItem { Severity = "Warning", Description = "second", FileName = "C:\\fixtures\\b.cs" }
};
var diagnosticsFirst = JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: expectedSolutionPath, pageSize: 1)));
Check(diagnosticsFirst.Value<int>("Total") == 2 && diagnosticsFirst.Value<int>("Returned") == 1 &&
    diagnosticsFirst.Value<bool>("CaptureComplete") && diagnosticsFirst.Value<string>("OperationCorrelation") == "not_requested",
    "Diagnostics first page carries bounded snapshot and unknown provenance metadata.");
var diagnosticsCursor = diagnosticsFirst.Value<string>("NextCursor");
VisualStudioConnector.Errors = new List<VisualStudioMcpServer.ErrorItem> { new() { Description = "replacement" } };
var diagnosticsSecond = JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: expectedSolutionPath, pageSize: 1, cursor: diagnosticsCursor)));
Check(diagnosticsSecond.Value<int>("Total") == 2 && diagnosticsSecond["Items"]![0]!["Description"]!.Value<string>() == "second",
    "Diagnostics cursor pages the original immutable capture after live rows change.");
Check(JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: expectedSolutionPath, severity: "Warning", pageSize: 1, cursor: diagnosticsCursor)))
    .Value<string>("Code") == "cursor_filter_mismatch", "Diagnostics cursor is bound to filters.");
Check(JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: expectedSolutionPath, projectId: "unknown")))
    .Value<string>("Code") == "project_filter_unavailable", "Unavailable stable project identity is explicit.");
Check(JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: Path.GetFullPath("Other.sln"))))
    .Value<string>("Code") == "target_mismatch", "Diagnostics request cannot read a different solution.");
Check(JObject.Parse(Await(GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath: "relative.sln")))
    .Value<string>("Code") == "invalid_workspace_identity", "Diagnostics requires an absolute solution identity.");
VisualStudioConnector.Panes.Add("Build");
VisualStudioConnector.Panes.Add("Legacy pane");
VisualStudioConnector.OutputPanes.Add(new VisualStudioMcpServer.Output.OutputPaneInfo("{BUILD-GUID}", "Build", "available"));
VisualStudioConnector.OutputPanes.Add(new VisualStudioMcpServer.Output.OutputPaneInfo(null, "Legacy pane", "identity_unavailable"));
Check(ListOutputPanesTool.ListOutputPanes() == "Available Output Panes (2):\n  - Build\n  - Legacy pane", "Successful pane list must remain unchanged.");
var paneJson = JArray.Parse(ListOutputPanesTool.ListOutputPanes("json"));
Check(paneJson.Count == 2 && paneJson[0]!.Value<string>("Guid") == "{BUILD-GUID}" &&
    paneJson[0]!.Value<string>("Name") == "Build" && paneJson[0]!.Value<string>("Availability") == "available",
    "Structured output pane listing must include GUID, localized display name, and availability.");
Check(paneJson[1]!["Guid"]!.Type == JTokenType.Null && paneJson[1]!.Value<string>("Availability") == "identity_unavailable",
    "Structured output pane listing must not invent an identity when GUID is unavailable.");
VisualStudioConnector.OutputSnapshot = new VisualStudioMcpServer.Output.OutputPaneTextSnapshot("{BUILD-GUID}", "snapshot text", 1, 3, true);
var initialOutput = JObject.Parse(Await(ReadOutputSinceTool.ReadOutputSince("{BUILD-GUID}")));
Check(initialOutput.Value<string>("Mode") == "snapshot" && initialOutput.Value<bool>("IncrementalSupported") == false &&
    initialOutput.Value<bool>("Gap") == false && initialOutput.Value<bool>("Reset") == false &&
    initialOutput.Value<string>("Text") == "snapshot text" && !string.IsNullOrWhiteSpace(initialOutput.Value<string>("Cursor")),
    "First output read returns an identified bounded snapshot with an opaque cursor and no delta claim.");
var nextOutput = JObject.Parse(Await(ReadOutputSinceTool.ReadOutputSince("{BUILD-GUID}", initialOutput.Value<string>("Cursor"))));
Check(nextOutput.Value<bool>("Gap") == true && nextOutput.Value<bool>("Reset") == true &&
    nextOutput.Value<bool>("IncrementalSupported") == false,
    "Follow-up output read requests a fresh snapshot when delta continuity is unproven.");
Check(JObject.Parse(Await(ReadOutputSinceTool.ReadOutputSince("{OTHER-GUID}", initialOutput.Value<string>("Cursor"))))
    .Value<string>("Code") == "invalid_cursor", "Cursor cannot be reused for another pane identity.");
Check(JObject.Parse(Await(ReadOutputSinceTool.ReadOutputSince("{BUILD-GUID}", maxCharacters: 32769)))
    .Value<string>("Code") == "invalid_max_characters", "Snapshot character request is bounded before provider dispatch.");
VisualStudioConnector.OutputSnapshot = new VisualStudioMcpServer.Output.OutputPaneTextSnapshot("{BUILD-GUID}", new string('"', 32768), 1, 0, false);
var boundedOutput = Await(ReadOutputSinceTool.ReadOutputSince("{BUILD-GUID}", maxCharacters: 32768));
Check(boundedOutput.Length <= 65536 && JObject.Parse(boundedOutput).Value<bool>("Truncated"),
    "Serialized output snapshots remain valid JSON within the response limit.");
Check(ReadOutputPaneTool.ReadOutputPane("Build") == "output", "Successful output text must remain unchanged.");
VisualStudioConnector.Content = new string('a', 100000);
Check(ReadOutputPaneTool.ReadOutputPane("Build").Length < 65600, "Successful output must remain bounded.");
VisualStudioConnector.Content = null;
Check(ReadOutputPaneTool.ReadOutputPane("missing").StartsWith("Error: Could not find output pane"), "Missing pane must remain distinct from missing DTE.");
ProviderReadChecks.RunDiagnostics(Check, expectedSolutionPath);
ProviderReadChecks.RunOutput(Check);
Console.WriteLine($"Passed {assertions} read-only tool assertions, including injected failures on the Windows STA. No live DTE was used.");

static string Await(Task<string> task) => task.GetAwaiter().GetResult();
