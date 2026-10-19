using VisualStudioMcpServer.Tools;

var cases = new (string Name, Func<(string? Code, string? Message)> Run, string? ExpectedCode)[]
{
    ("source location accepted with mutation policy", () => BreakpointRequestValidation.Validate("sample.cpp", 12, null, null, true, false), null),
    ("function location accepted with mutation policy", () => BreakpointRequestValidation.Validate(null, 0, "Namespace::Function", null, true, false), null),
    ("mutation denied by default", () => BreakpointRequestValidation.Validate("sample.cs", 10, null, null, false, false), "mutation_policy_required"),
    ("invalid source line rejected", () => BreakpointRequestValidation.Validate("sample.cs", 0, null, null, true, false), "invalid_location"),
    ("ambiguous location rejected", () => BreakpointRequestValidation.Validate("sample.cs", 10, "Function", null, true, false), "invalid_location"),
    ("condition evaluation requires explicit policy", () => BreakpointRequestValidation.Validate("sample.cpp", 10, null, "value > 0", true, false), "condition_policy_required"),
    ("condition accepted with both policies", () => BreakpointRequestValidation.Validate("sample.cpp", 10, null, "value > 0", true, true), null),
    ("oversized location rejected", () => BreakpointRequestValidation.Validate(new string('a', 2049), 1, null, null, true, false), "invalid_location"),
    ("removal denied by default", () => BreakpointRequestValidation.ValidateRemoval(1, "sample.cpp", 12, null, "", false), "mutation_policy_required"),
    ("removal requires location identity", () => BreakpointRequestValidation.ValidateRemoval(1, null, 0, null, "", true), "expected_location_required"),
    ("removal requires condition identity", () => BreakpointRequestValidation.ValidateRemoval(1, "sample.cpp", 12, null, null, true), "expected_condition_required"),
    ("source removal snapshot accepted", () => BreakpointRequestValidation.ValidateRemoval(1, "sample.cpp", 12, null, "", true), null),
    ("member depth lower bound", () => (BreakpointRequestValidation.ClampMemberDepth(-1).ToString(), null), "0"),
    ("member depth upper bound", () => (BreakpointRequestValidation.ClampMemberDepth(10).ToString(), null), "3"),
    ("frame index 0 rejected", () => (BreakpointRequestValidation.IsValidFrameIndex(0).ToString(), null), "False"),
    ("frame index 1 accepted", () => (BreakpointRequestValidation.IsValidFrameIndex(1).ToString(), null), "True"),
    ("frame index 128 accepted", () => (BreakpointRequestValidation.IsValidFrameIndex(128).ToString(), null), "True"),
    ("frame index 129 rejected", () => (BreakpointRequestValidation.IsValidFrameIndex(129).ToString(), null), "False"),
    ("member count lower bound", () => (BreakpointRequestValidation.ClampMemberCount(0).ToString(), null), "1"),
    ("member count upper bound", () => (BreakpointRequestValidation.ClampMemberCount(100).ToString(), null), "40"),
    ("current frame snapshot accepted", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Run", "Sample.dll", "Sample.dll").ToString(), null), "True"),
    ("changed frame count rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 6, "Run", "Run", "Sample.dll", "Sample.dll").ToString(), null), "False"),
    ("changed frame function rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Other", "Sample.dll", "Sample.dll").ToString(), null), "False"),
    ("changed frame module rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Run", "Sample.dll", "Other.dll").ToString(), null), "False"),
    ("arithmetic expression accepted for side-effect-free mode", () => (BreakpointRequestValidation.IsConservativeReadOnlyExpression("value + 1").ToString(), null), "True"),
    ("assignment expression rejected for side-effect-free mode", () => (BreakpointRequestValidation.IsConservativeReadOnlyExpression("value = 1").ToString(), null), "False"),
    ("function call rejected for side-effect-free mode", () => (BreakpointRequestValidation.IsConservativeReadOnlyExpression("UpdateValue()").ToString(), null), "False"),
    ("increment rejected for side-effect-free mode", () => (BreakpointRequestValidation.IsConservativeReadOnlyExpression("value++").ToString(), null), "False"),
    ("equals inside a string literal accepted", () => (BreakpointRequestValidation.IsConservativeReadOnlyExpression("message == \"a=b\"").ToString(), null), "True"),
    ("expression evaluation denied by default", () => BreakpointRequestValidation.ValidateExpression("value + 1", false, false), "evaluation_policy_required"),
    ("assignment evaluation requires side-effect policy", () => BreakpointRequestValidation.ValidateExpression("value = 1", true, false), "side_effect_policy_required"),
    ("read-only expression accepted with evaluation authorization", () => BreakpointRequestValidation.ValidateExpression("value + 1", true, false), null),
    ("expression size bound enforced", () => BreakpointRequestValidation.ValidateExpression(new string('a', 1025), true, true), "invalid_expression"),
    ("debugger control denied by default", () => BreakpointRequestValidation.ValidateDebuggerControl("pause", false), "debugger_control_policy_required"),
    ("unknown debugger control action rejected", () => BreakpointRequestValidation.ValidateDebuggerControl("terminate", true), "invalid_action"),
    ("step action accepted with explicit policy", () => BreakpointRequestValidation.ValidateDebuggerControl("step_over", true), null),
    ("fast same-frame step completes after a break event", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("step_over", "dbgBreakMode", false, true, false).ToString(), null), "True"),
    ("paused state alone does not prove step completion", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("step_over", "dbgBreakMode", false, false, false).ToString(), null), "False"),
    ("running step is not complete", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("step_into", "dbgRunMode", true, false, false).ToString(), null), "False"),
    ("continue can immediately hit another breakpoint", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("continue", "dbgBreakMode", true, true, false).ToString(), null), "True"),
    ("continue cannot reuse an unchanged paused state", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("continue", "dbgBreakMode", false, false, false).ToString(), null), "False"),
    ("pause observes a new break event", () => (VisualStudioMcpServer.DebuggerControlCompletion.HasCompleted("pause", "dbgBreakMode", false, true, false).ToString(), null), "True"),
    ("native environment read requires sensitive policy", () => ExpressionSensitivityPolicy.Validate("$env", "cpp", false, false), "sensitive_read_policy_required"),
    ("native environment read accepts explicit sensitive policy", () => ExpressionSensitivityPolicy.Validate("$ENV", "cpp", true, false), null),
    ("native command line read is sensitive", () => ExpressionSensitivityPolicy.Validate("$cmdline", "cpp", false, false), "sensitive_read_policy_required"),
    ("native current user pseudovariable is sensitive", () => ExpressionSensitivityPolicy.Validate("$user", "cpp", false, false), "sensitive_read_policy_required"),
    ("current user pseudovariable is sensitive in C sharp", () => ExpressionSensitivityPolicy.Validate("$user", "csharp", false, false), "sensitive_read_policy_required"),
    ("unclassified native context pseudovariable fails closed", () => ExpressionSensitivityPolicy.Validate("$tid", "cpp", true, true), "unsupported_expression_token"),
    ("return value pseudovariable is supported in C sharp", () => ExpressionSensitivityPolicy.Validate("$returnvalue", "csharp", false, false), null),
    ("return value pseudovariable is supported in native", () => ExpressionSensitivityPolicy.Validate("$returnvalue", "cpp", false, false), null),
    ("exception pseudovariable is supported in Visual Basic", () => ExpressionSensitivityPolicy.Validate("$exception", "basic", false, false), null),
    ("unknown language rejects pseudovariables", () => ExpressionSensitivityPolicy.Validate("$env", "unknown", true, true), "expression_language_unsupported"),
    ("unknown pseudovariable fails closed", () => ExpressionSensitivityPolicy.Validate("$mystery", "cpp", true, true), "unsupported_expression_token"),
    ("C sharp rejects native-only environment pseudovariable", () => ExpressionSensitivityPolicy.Validate("$env", "csharp", true, true), "unsupported_expression_token"),
    ("native register syntax fails closed", () => ExpressionSensitivityPolicy.Validate("@eax", "cpp", true, true), "unsupported_expression_token"),
    ("unclassified native context pseudovariable fails closed", () => ExpressionSensitivityPolicy.Validate("$handles", "cpp", true, true), "unsupported_expression_token"),
    ("sensitive permission does not authorize mutation", () => ExpressionSensitivityPolicy.Validate("$delete", "basic", true, false), "side_effect_policy_required"),
    ("side-effect permission does not authorize sensitive read", () => ExpressionSensitivityPolicy.Validate("$env", "cpp", false, true), "sensitive_read_policy_required"),
    ("Basic delete alias remains mutating", () => ExpressionSensitivityPolicy.Validate("$$DELETE", "basic", false, false), "side_effect_policy_required"),
    ("Basic delete alias accepts explicit side-effect policy", () => ExpressionSensitivityPolicy.Validate("$$delete", "basic", false, true), null),
    ("pseudovariable text inside a quoted literal is ignored", () => ExpressionSensitivityPolicy.Validate("\"$env\"", "cpp", false, false), null),
    ("escaped quote does not expose quoted pseudovariable", () => ExpressionSensitivityPolicy.Validate("\"text \\\" $env\"", "cpp", false, false), null),
    ("unterminated literal fails closed", () => ExpressionSensitivityPolicy.Validate("\"$env", "cpp", true, false), "unsupported_expression_token"),
    ("bare pseudovariable prefix fails closed", () => ExpressionSensitivityPolicy.Validate("$", "cpp", true, true), "unsupported_expression_token"),
    ("interpolated string fails closed", () => ExpressionSensitivityPolicy.Validate("$\"{$env}\"", "cpp", true, true), "unsupported_expression_token"),
    // 独立レビューの回避例（#53）。コメントや verbatim 文字列で $user を隠せてはならない。
    ("block comments fail closed", () => ExpressionSensitivityPolicy.Validate("/*\"*/$user/*\"*/", "csharp", false, false), "unsupported_expression_token"),
    ("line comments fail closed", () => ExpressionSensitivityPolicy.Validate("1 // \"\n$user", "csharp", false, false), "unsupported_expression_token"),
    ("verbatim string hiding a sensitive token fails closed", () => ExpressionSensitivityPolicy.Validate("@\"\\\" == null ? $user : (object)\"\\\"", "csharp", false, false), "unsupported_expression_token"),
    ("interpolated verbatim string fails closed", () => ExpressionSensitivityPolicy.Validate("$@\"{1}\" + $user", "csharp", false, false), "unsupported_expression_token"),
    ("reversed interpolated verbatim string fails closed", () => ExpressionSensitivityPolicy.Validate("@$\"{1}\" + $user", "csharp", false, false), "unsupported_expression_token"),
    ("native raw string fails closed", () => ExpressionSensitivityPolicy.Validate("R\"(\\)\" + $user", "cpp", false, false), "unsupported_expression_token"),
    ("native raw string with prefix fails closed", () => ExpressionSensitivityPolicy.Validate("u8R\"x(\\)x\" , $env", "cpp", false, false), "unsupported_expression_token"),
    ("csharp raw string hiding a sensitive token fails closed", () => ExpressionSensitivityPolicy.Validate("\"\"\"\\\"\"\" + $user + \"\\\"\"", "csharp", false, false), "unsupported_expression_token"),
    ("digit separator apostrophe fails closed", () => ExpressionSensitivityPolicy.Validate("1'000 + $user", "cpp", false, false), "unsupported_expression_token"),
    ("character literal holding a quote does not hide the next token", () => ExpressionSensitivityPolicy.Validate("'\"' + $user + '\"'", "csharp", false, false), "sensitive_read_policy_required"),
    ("valid character literals are still scanned", () => ExpressionSensitivityPolicy.Validate("'a' == 'b' ? $user : null", "csharp", false, false), "sensitive_read_policy_required"),
    ("escaped character literals are accepted", () => ExpressionSensitivityPolicy.Validate("'\\x41' + '\\n'", "csharp", false, false), null),
    ("quoted literal containing comment text is still a literal", () => ExpressionSensitivityPolicy.Validate("\"a // b /* c\"", "csharp", false, false), null),
    ("division is not a comment", () => ExpressionSensitivityPolicy.Validate("4 / 2 + 1", "csharp", false, false), null)
};

foreach (var test in cases)
{
    var result = test.Run();
    if (result.Code != test.ExpectedCode)
        throw new InvalidOperationException($"{test.Name}: expected code '{test.ExpectedCode ?? "ok"}', got '{result.Code ?? "ok"}'.");
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"Passed {cases.Length} breakpoint validation checks.");

// 実 IDE の EnvDTE frame は dynamic で、frame 由来の値を渡すと呼び出し全体が実行時束縛になる（#53 実機不具合の再現）。
dynamic dynamicFrame = new DynamicFrameProbe { Language = "cpp" };
var dynamicChecks = 0;
try
{
    var legacy = ExpressionSensitivityPolicy.Validate("$env", dynamicFrame.Language, false, false);
    _ = (string?)legacy.Code;
    throw new InvalidOperationException("The tuple result unexpectedly resolved its element name through dynamic binding.");
}
catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { dynamicChecks++; }
foreach (var (expression, sensitive, expected) in new (string, bool, string?)[]
{
    ("$env", false, "sensitive_read_policy_required"),
    ("$env", true, null),
    ("1 + 1", false, null),
    ("$returnvalue", false, null),
})
{
    var decision = ExpressionSensitivityPolicy.ValidateForFrame(expression, dynamicFrame.Language, sensitive, false);
    string? code = decision.Code;
    if (code != expected) throw new InvalidOperationException($"dynamic frame {expression}: expected '{expected ?? "ok"}', got '{code ?? "ok"}'.");
    dynamicChecks++;
}
dynamic csharpFrame = new DynamicFrameProbe { Language = "csharp" };
if (ExpressionSensitivityPolicy.ValidateForFrame("$user", csharpFrame.Language, false, false).Code != "sensitive_read_policy_required")
    throw new InvalidOperationException("dynamic C# frame did not gate $user.");
dynamicChecks++;
dynamic missingLanguage = new DynamicFrameProbe { Language = null };
if (ExpressionSensitivityPolicy.ValidateForFrame("$env", missingLanguage.Language, true, true).Code != "expression_language_unsupported")
    throw new InvalidOperationException("missing frame language did not fail closed.");
dynamicChecks++;
Console.WriteLine($"Passed {dynamicChecks} dynamic-frame expression policy checks.");

if (OperatingSystem.IsWindows())
{
    var apartment = VisualStudioMcpServer.StaComDispatcher.Invoke(() => Thread.CurrentThread.GetApartmentState());
    if (apartment != ApartmentState.STA) throw new InvalidOperationException("COM dispatcher must use STA.");

    var expected = new InvalidOperationException("dispatcher exception probe");
    try
    {
        VisualStudioMcpServer.StaComDispatcher.Invoke<int>(() => throw expected);
        throw new Exception("The dispatcher did not preserve the original exception.");
    }
    catch (InvalidOperationException actual) when (ReferenceEquals(actual, expected)) { }

    var expectedCom = new System.Runtime.InteropServices.COMException("COM exception probe", unchecked((int)0x80010001));
    try
    {
        VisualStudioMcpServer.StaComDispatcher.Invoke<int>(() => throw expectedCom);
        throw new Exception("The dispatcher did not preserve COM exception identity.");
    }
    catch (System.Runtime.InteropServices.COMException actual) when (ReferenceEquals(actual, expectedCom)) { }

    var expectedTimeout = new TimeoutException("operation timeout probe");
    try
    {
        VisualStudioMcpServer.StaComDispatcher.Invoke<int>(() => throw expectedTimeout);
        throw new Exception("The dispatcher did not report a timeout thrown by the operation.");
    }
    catch (VisualStudioMcpServer.StaComInvocationTimeoutException actual)
        when (actual.ExecutionStarted && ReferenceEquals(actual.InnerException, expectedTimeout)) { }

    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var blocker = Task.Run(() => VisualStudioMcpServer.StaComDispatcher.Invoke(() =>
    {
        entered.Set();
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test blocker was not released.");
        return true;
    }));
    var expiredOperationRan = false;
    var cleanupRan = false;
    Task? cleanup = null;
    try
    {
        if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Test blocker did not start.");
        try
        {
            VisualStudioMcpServer.StaComDispatcher.Invoke(() => expiredOperationRan = true, TimeSpan.FromMilliseconds(50));
            throw new Exception("The queued operation did not time out.");
        }
        catch (VisualStudioMcpServer.StaComInvocationTimeoutException ex) when (!ex.ExecutionStarted) { }
        cleanup = VisualStudioMcpServer.StaComDispatcher.Post(() => cleanupRan = true);
        try
        {
            cleanup.WaitAsync(TimeSpan.FromMilliseconds(50)).GetAwaiter().GetResult();
            throw new Exception("Cleanup should still be waiting behind the blocker.");
        }
        catch (TimeoutException) { }
    }
    finally { release.Set(); }
    blocker.GetAwaiter().GetResult();
    cleanup!.GetAwaiter().GetResult();
    VisualStudioMcpServer.StaComDispatcher.Invoke(() => true);
    if (expiredOperationRan) throw new InvalidOperationException("An expired queued operation executed after its caller timed out.");
    if (!cleanupRan) throw new InvalidOperationException("Required cleanup was discarded when its caller stopped waiting.");

    using var startedCallEntered = new ManualResetEventSlim();
    using var startedCallRelease = new ManualResetEventSlim();
    var executionStarted = false;
    var startedCaller = Task.Run(() =>
    {
        try
        {
            VisualStudioMcpServer.StaComDispatcher.Invoke(() =>
            {
                startedCallEntered.Set();
                if (!startedCallRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Started-call blocker was not released.");
                return true;
            }, TimeSpan.FromMilliseconds(50));
        }
        catch (VisualStudioMcpServer.StaComInvocationTimeoutException ex) { executionStarted = ex.ExecutionStarted; }
    });
    if (!startedCallEntered.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Started-call blocker did not start.");
    startedCaller.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    startedCallRelease.Set();
    VisualStudioMcpServer.StaComDispatcher.Invoke(() => true);
    if (!executionStarted) throw new InvalidOperationException("The dispatcher did not report that the timed-out COM operation had started.");
    Console.WriteLine("Passed 8 Windows STA dispatcher checks.");
}

internal sealed class DynamicFrameProbe
{
    public string? Language { get; set; }
}
