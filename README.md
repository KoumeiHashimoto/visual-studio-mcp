# Visual Studio MCP Bridge

An MCP server fork for Visual Studio 2022/2026. VSIX-managed HTTP is launched and owned by one Visual Studio Debug MCP VSIX instance. It binds to loopback, authenticates every request, and targets the Visual Studio process identity supplied by its parent. The original stdio mode remains available for direct client launches.

The original upstream is [alon-mini/visual-studio-mcp](https://github.com/alon-mini/visual-studio-mcp). This fork preserves its MIT license and attribution. Changes for HTTP hosting and instance binding belong in this fork.

## Build

Requirements: Windows x64, .NET SDK 10, and the .NET 10 runtime for framework-dependent execution.

```powershell
dotnet restore .\VisualStudioMcpServer.csproj
dotnet build .\VisualStudioMcpServer.csproj -c Release -f net8.0 --no-restore
dotnet build .\VisualStudioMcpServer.csproj -c Release -f net10.0 --no-restore
```

The VSIX packages the `net10.0/win-x64` publish output. Its parent repository has a transport smoke test at `scripts/Smoke-Test-Transports.ps1`.

## Run modes

### stdio

Run `VisualStudioMcpServer.exe` with no arguments (or `stdio`). The server uses MCP stdio and discovers all tool types in the assembly. The original tools include read-only solution, Error List, and Output Window inspection, plus existing build/debug operations. Treat this mode as trusted local client access. COM lookup requires exactly one matching Visual Studio instance in the ROT because stdio has no VSIX-supplied process ID.

Breakpoint tools require an explicit process identity so they cannot choose a Visual Studio instance on the client's behalf. Launch stdio with the `devenv.exe` PID and its UTC process-start ticks:

```text
VisualStudioMcpServer.exe stdio --parent-pid <PID> --parent-start-ticks <UTC process start ticks>
```

`list_breakpoints` is read-only and returns at most 25 entries. `set_breakpoint` and `remove_breakpoint` require `allowMutation=true`. A condition expression also requires `allowConditionEvaluation=true`. Removal requires the location and condition fields returned by the latest list response; a changed list entry is rejected as stale. Breakpoint IDs are one-based indexes and can change after edits. Results use structured JSON with an error code and message.

While the debugger is paused, `list_debugger_threads` lists up to 64 threads, `get_call_stack` lists up to 128 frames for the current thread, `select_debugger_thread` selects a live thread ID, and `select_debugger_frame` selects a one-based frame after checking its function name against the latest stack snapshot. `get_debugger_locals` requires the frame index and expected function from that snapshot. Stale indexes and a changed debugger state return structured errors. EnvDTE.StackFrame does not expose source coordinates; empty file names and zero line numbers mean unavailable coordinates.

Local inspection returns at most 40 values, limits nested expansion to depth 3, and truncates each displayed name, type, or value to 256 characters. Member expansion is off by default; set `allowMemberExpansion=true` to request it. C++ Natvis and managed debugger visualizers can expose different member sets, and values beyond the limits are intentionally omitted. The server does not retain COM references between requests.

`evaluate_expression` requires `allowEvaluation=true`, a paused frame identity from the latest stack snapshot, and a current thread ID. The server explicitly queries EnvDTE100.Debugger5 before calling GetExpression3; an unavailable interface returns evaluation_not_supported without a fallback that could enable function evaluation. Evaluation has a 5 second debugger timeout and a 15 second STA wait bound; the returned value is capped at 4096 characters. Automatic property and function evaluation is disabled by default. In that mode, a conservative syntax check rejects function calls, assignments, increments, statements, and object construction. Set `allowSideEffects=true` only when code execution or state changes from evaluation are intended. Visual Studio documents that expressions, property evaluation, and implicit function calls can have side effects; this API keeps the caller in control and does not attempt to classify every language expression perfectly.

`control_debugger` supports `pause`, `continue`, `step_into`, `step_over`, and `step_out`. Every action requires `allowDebuggerControl=true` and uses the explicitly bound Visual Studio PID. Pause is accepted only while the debugger is running; continue and step actions are accepted only while paused. Control requests are serialized. The server subscribes to retained debugger COM events before issuing the non-waiting EnvDTE command, pumps STA messages, and polls the bound target every 25 ms. A step completes on a break event even when the function name is unchanged or a brief running state is missed by polling. The wait is clamped to 1 to 10 seconds (5 seconds by default) and honors MCP request cancellation. Expired queued commands are skipped. Event cleanup remains queued on the STA after a caller times out, and the control gate stays held until cleanup completes; normal cleanup is awaited for up to 3 additional seconds. Cancellation after dispatch does not reverse an already-sent action, which may still finish.

### VSIX-managed Streamable HTTP

The VSIX starts the executable in this form:

```text
VisualStudioMcpServer.exe http --parent-pid <PID> --parent-start-ticks <UTC process start ticks> --endpoint-file <ACL-protected path>
```

The server verifies both the parent PID and process start time, creates a random 256-bit bearer token, binds Kestrel to an OS-assigned port on `127.0.0.1`, and writes the endpoint URL and token to the protected endpoint descriptor. The MCP endpoint is `/mcp`; the authenticated VSIX shutdown endpoint is `/_vsix/shutdown`. Parent-process exit stops the listener.

Authenticated stateful HTTP exposes the following bounded solution, debugger, build, inventory, and file-registration tools:

- `get_solution_info`
- `get_error_list`
- `get_diagnostics` (bounded snapshot pages; exact project identity and richer provenance may be unavailable)
- `list_output_panes`
- `read_output_pane`
- `read_output_since` (bounded snapshot fallback; does not claim incremental delta continuity)
- `list_breakpoints`, `set_breakpoint`, `remove_breakpoint`
- `list_debugger_threads`, `get_call_stack`, `select_debugger_thread`, `select_debugger_frame`, `get_debugger_locals`
- `evaluate_expression`, `control_debugger`
- `build_solution`, `rebuild_solution`, `clean_solution`, `get_build_status`
- `list_solution_projects`, `get_project_info`, `list_project_files`
- `open_project_file`, `add_project_file`, `remove_project_file`

The VSIX supplies an owner bearer token that is restricted to owner routes and the legacy stdio-compatible HTTP path. The `/mcp` endpoint uses the MCP SDK's stateful HTTP transport. A one-use connection key admits one MCP session; each request must authenticate with that key and its bound `Mcp-Session-Id`. The key is not accepted by owner routes or shutdown. Breakpoint changes, expression evaluation, member expansion, execution control, builds, and project registration retain explicit per-request policy flags. Authentication, loopback binding, Host and Origin validation, and the parent-process PID/start-time identity check apply to the tools. Legacy `start_debugging` and `stop_debugging` remain available only through stdio.

Build tools require `allowBuild=true`, a matching absolute `expectedSolutionPath`, a live bound Visual Studio process, and design mode. Project registration requires `allowProjectMutation=true`, a matching solution and project path, design mode, no build in progress, and a clean project/source file. `remove_project_file` uses project-system item removal and verifies the physical file remains. Project inventory captures solution registrations only; it never walks arbitrary filesystem paths. Snapshots contain no COM references and are capped by count, size, traversal depth, node count, lifetime, and response length. `open_project_file` opens the captured item in the text editor and does not save or reload an existing dirty buffer.

HTTP requests without a valid owner token or admitted connection key receive 401. A non-loopback Host/connection or an Origin other than absent or HTTP localhost/loopback receives 403. Owner tokens and connection keys are not placed in process arguments or logs. The HTTP allowlist contains 27 unique tool names. A registered tool may still report `dte_unavailable` when no VSIX-owned in-process provider is connected; registration does not establish provider availability.

## Visual Studio binding and COM

The server enumerates the Running Object Table and parses the PID from Visual Studio DTE monikers. HTTP mode accepts only the DTE registered for the supplied parent PID; stdio mode accepts a unique match only. Breakpoint tools additionally require the supplied PID and process-start time to remain current on each operation. They do not use generic `GetActiveObject` to choose an arbitrary Visual Studio instance.

COM calls execute on a dedicated STA worker thread. Rejected calls are retried for a bounded interval, and calls have a finite wait limit. Tool output, request body size, and enumerated member counts are bounded.

## Debugger validation checks

Run the argument and safety-policy checks with:

```powershell
dotnet run --project .\tests\BreakpointValidation\BreakpointValidation.csproj
```

The 38 policy, bounds, and event-completion checks also run without Visual Studio. On Windows, 5 additional STA dispatcher checks cover apartment selection, exception preservation, expired queued work, and guaranteed cleanup. These dispatcher checks do not exercise live Visual Studio COM calls or replace the required native C++ and C# Visual Studio 2026 sessions.

## v1.2 report corrections

The four legacy read-only tools preserve their successful output formats. Dispatch failures now return JSON with `Success=false`, a stable `Code`, and an actionable `Message`. Codes include `dte_unavailable`, `no_solution` (solution information only), `dte_disconnected`, `com_busy`, `com_timeout`, `com_error`, and `operation_failed`. Exception diagnostics include only the tool identifier, exception type and HRESULT; exception text and stacks are deliberately omitted to avoid leaking credentials or user data. This improves diagnosis; it does not establish the cause of a historical failure that stopped reproducing after an IDE restart.

Thread listing now includes `CurrentThreadId`, using the same observed ID for every `IsCurrent` flag. Locals and expression evaluation reject missing thread IDs with `thread_not_found` and existing non-current IDs with `thread_not_current`, including selection guidance. At most 512 threads are searched; an inconclusive capped search returns `thread_lookup_incomplete`. These checks do not select a thread or evaluate an expression.

The frame provider reads `EnvDTE90a.StackFrame2.FileName` and `LineNumber` without selecting a frame. Complete, bounded coordinates return `SourceLocationAvailable=true` with a null reason. Other frames return `File=null`, `Line=null`, `SourceLocationAvailable=false`, and a stable `SourceLocationUnavailableReason`: `source_location_interface_unavailable`, `source_location_not_reported`, `source_location_com_error`, or `source_location_exceeds_limit`. Missing coordinates do not by themselves imply missing symbols. Source paths are capped at 2048 characters and line numbers must fit a positive Int32. See the [StackFrame2 API](https://learn.microsoft.com/en-us/dotnet/api/envdte90a.stackframe2?view=visualstudiosdk-2022).

Frame `Language` is a stable identifier: `cpp`, `csharp`, `basic`, `fsharp`, `javascript`, `typescript`, `python`, `sql`, or `unknown`. `LanguageDisplayName` retains the original IDE label. Unsupported/localized labels normalize to `unknown`. Clients that consumed empty strings/zero coordinates or raw language labels should use the explicit availability fields and display label instead.

Additional regression harnesses:

```powershell
dotnet run --project .\tests\DebuggerMetadataValidation -c Release
dotnet run --project .\tests\ReadOnlyToolValidation -c Release
```

The metadata harness checks three cultures and serialized null coordinates. The read-only harness injects failures into the actual tool entry points and Windows STA dispatcher with a fake connector; it does not reproduce a physical DTE disconnection.

## License

MIT License. See [LICENSE](LICENSE).
