using System.ComponentModel;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// Describes a breakpoint returned by the Visual Studio debugger.
/// </summary>
public sealed class BreakpointInfo
{
    /// <summary>Gets or sets the one-based index from the current list snapshot.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the source file path.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the one-based source line.</summary>
    public int Line { get; set; }

    /// <summary>Gets or sets the function name.</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Gets or sets the condition expression.</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the breakpoint is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the hit count, when available.</summary>
    public int? HitCount { get; set; }
}

/// <summary>
/// Carries a structured result for a breakpoint operation.
/// </summary>
public sealed class BreakpointToolResponse
{
    /// <summary>Gets or sets whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a stable error code when the operation failed.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets an actionable result message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the Visual Studio process ID.</summary>
    public int? TargetProcessId { get; set; }

    /// <summary>Gets or sets the observed debugger mode.</summary>
    public string? DebuggerMode { get; set; }

    /// <summary>Gets or sets the current list of breakpoints.</summary>
    public List<BreakpointInfo> Breakpoints { get; set; } = new();

    /// <summary>Gets or sets the total count before response bounding.</summary>
    public int? TotalCount { get; set; }

    /// <summary>Gets or sets whether the response list was truncated.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Carries a structured result for debugger thread, frame, and local inspection.
/// </summary>
public sealed class DebuggerInspectionResponse
{
    /// <summary>Gets or sets whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a stable error code when the operation failed.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets an actionable result message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the Visual Studio process ID.</summary>
    public int? TargetProcessId { get; set; }

    /// <summary>Gets or sets the observed debugger mode.</summary>
    public string? DebuggerMode { get; set; }

    /// <summary>Gets or sets the ID of the current debugger thread, when available.</summary>
    public int? CurrentThreadId { get; set; }

    /// <summary>Gets or sets the total count before response bounding.</summary>
    public int? TotalCount { get; set; }

    /// <summary>Gets or sets debugger threads.</summary>
    public List<DebuggerThreadInfo> Threads { get; set; } = new();

    /// <summary>Gets or sets stack frames.</summary>
    public List<DebuggerFrameInfo> Frames { get; set; } = new();

    /// <summary>Gets or sets local variables.</summary>
    public List<DebuggerVariableInfo> Locals { get; set; } = new();

    /// <summary>Gets or sets whether the result list was truncated.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Describes one debugger thread.
/// </summary>
public sealed class DebuggerThreadInfo
{
    /// <summary>Gets or sets the operating system thread ID.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the debugger thread name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of stack frames.</summary>
    public int FrameCount { get; set; }

    /// <summary>Gets or sets whether this is the currently selected thread.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Describes one stack frame in the current thread.
/// </summary>
public sealed class DebuggerFrameInfo
{
    /// <summary>Gets or sets the one-based frame index in the current snapshot.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the function name.</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Gets or sets the module name.</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Gets or sets the source file when the debugger exposes it.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the source line when the debugger exposes it.</summary>
    public int Line { get; set; }

    /// <summary>Gets or sets the source language.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is the selected frame.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Describes one local variable and its bounded child values.
/// </summary>
public sealed class DebuggerVariableInfo
{
    /// <summary>Gets or sets the local or member name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the displayed value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the bounded member values.</summary>
    public List<DebuggerVariableInfo> Children { get; set; } = new();

    /// <summary>Gets or sets whether member expansion was limited.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Validates breakpoint request arguments before Visual Studio COM is called.
/// </summary>
public static class BreakpointRequestValidation
{
    /// <summary>Clamps member expansion depth to the supported safe range.</summary>
    public static int ClampMemberDepth(int depth) => Math.Clamp(depth, 0, 3);

    /// <summary>Clamps total locals and members to the supported response bound.</summary>
    public static int ClampMemberCount(int count) => Math.Clamp(count, 1, 40);

    /// <summary>
    /// Validates that a stack-frame reference still identifies the expected snapshot entry.
    /// </summary>
    public static bool MatchesFrameSnapshot(
        int index,
        int expectedFrameCount,
        int currentFrameCount,
        string? expectedFunction,
        string? currentFunction,
        string? expectedModule,
        string? currentModule) =>
        index >= 1 && expectedFrameCount == currentFrameCount &&
        !string.IsNullOrWhiteSpace(expectedFunction) &&
        string.Equals(expectedFunction, currentFunction, StringComparison.Ordinal) &&
        expectedModule is not null &&
        string.Equals(expectedModule, currentModule, StringComparison.Ordinal);

    /// <summary>
    /// Validates a source or function breakpoint request and its safety policies.
    /// </summary>
    public static (string? Code, string? Message) Validate(
        string? file,
        int line,
        string? function,
        string? condition,
        bool allowMutation,
        bool allowConditionEvaluation)
    {
        var hasFile = !string.IsNullOrWhiteSpace(file);
        var hasFunction = !string.IsNullOrWhiteSpace(function);
        if (hasFile == hasFunction)
            return ("invalid_location", "Specify exactly one source file or function name.");
        if (hasFile && (line < 1 || line > 1_000_000))
            return ("invalid_location", "A source breakpoint line must be between 1 and 1000000.");
        if (hasFunction && line != 0)
            return ("invalid_location", "Function breakpoints must use line 0.");
        if ((file?.Length ?? 0) > 2048 || (function?.Length ?? 0) > 2048)
            return ("invalid_location", "The breakpoint location exceeds the 2048 character limit.");
        if ((condition?.Length ?? 0) > 2048)
            return ("invalid_condition", "The breakpoint condition exceeds the 2048 character limit.");
        if (!allowMutation)
            return ("mutation_policy_required", "Set allowMutation=true to add a breakpoint.");
        if (!string.IsNullOrWhiteSpace(condition) && !allowConditionEvaluation)
            return ("condition_policy_required", "Conditions can execute debugger expressions. Set allowConditionEvaluation=true explicitly.");
        return (null, null);
    }

    /// <summary>
    /// Validates a removal request against a current list snapshot and its mutation policy.
    /// </summary>
    public static (string? Code, string? Message) ValidateRemoval(
        int id,
        string? expectedFile,
        int expectedLine,
        string? expectedFunction,
        string? expectedCondition,
        bool allowMutation)
    {
        if (!allowMutation)
            return ("mutation_policy_required", "Set allowMutation=true to remove a breakpoint.");
        if (id < 1)
            return ("invalid_breakpoint_id", "Breakpoint id must be a positive id from the latest list snapshot.");
        var hasFile = !string.IsNullOrWhiteSpace(expectedFile);
        var hasFunction = !string.IsNullOrWhiteSpace(expectedFunction);
        if (hasFile == hasFunction)
            return ("expected_location_required", "Pass exactly one expectedFile or expectedFunction from the latest breakpoint list.");
        if (hasFile && expectedLine < 1)
            return ("expected_location_required", "Pass the expected one-based source line from the latest breakpoint list.");
        if (expectedCondition is null)
            return ("expected_condition_required", "Pass expectedCondition from the latest breakpoint list. Use an empty string for no condition.");
        if ((expectedFile?.Length ?? 0) > 2048 || (expectedFunction?.Length ?? 0) > 2048 || expectedCondition.Length > 2048)
            return ("invalid_location", "Expected breakpoint fields exceed their size limits.");
        return (null, null);
    }
}
