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
    ("member count lower bound", () => (BreakpointRequestValidation.ClampMemberCount(0).ToString(), null), "1"),
    ("member count upper bound", () => (BreakpointRequestValidation.ClampMemberCount(100).ToString(), null), "40"),
    ("current frame snapshot accepted", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Run", "Sample.dll", "Sample.dll").ToString(), null), "True"),
    ("changed frame count rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 6, "Run", "Run", "Sample.dll", "Sample.dll").ToString(), null), "False"),
    ("changed frame function rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Other", "Sample.dll", "Sample.dll").ToString(), null), "False"),
    ("changed frame module rejected", () => (BreakpointRequestValidation.MatchesFrameSnapshot(2, 5, 5, "Run", "Run", "Sample.dll", "Other.dll").ToString(), null), "False")
};

foreach (var test in cases)
{
    var result = test.Run();
    if (result.Code != test.ExpectedCode)
        throw new InvalidOperationException($"{test.Name}: expected code '{test.ExpectedCode ?? "ok"}', got '{result.Code ?? "ok"}'.");
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"Passed {cases.Length} breakpoint validation checks.");
