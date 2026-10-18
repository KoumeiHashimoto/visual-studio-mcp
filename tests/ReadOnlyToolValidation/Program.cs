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
    ("list_output_panes", ListOutputPanesTool.ListOutputPanes),
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
VisualStudioConnector.Panes.Add("Build");
Check(ListOutputPanesTool.ListOutputPanes() == "Available Output Panes (1):\n  - Build", "Successful pane list must remain unchanged.");
Check(ReadOutputPaneTool.ReadOutputPane("Build") == "output", "Successful output text must remain unchanged.");
VisualStudioConnector.Content = new string('a', 100000);
Check(ReadOutputPaneTool.ReadOutputPane("Build").Length < 65600, "Successful output must remain bounded.");
VisualStudioConnector.Content = null;
Check(ReadOutputPaneTool.ReadOutputPane("missing").StartsWith("Error: Could not find output pane"), "Missing pane must remain distinct from missing DTE.");
Console.WriteLine($"Passed {assertions} read-only tool assertions, including injected failures on the Windows STA. No live DTE was used.");
