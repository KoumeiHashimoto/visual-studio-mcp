# Visual Studio MCP Bridge

An MCP server fork for Visual Studio 2022/2026. The v1.0 HTTP mode is intended to be launched and owned by the Visual Studio Debug MCP VSIX. It binds to loopback, authenticates every request, and targets the Visual Studio process ID supplied by its parent. The original stdio mode remains available for direct client launches.

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

While the debugger is paused, `list_debugger_threads` lists up to 64 threads, `get_call_stack` lists up to 128 frames for the current thread, `select_debugger_thread` selects a live thread ID, and `select_debugger_frame` selects a one-based frame after checking its function name against the latest stack snapshot. `get_debugger_locals` requires the frame index and expected function from that snapshot. Stale indexes and a changed debugger state return structured errors.

Local inspection returns at most 40 values, limits nested expansion to depth 3, and truncates each displayed name, type, or value to 256 characters. Member expansion is off by default; set `allowMemberExpansion=true` to request it. C++ Natvis and managed debugger visualizers can expose different member sets, and values beyond the limits are intentionally omitted. The server does not retain COM references between requests.

`evaluate_expression` requires `allowEvaluation=true`, a paused frame identity from the latest stack snapshot, and a current thread ID. Evaluation has a 5 second debugger timeout and a 15 second STA wait bound; the returned value is capped at 4096 characters. Automatic property and function evaluation is disabled by default. In that mode, a conservative syntax check rejects function calls, assignments, increments, statements, and object construction. Set `allowSideEffects=true` only when code execution or state changes from evaluation are intended. Visual Studio documents that expressions, property evaluation, and implicit function calls can have side effects; this API keeps the caller in control and does not attempt to classify every language expression perfectly.

### VSIX-managed Streamable HTTP

The VSIX starts the executable in this form:

```text
VisualStudioMcpServer.exe http --parent-pid <PID> --parent-start-ticks <UTC process start ticks> --endpoint-file <ACL-protected path>
```

The server verifies both the parent PID and process start time, creates a random 256-bit bearer token, binds Kestrel to an OS-assigned port on `127.0.0.1`, and writes the endpoint URL and token to the protected endpoint descriptor. The MCP endpoint is `/mcp`; the authenticated VSIX shutdown endpoint is `/_vsix/shutdown`. Parent-process exit stops the listener.

HTTP exposes only these v1.0 read-only tools:

- `get_solution_info`
- `get_error_list`
- `list_output_panes`
- `read_output_pane`

HTTP requests without a valid bearer token receive 401. A non-loopback Host/connection or an Origin other than absent or HTTP localhost/loopback receives 403. The bearer token is not placed in process arguments or logs.

## Visual Studio binding and COM

The server enumerates the Running Object Table and parses the PID from Visual Studio DTE monikers. HTTP mode accepts only the DTE registered for the supplied parent PID; stdio mode accepts a unique match only. Breakpoint tools additionally require the supplied PID and process-start time to remain current on each operation. They do not use generic `GetActiveObject` to choose an arbitrary Visual Studio instance.

COM calls execute on a dedicated STA worker thread. Rejected calls are retried for a bounded interval, and calls have a finite wait limit. Tool output, request body size, and enumerated member counts are bounded.

## Debugger validation checks

Run the argument and safety-policy checks with:

```powershell
dotnet run --project .\tests\BreakpointValidation\BreakpointValidation.csproj
```

These checks cover request policies and bounds; they do not exercise COM or replace the required native C++ and C# Visual Studio 2026 sessions.

## License

MIT License. See [LICENSE](LICENSE).
