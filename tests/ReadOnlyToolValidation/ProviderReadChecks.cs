using Newtonsoft.Json.Linq;
using VisualStudioMcpServer;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Tools;

/// <summary>
/// provider を優先する read tool（#59/#60）の offline 検証。fake transport と connector stub だけを使い、
/// provider 経路・DTE fallback・由来の表示・照合失敗時に fallback しないことを確認する。
/// </summary>
internal static class ProviderReadChecks
{
    // 親 repository の ProviderHostValidation が VSIX 側 DTO から生成する文字列と同一。
    internal const string DiagnosticsGolden = "{\"captureId\":\"0123456789ab4def8123456789abcdef\",\"providerGeneration\":3,\"provenance\":\"errorListTable\",\"partial\":false,\"sourcesStable\":true,\"sourceCount\":2,\"scannedRows\":2,\"codeAvailability\":\"partial\",\"sourceAvailability\":\"available\",\"projectIdentityAvailability\":\"partial\",\"rangeAvailability\":\"startOnly\",\"rows\":[{\"code\":\"CS0168\",\"severity\":\"warning\",\"description\":\"変数 'x' は宣言されていますが、使用されていません。\",\"descriptionTruncated\":false,\"documentPath\":\"C:\\\\fixtures\\\\a.cs\",\"line\":3,\"column\":5,\"projectName\":\"App\",\"projectId\":\"11111111-2222-4333-8444-555555555555\",\"source\":\"build\",\"buildTool\":\"Compiler\"},{\"code\":null,\"severity\":\"error\",\"description\":\"info is not an English severity word here\",\"descriptionTruncated\":false,\"documentPath\":null,\"line\":null,\"column\":null,\"projectName\":null,\"projectId\":null,\"source\":\"other\",\"buildTool\":null}]}";
    internal const string ProjectId = "11111111-2222-4333-8444-555555555555";

    internal static void RunDiagnostics(Action<bool, string> check, string expectedSolutionPath)
    {
        var fake = new FakeReadTransport();
        ProviderReadGateway.Transport = fake;
        VisualStudioConnector.Available = true;
        VisualStudioConnector.Failure = null;
        VisualStudioConnector.Solution = new SolutionInfo { SolutionPath = expectedSolutionPath };
        VisualStudioConnector.Errors = new List<ErrorItem> { new() { Description = "dte row" } };
        VisualStudioConnector.OperationCount = 0;

        fake.Next = Ok(DiagnosticsGolden);
        var first = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, pageSize: 1));
        var request = fake.Calls.Single();
        check(request.Operation == "diagnostics.capture" && request.WorkspacePath == expectedSolutionPath &&
            ProviderBodyRules.TryValidateRequest(request.Operation, request.Body, out _) &&
            request.Body.Contains("\"severity\":null"), "Diagnostics capture sends the expected workspace identity to the provider.");
        var firstItem = first["Items"]![0]!;
        check(first.Value<string>("Provenance") == "errorListTable" && first.Value<string>("ProviderEpoch") == FakeReadTransport.Epoch &&
            first["ProviderFallbackReason"]!.Type == JTokenType.Null && first.Value<int>("Total") == 2 &&
            first.Value<string>("CodeAvailability") == "partial" && first.Value<string>("RangeAvailability") == "startOnly" &&
            firstItem.Value<string>("Code") == "CS0168" && firstItem.Value<string>("Severity") == "Warning" &&
            firstItem.Value<string>("Source") == "build" && firstItem.Value<string>("ProjectId") == ProjectId &&
            firstItem.Value<int>("Line") == 3 && VisualStudioConnector.OperationCount == 0,
            "Provider diagnostics keep code, source, project identity and severity without a DTE read.");
        VisualStudioConnector.Available = false;
        var second = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, pageSize: 1, cursor: first.Value<string>("NextCursor")));
        check(second.Value<string>("Provenance") == "errorListTable" && second["Items"]![0]!.Value<string>("Severity") == "Error" &&
            second["Items"]![0]!["Code"]!.Type == JTokenType.Null && fake.Calls.Count == 1,
            "Provider snapshot pages are served from the capture without re-reading the provider or DTE.");
        VisualStudioConnector.Available = true;

        fake.Next = Ok(DiagnosticsGolden);
        var byProject = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, projectId: ProjectId));
        check(byProject.Value<int>("Total") == 1 && fake.Calls[^1].Body.Contains("\"projectId\":\"" + ProjectId + "\""),
            "Project identity filters are sent to the provider and applied to the stable snapshot.");
        fake.Next = Ok(DiagnosticsGolden);
        var bySource = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, source: "Build", severity: "Warning"));
        check(bySource.Value<int>("Total") == 1 && fake.Calls[^1].Body.Contains("\"source\":\"build\"") &&
            fake.Calls[^1].Body.Contains("\"severity\":\"warning\""), "Source and severity filters use the provider vocabulary.");
        fake.Next = Ok(DiagnosticsGolden);
        check(JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, source: "intellisense")).Value<int>("Total") == 0,
            "IntelliSense is never inferred from build or other rows.");
        fake.Next = Ok(DiagnosticsGolden.Replace("\"partial\":false", "\"partial\":true"));
        var partial = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath));
        check(!partial.Value<bool>("CaptureComplete") && partial.Value<bool>("Truncated"), "Partial provider captures are reported as incomplete.");
        fake.Next = Ok(DiagnosticsGolden);
        var openFolder = JObject.Parse(Call(expectedWorkspaceRoot: "C:\\fixtures\\open-folder"));
        check(openFolder.Value<string>("Provenance") == "errorListTable" && fake.Calls[^1].WorkspacePath == "C:\\fixtures\\open-folder",
            "Open Folder diagnostics use the provider identity check instead of the DTE-only rejection.");

        VisualStudioConnector.OperationCount = 0;
        fake.Next = new ProviderReadOutcome("error", "target_mismatch", "Other workspace.", null, "notDispatched", FakeReadTransport.Epoch, null);
        check(JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath)).Value<string>("Code") == "target_mismatch" &&
            VisualStudioConnector.OperationCount == 0, "A provider workspace mismatch never falls back to reading DTE.");
        fake.Next = ProviderReadGateway.Unavailable("operation_not_implemented", FakeReadTransport.Epoch);
        var fallback = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath));
        check(fallback.Value<string>("Provenance") == "dte" && fallback.Value<string>("ProviderFallbackReason") == "operation_not_implemented" &&
            fallback.Value<string>("CodeAvailability") == "unavailable" && fallback["Items"]![0]!.Value<string>("Description") == "dte row",
            "An unavailable provider falls back to the DTE snapshot with truthful provenance.");
        fake.Next = new ProviderReadOutcome("error", "provider_busy", "Busy.", null, "notDispatched", FakeReadTransport.Epoch, null);
        var busy = JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, projectId: ProjectId));
        check(busy.Value<string>("Code") == "project_filter_unavailable" && busy.Value<string>("ProviderFallbackReason") == "provider_busy",
            "DTE fallback keeps its explicit unsupported filters and reports why the provider was not used.");
        fake.Next = Ok(DiagnosticsGolden.Replace("\"rows\":[", "\"extra\":1,\"rows\":["));
        check(JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath)).Value<string>("Code") == "provider_response_invalid",
            "A provider body outside the contract is rejected.");
        fake.Next = Ok(DiagnosticsGolden.Replace("\"provenance\":\"errorListTable\"", "\"provenance\":\"exact\""));
        check(JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath)).Value<string>("Code") == "provider_response_invalid",
            "Unknown provenance values are rejected instead of being presented as evidence.");
        check(JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, source: "compiler")).Value<string>("Code") == "invalid_source" &&
            JObject.Parse(Call(expectedSolutionPath: expectedSolutionPath, projectId: "a b")).Value<string>("Code") == "invalid_project_id",
            "Invalid source and project filters are rejected before dispatch.");

        var huge = DiagnosticsGolden.Replace("\"description\":\"info is not an English severity word here\"",
            "\"description\":\"" + new string('"', 1).Replace("\"", "\\\"") + new string('x', 4000) + "\"");
        var bigRows = string.Join(",", Enumerable.Range(0, 200).Select(_ =>
            "{\"code\":null,\"severity\":\"error\",\"description\":\"" + new string('\\', 1).Replace("\\", "\\\\") + new string('y', 4000) +
            "\",\"descriptionTruncated\":true,\"documentPath\":null,\"line\":null,\"column\":null,\"projectName\":null,\"projectId\":null,\"source\":\"other\",\"buildTool\":null}"));
        fake.Next = Ok(huge.Substring(0, huge.IndexOf("\"rows\":[", StringComparison.Ordinal)) + "\"rows\":[" + bigRows + "]}");
        var bounded = Call(expectedSolutionPath: expectedSolutionPath, pageSize: 200);
        var boundedJson = JObject.Parse(bounded);
        check(bounded.Length <= 65536 && boundedJson.Value<bool>("Truncated") && boundedJson.Value<int>("Returned") < 200 &&
            boundedJson.Value<string>("NextCursor") is not null, "Provider pages stay within 64 Ki characters and continue by cursor.");

        ProviderReadGateway.Transport = null;
        Console.WriteLine("PASS provider-preferred diagnostics and DTE fallback");
    }

    private static string Call(string? expectedSolutionPath = null, string? expectedWorkspaceRoot = null, string? severity = null,
        string? projectId = null, string? file = null, string? source = null, int pageSize = 100, string? cursor = null) =>
        GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath, expectedWorkspaceRoot, severity, projectId, file, source, pageSize,
            cursor).GetAwaiter().GetResult();

    internal static ProviderReadOutcome Ok(string body) => new("ok", null, null, body, "dispatched", FakeReadTransport.Epoch, null);
}

/// <summary>要求を記録し、設定済みの応答を 1 回だけ返す fake transport。</summary>
internal sealed class FakeReadTransport : IProviderReadTransport
{
    internal const string Epoch = "99999999999949998999999999999999";
    internal List<(string Operation, string Body, string? WorkspacePath)> Calls { get; } = new();
    internal ProviderReadOutcome? Next { get; set; }

    public Task<ProviderReadOutcome> SendAsync(string operation, Func<ProviderTarget, object> bodyFactory,
        string? workspacePath, TimeSpan timeout)
    {
        var body = ProviderProtocol.SerializeBody(bodyFactory(new ProviderTarget
        {
            VisualStudioPid = 4242,
            VisualStudioStartTicks = 638900000000000000,
            WorkspacePath = workspacePath
        }));
        Calls.Add((operation, body, workspacePath));
        var next = Next ?? throw new InvalidOperationException("No fake provider response configured.");
        Next = null;
        return Task.FromResult(next);
    }
}
