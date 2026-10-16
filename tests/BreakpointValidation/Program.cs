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
    ("source removal snapshot accepted", () => BreakpointRequestValidation.ValidateRemoval(1, "sample.cpp", 12, null, "", true), null)
};

foreach (var test in cases)
{
    var result = test.Run();
    if (result.Code != test.ExpectedCode)
        throw new InvalidOperationException($"{test.Name}: expected code '{test.ExpectedCode ?? "ok"}', got '{result.Code ?? "ok"}'.");
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"Passed {cases.Length} breakpoint validation checks.");
