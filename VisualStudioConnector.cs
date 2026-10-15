using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SystemProcess = System.Diagnostics.Process;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;
using VisualStudioMcpServer.Tools;

namespace VisualStudioMcpServer;

/// <summary>
/// Handles connection to Visual Studio via COM automation.
/// Supports VS2026 (DTE 18.0) and falls back to generic DTE.
/// </summary>
public static class VisualStudioConnector
{
    private static DTE2? _cachedDte;
    private static int? _targetProcessId;
    private static int? _cachedProcessId;
    private static long? _targetProcessStartTicks;

    /// <summary>
    /// Sets the target Visual Studio process ID.
    /// </summary>
    /// <param name="processId">The target PID. If null, requires one unique Visual Studio in the ROT.</param>
    public static void SetTargetProcessId(int? processId)
    {
        _targetProcessId = processId;
        _targetProcessStartTicks = processId.HasValue
            ? SystemProcess.GetProcessById(processId.Value).StartTime.ToUniversalTime().Ticks
            : null;
        _cachedDte = null;
        _cachedProcessId = null;
    }

    /// <summary>
    /// Sets the Visual Studio process identity used for debugger operations.
    /// </summary>
    /// <param name="processId">The Visual Studio process ID.</param>
    /// <param name="processStartTicks">The process start time in UTC ticks.</param>
    public static void SetTargetProcessIdentity(int processId, long processStartTicks)
    {
        _targetProcessId = processId;
        _targetProcessStartTicks = processStartTicks;
        _cachedDte = null;
        _cachedProcessId = null;
    }

    /// <summary>
    /// Gets the Visual Studio DTE from the ROT that matches the specified PID.
    /// </summary>
    public static DTE2? GetActiveVS()
    {
        if (_cachedDte != null)
        {
            try
            {
                _ = _cachedDte.Version;
                if (!_targetProcessId.HasValue || _cachedProcessId == _targetProcessId.Value) return _cachedDte;
            }
            catch
            {
                _cachedDte = null;
            }
        }

        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var runningObjectTable));
        runningObjectTable.EnumRunning(out var monikerEnumerator);
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var bindContext));
        var monikers = new IMoniker[1];
        var matches = new List<(int ProcessId, DTE2 Dte)>();
        while (monikerEnumerator.Next(1, monikers, IntPtr.Zero) == 0)
        {
            string displayName;
            try { monikers[0].GetDisplayName(bindContext, null, out displayName); }
            catch (COMException) { continue; }

            var match = Regex.Match(displayName, @"^!?VisualStudio\.DTE\.(?<version>\d+\.\d+):(?<pid>\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups["pid"].Value, out var pid)) continue;
            if (_targetProcessId.HasValue && pid != _targetProcessId.Value) continue;

            try
            {
                runningObjectTable.GetObject(monikers[0], out var candidate);
                if (candidate is DTE2 dte && (!_targetProcessId.HasValue || pid == _targetProcessId.Value))
                    matches.Add((pid, dte));
            }
            catch (COMException)
            {
                // Ignore candidates whose ROT registration ended during enumeration.
            }
        }

        if (matches.Count != 1) return null;
        _cachedDte = matches[0].Dte;
        _cachedProcessId = matches[0].ProcessId;
        return _cachedDte;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    /// <summary>
    /// Gets all output window pane names.
    /// </summary>
    public static List<string> GetOutputPaneNames()
    {
        var dte = GetActiveVS();
        if (dte == null) return new List<string>();

        var names = new List<string>();
        var outputWindow = dte.ToolWindows.OutputWindow;

        foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
        {
            names.Add(pane.Name);
            if (names.Count >= 100) break;
        }

        return names;
    }

    /// <summary>
    /// Reads content from a specific output pane.
    /// </summary>
    /// <param name="paneName">Name (or partial name) of the pane</param>
    /// <param name="maxLines">Maximum number of lines to return (from the end). 0 = all lines.</param>
    public static string? ReadOutputPane(string paneName, int maxLines = 50)
    {
        var dte = GetActiveVS();
        if (dte == null) return null;

        var outputWindow = dte.ToolWindows.OutputWindow;

        OutputWindowPane? matchingPane = null;
        foreach (OutputWindowPane pane in outputWindow.OutputWindowPanes)
        {
            if (pane.Name.Contains(paneName, StringComparison.OrdinalIgnoreCase))
            {
                matchingPane = pane;
                break;
            }
        }

        if (matchingPane == null) return null;

        var textDoc = matchingPane.TextDocument;
        var endPoint = textDoc.EndPoint;
        var firstLine = Math.Max(1, endPoint.Line - Math.Clamp(maxLines, 1, 2000) + 1);
        var startPoint = endPoint.CreateEditPoint();
        startPoint.MoveToLineAndOffset(firstLine, 1);
        var text = startPoint.GetText(endPoint);
        return firstLine > 1 ? $"[Truncated to the last {endPoint.Line - firstLine + 1} lines]\n{text}" : text;
    }

    /// <summary>
    /// Gets all items from the Error List.
    /// </summary>
    public static List<ErrorItem> GetErrorListItems()
    {
        var dte = GetActiveVS();
        if (dte == null) return new List<ErrorItem>();

        var items = new List<ErrorItem>();
        var errorList = dte.ToolWindows.ErrorList;
        var errorItems = errorList.ErrorItems;

        for (int i = 1; i <= Math.Min(errorItems.Count, 500); i++)
        {
            var item = errorItems.Item(i);
            items.Add(new ErrorItem
            {
                Severity = GetSeverityString(item),
                Description = item.Description,
                FileName = item.FileName,
                Line = item.Line,
                Column = item.Column,
                Project = item.Project
            });
        }

        return items;
    }

    private static string GetSeverityString(EnvDTE80.ErrorItem item)
    {
        // ErrorItem doesn't have a direct Severity property in all versions
        // We infer from the error code or default to "Error"
        try
        {
            var desc = item.Description?.ToLower() ?? "";
            if (desc.Contains("warning")) return "Warning";
            if (desc.Contains("message") || desc.Contains("info")) return "Message";
        }
        catch { }
        return "Error";
    }

    /// <summary>
    /// Gets information about the currently open solution.
    /// </summary>
    public static SolutionInfo? GetSolutionInfo()
    {
        var dte = GetActiveVS();
        if (dte?.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            return null;

        var info = new SolutionInfo
        {
            SolutionPath = dte.Solution.FullName,
            SolutionName = Path.GetFileName(dte.Solution.FullName),
            Projects = new List<string>()
        };

        foreach (Project project in dte.Solution.Projects)
        {
            if (!string.IsNullOrEmpty(project.Name))
            {
                info.Projects.Add(project.Name);
                if (info.Projects.Count >= 100) break;
            }
        }

        return info;
    }

    // ==================== BUILD & DEBUG CONTROL ====================

    /// <summary>
    /// Builds the solution.
    /// </summary>
    /// <param name="waitForBuild">If true, waits for build to complete</param>
    /// <returns>Build result info</returns>
    public static BuildResult BuildSolution(bool waitForBuild = true)
    {
        var dte = GetActiveVS();
        if (dte?.Solution == null)
            return new BuildResult { Success = false, Message = "No solution is open" };

        var solutionBuild = dte.Solution.SolutionBuild;

        // Start the build
        solutionBuild.Build(waitForBuild);

        if (waitForBuild)
        {
            return new BuildResult
            {
                Success = solutionBuild.LastBuildInfo == 0,
                ErrorCount = solutionBuild.LastBuildInfo,
                Message = solutionBuild.LastBuildInfo == 0 
                    ? "Build succeeded" 
                    : $"Build failed with {solutionBuild.LastBuildInfo} error(s)"
            };
        }

        return new BuildResult { Success = true, Message = "Build started" };
    }

    /// <summary>
    /// Gets the current debugger state.
    /// </summary>
    public static DebuggerState GetDebuggerState()
    {
        var dte = GetActiveVS();
        if (dte == null)
            return new DebuggerState { Mode = "Unknown", Message = "No VS instance found" };

        var mode = dte.Debugger.CurrentMode;
        var state = new DebuggerState
        {
            Mode = mode.ToString(),
            IsDebugging = mode != dbgDebugMode.dbgDesignMode,
            IsRunning = mode == dbgDebugMode.dbgRunMode,
            IsAtBreakpoint = mode == dbgDebugMode.dbgBreakMode
        };

        if (state.IsAtBreakpoint && dte.Debugger.CurrentStackFrame != null)
        {
            try
            {
                dynamic currentFrame = dte.Debugger.CurrentStackFrame;
                state.CurrentFunction = currentFrame.FunctionName;
                state.CurrentFile = Path.GetFileName((string)(currentFrame.File ?? ""));
                state.CurrentLine = (int)(currentFrame.LineCharOffset ?? 0);
            }
            catch { }
        }

        state.Message = state.Mode switch
        {
            "dbgDesignMode" => "Not debugging (Design mode)",
            "dbgRunMode" => "Running (no breakpoint)",
            "dbgBreakMode" => $"At breakpoint: {state.CurrentFunction} ({state.CurrentFile}:{state.CurrentLine})",
            _ => state.Mode
        };

        return state;
    }

    /// <summary>
    /// Starts debugging (F5).
    /// </summary>
    public static string StartDebugging()
    {
        var dte = GetActiveVS();
        if (dte == null) return "Error: No VS instance found";

        var mode = dte.Debugger.CurrentMode;
        if (mode == dbgDebugMode.dbgRunMode)
            return "Already running";
        if (mode == dbgDebugMode.dbgBreakMode)
        {
            dte.Debugger.Go(false); // Continue from breakpoint
            return "Continued from breakpoint";
        }

        dte.Debugger.Go(false);
        return "Started debugging";
    }

    /// <summary>
    /// Starts without debugging (Ctrl+F5).
    /// </summary>
    public static string StartWithoutDebugging()
    {
        var dte = GetActiveVS();
        if (dte == null) return "Error: No VS instance found";

        try
        {
            dte.ExecuteCommand("Debug.StartWithoutDebugging");
            return "Started without debugging";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Stops debugging.
    /// </summary>
    public static string StopDebugging()
    {
        var dte = GetActiveVS();
        if (dte == null) return "Error: No VS instance found";

        var mode = dte.Debugger.CurrentMode;
        if (mode == dbgDebugMode.dbgDesignMode)
            return "Not currently debugging";

        dte.Debugger.Stop(false);
        return "Stopped debugging";
    }

    /// <summary>
    /// Gets the call stack (when at a breakpoint).
    /// </summary>
    public static List<StackFrameInfo> GetCallStack()
    {
        var dte = GetActiveVS();
        var frames = new List<StackFrameInfo>();

        if (dte?.Debugger?.CurrentThread?.StackFrames == null)
            return frames;

        foreach (dynamic frame in dte.Debugger.CurrentThread.StackFrames)
        {
            try
            {
                frames.Add(new StackFrameInfo
                {
                    FunctionName = frame.FunctionName ?? "",
                    FileName = Path.GetFileName((string)(frame.File ?? "")),
                    LineNumber = (int)(frame.LineCharOffset ?? 0),
                    Language = frame.Language ?? ""
                });
            }
            catch { /* Some frames may not have file info */ }
        }

        return frames;
    }

    /// <summary>
    /// Lists breakpoints from the explicitly bound Visual Studio instance.
    /// </summary>
    public static BreakpointToolResponse ListBreakpoints()
    {
        try
        {
            var (dte, mode) = GetBoundDebugger();
            dynamic breakpoints = dte.Debugger.Breakpoints;
            var count = (int)breakpoints.Count;
            var items = new List<BreakpointInfo>();
            var returnedCount = Math.Min(count, 25);
            for (var index = 1; index <= returnedCount; index++)
            {
                dynamic breakpoint = breakpoints.Item(index);
                items.Add(ReadBreakpoint(index, breakpoint));
            }

            return new BreakpointToolResponse
            {
                Success = true,
                Code = "ok",
                Message = count == 0 ? "No breakpoints are configured." : $"Listed {items.Count} breakpoint(s).",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                Breakpoints = items,
                TotalCount = count,
                Truncated = count > returnedCount
            };
        }
        catch (BreakpointOperationException ex)
        {
            return CreateBreakpointError(ex.Code, ex.Message);
        }
        catch (COMException ex)
        {
            return CreateBreakpointError("com_error", $"Visual Studio rejected the breakpoint list request (HRESULT 0x{ex.HResult:X8}).");
        }
        catch (Exception ex)
        {
            return CreateBreakpointError("operation_failed", LimitDiagnostic(ex.Message));
        }
    }

    /// <summary>
    /// Adds a source or function breakpoint to the explicitly bound Visual Studio instance.
    /// </summary>
    public static BreakpointToolResponse SetBreakpoint(
        string? file,
        int line,
        string? function,
        string? condition,
        bool allowMutation,
        bool allowConditionEvaluation)
    {
        var validation = BreakpointRequestValidation.Validate(
            file, line, function, condition, allowMutation, allowConditionEvaluation);
        if (validation.Code is not null)
            return CreateBreakpointError(validation.Code, validation.Message ?? "The breakpoint request is invalid.");

        if ((file?.Length ?? 0) > 2048 || (function?.Length ?? 0) > 2048 || (condition?.Length ?? 0) > 2048)
            return CreateBreakpointError("invalid_location", "Breakpoint arguments exceed their size limits.");

        try
        {
            var (dte, mode) = GetBoundDebugger();
            dynamic breakpoints = dte.Debugger.Breakpoints;
            var sourceFile = string.IsNullOrWhiteSpace(file) ? string.Empty : file;
            var functionName = string.IsNullOrWhiteSpace(function) ? string.Empty : function;
            dte.Debugger.Breakpoints.Add(
                functionName,
                sourceFile,
                line,
                1,
                condition ?? string.Empty,
                dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                string.Empty,
                string.Empty,
                0,
                string.Empty,
                0,
                dbgHitCountType.dbgHitCountTypeNone);
            var index = (int)breakpoints.Count;
            dynamic added = breakpoints.Item(index);
            return new BreakpointToolResponse
            {
                Success = true,
                Code = "ok",
                Message = "Breakpoint added. List breakpoints to obtain its current snapshot ID.",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                Breakpoints = new List<BreakpointInfo> { ReadBreakpoint(index, added) },
                TotalCount = (int)breakpoints.Count
            };
        }
        catch (BreakpointOperationException ex)
        {
            return CreateBreakpointError(ex.Code, ex.Message);
        }
        catch (COMException ex)
        {
            return CreateBreakpointError("invalid_location", $"Visual Studio could not resolve the breakpoint location (HRESULT 0x{ex.HResult:X8}). Check the file, line, function, and condition for the selected project.");
        }
        catch (Exception ex)
        {
            return CreateBreakpointError("operation_failed", LimitDiagnostic(ex.Message));
        }
    }

    /// <summary>
    /// Removes the listed breakpoint after rechecking its location against the current snapshot.
    /// </summary>
    public static BreakpointToolResponse RemoveBreakpoint(
        int id,
        string? expectedFile,
        int expectedLine,
        string? expectedFunction,
        string? expectedCondition,
        bool allowMutation)
    {
        var validation = BreakpointRequestValidation.ValidateRemoval(
            id, expectedFile, expectedLine, expectedFunction, expectedCondition, allowMutation);
        if (validation.Code is not null)
            return CreateBreakpointError(validation.Code, validation.Message ?? "The breakpoint removal request is invalid.");

        try
        {
            var (dte, mode) = GetBoundDebugger();
            dynamic breakpoints = dte.Debugger.Breakpoints;
            var count = (int)breakpoints.Count;
            if (id > count)
                return CreateBreakpointError("stale_breakpoint_id", "The breakpoint id is no longer present. List breakpoints again.");

            dynamic breakpoint = breakpoints.Item(id);
            var current = ReadBreakpoint(id, breakpoint);
            if (!BreakpointMatches(current, expectedFile, expectedLine, expectedFunction, expectedCondition))
                return CreateBreakpointError("stale_breakpoint_id", "The breakpoint at this id changed since it was listed. List breakpoints again before removing it.");

            breakpoint.Delete();
            return new BreakpointToolResponse
            {
                Success = true,
                Code = "ok",
                Message = "Breakpoint removed.",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                Breakpoints = new List<BreakpointInfo> { current },
                TotalCount = count - 1
            };
        }
        catch (BreakpointOperationException ex)
        {
            return CreateBreakpointError(ex.Code, ex.Message);
        }
        catch (COMException ex)
        {
            return CreateBreakpointError("com_error", $"Visual Studio rejected the breakpoint removal request (HRESULT 0x{ex.HResult:X8}).");
        }
        catch (Exception ex)
        {
            return CreateBreakpointError("operation_failed", LimitDiagnostic(ex.Message));
        }
    }

    private static (DTE2 Dte, string Mode) GetBoundDebugger()
    {
        if (!_targetProcessId.HasValue || !_targetProcessStartTicks.HasValue)
            throw new BreakpointOperationException("target_identity_required", "Breakpoint tools require an explicit Visual Studio PID and process start time. Launch stdio with --parent-pid and --parent-start-ticks, or use VSIX-managed HTTP mode.");

        try
        {
            using var target = SystemProcess.GetProcessById(_targetProcessId.Value);
            if (target.HasExited || target.StartTime.ToUniversalTime().Ticks != _targetProcessStartTicks.Value)
                throw new BreakpointOperationException("stale_target", "The bound Visual Studio process has exited or its PID was reused.");
        }
        catch (ArgumentException)
        {
            throw new BreakpointOperationException("stale_target", "The bound Visual Studio process has exited.");
        }
        catch (InvalidOperationException)
        {
            throw new BreakpointOperationException("stale_target", "The bound Visual Studio process has exited.");
        }

        var dte = GetActiveVS();
        if (dte is null || _cachedProcessId != _targetProcessId)
            throw new BreakpointOperationException("target_not_registered", "The bound Visual Studio instance is not available in the Running Object Table.");

        try
        {
            var mode = dte.Debugger.CurrentMode.ToString();
            if (string.IsNullOrWhiteSpace(mode))
                throw new BreakpointOperationException("debugger_state_unavailable", "Visual Studio returned no debugger state.");
            return (dte, mode);
        }
        catch (COMException)
        {
            throw new BreakpointOperationException("debugger_state_unavailable", "Could not read debugger state from the bound Visual Studio instance.");
        }
    }

    private static BreakpointInfo ReadBreakpoint(int id, dynamic breakpoint)
    {
        return new BreakpointInfo
        {
            Id = id,
            File = ReadBoundedString(() => (string?)breakpoint.File),
            Line = ReadInteger(() => (int)breakpoint.FileLine),
            Function = ReadBoundedString(() => (string?)breakpoint.FunctionName),
            Condition = ReadBoundedString(() => (string?)breakpoint.Condition),
            Enabled = ReadBoolean(() => (bool)breakpoint.Enabled),
            HitCount = ReadNullableInteger(() => (int)breakpoint.CurrentHits)
        };
    }

    private static bool BreakpointMatches(
        BreakpointInfo current,
        string? expectedFile,
        int expectedLine,
        string? expectedFunction,
        string? expectedCondition)
    {
        if (!string.IsNullOrWhiteSpace(expectedFile) &&
            (!string.Equals(current.File, expectedFile, StringComparison.OrdinalIgnoreCase) || current.Line != expectedLine))
            return false;
        if (!string.IsNullOrWhiteSpace(expectedFunction) &&
            !string.Equals(current.Function, expectedFunction, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(current.Condition, expectedCondition, StringComparison.Ordinal);
    }

    private static string ReadBoundedString(Func<string?> read)
    {
        try
        {
            var value = read() ?? string.Empty;
            return value.Length <= 512 ? value : value[..512];
        }
        catch { return string.Empty; }
    }

    private static int ReadInteger(Func<int> read)
    {
        try { return read(); } catch { return 0; }
    }

    private static int? ReadNullableInteger(Func<int> read)
    {
        try { return read(); } catch { return null; }
    }

    private static bool ReadBoolean(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }

    private static BreakpointToolResponse CreateBreakpointError(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = LimitDiagnostic(message),
        TargetProcessId = _targetProcessId
    };

    private static string LimitDiagnostic(string message) =>
        message.Length <= 1024 ? message : message[..1024];

    /// <summary>
    /// Lists threads in the explicitly bound Visual Studio instance while execution is paused.
    /// </summary>
    public static DebuggerInspectionResponse ListDebuggerThreads()
    {
        try
        {
            var (dte, mode) = GetPausedDebugger();
            dynamic threads = dte.Debugger.CurrentProgram.Threads;
            dynamic currentThread = dte.Debugger.CurrentThread;
            var count = (int)threads.Count;
            var items = new List<DebuggerThreadInfo>();
            var returned = Math.Min(count, 64);
            for (var index = 1; index <= returned; index++)
            {
                dynamic thread = threads.Item(index);
                var id = (int)thread.ID;
                items.Add(new DebuggerThreadInfo
                {
                    Id = id,
                    Name = ReadBoundedString(() => (string?)thread.Name),
                    FrameCount = ReadInteger(() => (int)thread.StackFrames.Count),
                    IsCurrent = id == ReadInteger(() => (int)currentThread.ID)
                });
            }

            return new DebuggerInspectionResponse
            {
                Success = true,
                Code = "ok",
                Message = $"Listed {items.Count} thread(s).",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                Threads = items,
                TotalCount = count,
                Truncated = count > returned
            };
        }
        catch (BreakpointOperationException ex) { return CreateInspectionError(ex.Code, ex.Message); }
        catch (COMException ex) { return CreateInspectionError("com_error", $"Visual Studio rejected the thread inspection request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return CreateInspectionError("operation_failed", LimitDiagnostic(ex.Message)); }
    }

    /// <summary>
    /// Lists stack frames in the current thread while execution is paused.
    /// </summary>
    public static DebuggerInspectionResponse GetDebuggerStack()
    {
        try
        {
            var (dte, mode) = GetPausedDebugger();
            dynamic frames = dte.Debugger.CurrentThread.StackFrames;
            var count = (int)frames.Count;
            var items = new List<DebuggerFrameInfo>();
            var returned = Math.Min(count, 128);
            dynamic currentFrame = dte.Debugger.CurrentStackFrame;
            dynamic currentThread = dte.Debugger.CurrentThread;
            var currentFunction = ReadBoundedString(() => (string?)currentFrame.FunctionName);
            var currentModule = ReadBoundedString(() => (string?)currentFrame.Module);
            var markedCurrent = false;
            for (var index = 1; index <= returned; index++)
            {
                dynamic frame = frames.Item(index);
                var frameInfo = ReadDebuggerFrame(index, frame, false);
                frameInfo.IsCurrent = !markedCurrent &&
                    string.Equals(frameInfo.Function, currentFunction, StringComparison.Ordinal) &&
                    string.Equals(frameInfo.Module, currentModule, StringComparison.Ordinal);
                markedCurrent |= frameInfo.IsCurrent;
                items.Add(frameInfo);
            }

            return new DebuggerInspectionResponse
            {
                Success = true,
                Code = "ok",
                Message = $"Listed {items.Count} frame(s) for the current thread.",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                CurrentThreadId = ReadInteger(() => (int)currentThread.ID),
                Frames = items,
                TotalCount = count,
                Truncated = count > returned
            };
        }
        catch (BreakpointOperationException ex) { return CreateInspectionError(ex.Code, ex.Message); }
        catch (COMException ex) { return CreateInspectionError("com_error", $"Visual Studio rejected the stack inspection request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return CreateInspectionError("operation_failed", LimitDiagnostic(ex.Message)); }
    }

    /// <summary>
    /// Selects a debugger thread after resolving its current process thread ID.
    /// </summary>
    public static DebuggerInspectionResponse SelectDebuggerThread(int threadId)
    {
        if (threadId <= 0) return CreateInspectionError("invalid_thread_id", "Thread ID must be a positive ID from the current thread list.");
        try
        {
            var (dte, mode) = GetPausedDebugger();
            dynamic threads = dte.Debugger.CurrentProgram.Threads;
            var count = (int)threads.Count;
            for (var index = 1; index <= Math.Min(count, 512); index++)
            {
                dynamic thread = threads.Item(index);
                if ((int)thread.ID != threadId) continue;
                dte.Debugger.CurrentThread = (EnvDTE.Thread)thread;
                return new DebuggerInspectionResponse
                {
                    Success = true,
                    Code = "ok",
                    Message = $"Selected thread {threadId}.",
                    TargetProcessId = _targetProcessId,
                    DebuggerMode = dte.Debugger.CurrentMode.ToString(),
                    Threads = new List<DebuggerThreadInfo> { ReadDebuggerThread(threadId, thread, true) }
                };
            }

            return CreateInspectionError("stale_thread_id", "Thread ID is not present in the current paused process. List threads again.");
        }
        catch (BreakpointOperationException ex) { return CreateInspectionError(ex.Code, ex.Message); }
        catch (COMException ex) { return CreateInspectionError("com_error", $"Visual Studio rejected the thread selection request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return CreateInspectionError("operation_failed", LimitDiagnostic(ex.Message)); }
    }

    /// <summary>
    /// Selects a stack frame after checking its function name against the current snapshot.
    /// </summary>
    public static DebuggerInspectionResponse SelectDebuggerFrame(
        int index,
        string? expectedFunction,
        string? expectedModule,
        int expectedFrameCount)
    {
        if (index < 1 || index > 128 || string.IsNullOrWhiteSpace(expectedFunction) || expectedFunction.Length > 2048 ||
            expectedModule is null || expectedModule.Length > 512 || expectedFrameCount < 1)
            return CreateInspectionError("invalid_frame", "Pass the positive frame index, function, module, and total frame count from the latest stack response.");
        try
        {
            var (dte, mode) = GetPausedDebugger();
            dynamic frames = dte.Debugger.CurrentThread.StackFrames;
            var count = (int)frames.Count;
            if (index > count) return CreateInspectionError("stale_frame", "The frame index is no longer present. Get the stack again.");
            dynamic frame = frames.Item(index);
            var current = ReadDebuggerFrame(index, frame, false);
            if (!BreakpointRequestValidation.MatchesFrameSnapshot(index, expectedFrameCount, count,
                expectedFunction, current.Function, expectedModule, current.Module))
                return CreateInspectionError("stale_frame", "The stack changed since it was listed. Get the stack again before selecting a frame.");

            dte.Debugger.CurrentStackFrame = (StackFrame)frame;
            return new DebuggerInspectionResponse
            {
                Success = true,
                Code = "ok",
                Message = $"Selected frame {index} ({current.Function}).",
                TargetProcessId = _targetProcessId,
                DebuggerMode = dte.Debugger.CurrentMode.ToString(),
                Frames = new List<DebuggerFrameInfo> { new()
                    {
                        Index = current.Index,
                        Function = current.Function,
                        Module = current.Module,
                        File = current.File,
                        Line = current.Line,
                        Language = current.Language,
                        IsCurrent = true
                    } }
            };
        }
        catch (BreakpointOperationException ex) { return CreateInspectionError(ex.Code, ex.Message); }
        catch (COMException ex) { return CreateInspectionError("com_error", $"Visual Studio rejected the frame selection request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return CreateInspectionError("operation_failed", LimitDiagnostic(ex.Message)); }
    }

    /// <summary>
    /// Reads locals from a snapshot-validated frame with bounded member expansion.
    /// </summary>
    public static DebuggerInspectionResponse GetDebuggerLocals(
        int frameIndex,
        string? expectedFunction,
        string? expectedModule,
        int expectedFrameCount,
        int expectedThreadId,
        int maxDepth,
        int maxItems,
        bool allowMemberExpansion)
    {
        if (frameIndex < 1 || frameIndex > 128 || string.IsNullOrWhiteSpace(expectedFunction) || expectedFunction.Length > 2048 ||
            expectedModule is null || expectedModule.Length > 512 || expectedFrameCount < 1 || expectedThreadId <= 0)
            return CreateInspectionError("invalid_frame", "Pass the frame index, function, module, total frame count, and thread ID from the latest debugger snapshot.");
        maxDepth = BreakpointRequestValidation.ClampMemberDepth(maxDepth);
        maxItems = BreakpointRequestValidation.ClampMemberCount(maxItems);
        try
        {
            var (dte, mode) = GetPausedDebugger();
            var currentThreadId = (int)dte.Debugger.CurrentThread.ID;
            if (currentThreadId != expectedThreadId)
                return CreateInspectionError("stale_thread", "The selected thread changed since it was listed. List threads and the stack again.");
            dynamic frames = dte.Debugger.CurrentThread.StackFrames;
            var count = (int)frames.Count;
            if (frameIndex > count) return CreateInspectionError("stale_frame", "The frame index is no longer present. Get the stack again.");
            dynamic frame = frames.Item(frameIndex);
            var frameInfo = ReadDebuggerFrame(frameIndex, frame, false);
            if (!BreakpointRequestValidation.MatchesFrameSnapshot(frameIndex, expectedFrameCount, count,
                expectedFunction, frameInfo.Function, expectedModule, frameInfo.Module))
                return CreateInspectionError("stale_frame", "The stack changed since it was listed. Get the stack again before reading locals.");

            dynamic locals = frame.Locals;
            var localCount = (int)locals.Count;
            var returned = Math.Min(localCount, maxItems);
            var budget = maxItems;
            var items = new List<DebuggerVariableInfo>();
            for (var index = 1; index <= returned && budget > 0; index++)
            {
                dynamic local = locals.Item(index);
                items.Add(ReadDebuggerVariable(local, maxDepth, allowMemberExpansion, ref budget));
            }

            return new DebuggerInspectionResponse
            {
                Success = true,
                Code = "ok",
                Message = $"Read {items.Count} local(s) from frame {frameIndex}.",
                TargetProcessId = _targetProcessId,
                DebuggerMode = mode,
                CurrentThreadId = currentThreadId,
                Locals = items,
                TotalCount = localCount,
                Truncated = localCount > returned || budget == 0 || items.Any(item => item.Truncated)
            };
        }
        catch (BreakpointOperationException ex) { return CreateInspectionError(ex.Code, ex.Message); }
        catch (COMException ex) { return CreateInspectionError("com_error", $"Visual Studio rejected the local inspection request (HRESULT 0x{ex.HResult:X8})."); }
        catch (Exception ex) { return CreateInspectionError("operation_failed", LimitDiagnostic(ex.Message)); }
    }

    private static (DTE2 Dte, string Mode) GetPausedDebugger()
    {
        var (dte, mode) = GetBoundDebugger();
        if (!mode.Equals("dbgBreakMode", StringComparison.OrdinalIgnoreCase))
            throw new BreakpointOperationException("not_paused", "The Visual Studio debugger must be paused at a break event for thread, frame, or local inspection.");
        return (dte, mode);
    }

    private static DebuggerThreadInfo ReadDebuggerThread(int id, dynamic thread, bool isCurrent) => new()
    {
        Id = id,
        Name = ReadBoundedString(() => (string?)thread.Name),
        FrameCount = ReadInteger(() => (int)thread.StackFrames.Count),
        IsCurrent = isCurrent
    };

    private static DebuggerFrameInfo ReadDebuggerFrame(int index, dynamic frame, bool isCurrent) => new()
    {
        Index = index,
        Function = ReadBoundedString(() => (string?)frame.FunctionName),
        Module = ReadBoundedString(() => (string?)frame.Module),
        File = ReadVariableString(() => (string?)frame.File),
        Line = ReadInteger(() => (int)frame.LineCharOffset),
        Language = ReadBoundedString(() => (string?)frame.Language),
        IsCurrent = isCurrent
    };

    private static DebuggerVariableInfo ReadDebuggerVariable(dynamic expression, int depth, bool allowMemberExpansion, ref int remaining)
    {
        remaining--;
        var result = new DebuggerVariableInfo
        {
            Name = ReadVariableString(() => (string?)expression.Name),
            Type = ReadVariableString(() => (string?)expression.Type),
            Value = ReadVariableString(() => (string?)expression.Value)
        };
        if (!allowMemberExpansion || depth <= 0 || remaining <= 0) return result;

        try
        {
            dynamic members = expression.DataMembers;
            var count = (int)members.Count;
            var returned = Math.Min(count, remaining);
            for (var index = 1; index <= returned && remaining > 0; index++)
            {
                dynamic member = members.Item(index);
                result.Children.Add(ReadDebuggerVariable(member, depth - 1, allowMemberExpansion, ref remaining));
            }
            result.Truncated = count > returned || remaining == 0;
        }
        catch (COMException)
        {
            result.Truncated = true;
        }

        return result;
    }

    private static DebuggerInspectionResponse CreateInspectionError(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = LimitDiagnostic(message),
        TargetProcessId = _targetProcessId
    };

    private static string ReadVariableString(Func<string?> read)
    {
        var value = ReadBoundedString(read);
        return value.Length <= 256 ? value : value[..256];
    }

    private sealed class BreakpointOperationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

public class BuildResult
{
    public bool Success { get; set; }
    public int ErrorCount { get; set; }
    public string Message { get; set; } = "";
}

public class DebuggerState
{
    public string Mode { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsDebugging { get; set; }
    public bool IsRunning { get; set; }
    public bool IsAtBreakpoint { get; set; }
    public string? CurrentFunction { get; set; }
    public string? CurrentFile { get; set; }
    public int CurrentLine { get; set; }
}

public class StackFrameInfo
{
    public string FunctionName { get; set; } = "";
    public string FileName { get; set; } = "";
    public int LineNumber { get; set; }
    public string Language { get; set; } = "";
}

public class ErrorItem
{
    public string Severity { get; set; } = "Error";
    public string Description { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Project { get; set; } = "";

    public override string ToString()
    {
        return $"[{Severity}] {Description} ({Path.GetFileName(FileName)}:{Line})";
    }
}

public class SolutionInfo
{
    public string SolutionPath { get; set; } = "";
    public string SolutionName { get; set; } = "";
    public List<string> Projects { get; set; } = new();
}
