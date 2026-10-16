using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualStudioMcpServer;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using VisualStudioMcpServer.Tools;
using VisualStudioMcpServer.ConnectionKeys;

[assembly: SupportedOSPlatform("windows")]

/// <summary>
/// Starts the stdio and HTTP hosts for the Visual Studio MCP server.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the MCP server.
    /// </summary>
    /// <param name="args">The startup mode and Visual Studio owner information.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0].Equals("stdio", StringComparison.OrdinalIgnoreCase))
            {
                await RunStdioAsync(args.Length == 0 ? Array.Empty<string>() : args[1..]);
                return 0;
            }

            if (args[0].Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                await RunHttpAsync(args[1..]);
                return 0;
            }

            Console.Error.WriteLine("Usage: VisualStudioMcpServer.exe [stdio [--parent-pid PID --parent-start-ticks TICKS] | http --parent-pid PID --parent-start-ticks TICKS --endpoint-file PATH]");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Starts the stdio transport.
    /// </summary>
    private static async Task RunStdioAsync(string[] args)
    {
        var targetIdentity = HttpOptions.ParseOptionalParentIdentity(args);
        if (targetIdentity.HasValue)
            VisualStudioConnector.SetTargetProcessIdentity(targetIdentity.Value.ProcessId, targetIdentity.Value.StartTicks);
        StaComDispatcher.Start();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(GetSolutionInfoTool).Assembly);

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// Starts the authenticated loopback HTTP host managed by the VSIX.
    /// </summary>
    /// <param name="args">The parent process identity and endpoint descriptor path.</param>
    private static async Task RunHttpAsync(string[] args)
    {
        var options = HttpOptions.Parse(args);
        using var parent = Process.GetProcessById(options.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks)
        {
            throw new InvalidOperationException("The specified Visual Studio process identity is stale.");
        }

        VisualStudioConnector.SetTargetProcessIdentity(options.ParentPid, options.ParentStartTicks);
        StaComDispatcher.Start();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var serverInstanceId = ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        ConnectionKeyStore? connectionKeys = null;
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
#pragma warning disable MCP9006 // Issue #43 pins the legacy stateful session contract for native SDK clients.
            .WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateful;
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.MaxIdleSessionCount = 16;
            })
#pragma warning restore MCP9006
            .WithTools<ReadOnlyHttpTools>()
            .WithTools<DebuggerHttpTools>()
            .WithTools<BuildOperationTools>()
            .WithTools<ProjectInventoryTools>()
            .WithTools<ProjectFileActionTools>();

        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local) ||
                context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var host = context.Request.Host.Host;
            if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                (!IPAddress.TryParse(host, out var hostAddress) || !IPAddress.IsLoopback(hostAddress)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (!IsAllowedOrigin(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var authorization = context.Request.Headers.Authorization.ToString();
            if (context.Request.Headers.Authorization.Count != 1 || authorization.Length > 256 || authorization.Any(character => character > 0x7f))
            {
                var containsOwnerCredential = context.Request.Headers.Authorization.Any(value =>
                    value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                    TokenEquals(token, value[7..].Trim()));
                if (!containsOwnerCredential) connectionKeys?.RecordFailedKeyAuth();
                var limited = connectionKeys?.IsKeyAuthRateLimited() == true;
                context.Response.StatusCode = limited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
                if (limited) context.Response.Headers.RetryAfter = "5";
                return;
            }
            var suppliedToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..].Trim()
                : string.Empty;
            if (connectionKeys is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await ConnectionKeyHttpAuthorization.InvokeAsync(context, connectionKeys,
                candidate => TokenEquals(token, candidate), _ => next());
        });
        app.MapPost("/_vsix/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok();
        });
        app.MapPost("/_vsix/connection-key/issue", async (HttpContext context) =>
        {
            var store = connectionKeys;
            if (store is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var issuanceId = await TryReadIssuanceIdAsync(context.Request);
            if (issuanceId is null) return Results.Json(new { code = "invalid_argument", message = "The issuance identifier is invalid.", retryable = false, mayHaveChanged = false }, statusCode: StatusCodes.Status400BadRequest);
            var result = store.Issue(issuanceId.Value);
            if (result.Result != ConnectionKeyResult.Success || result.Key is null)
                return result.Result == ConnectionKeyResult.CapacityExceeded
                    ? Results.Json(new { code = "key_capacity_exceeded", message = "Connection-key capacity is full.", retryable = true, mayHaveChanged = false }, statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(new { code = "key_issue_failed", message = "The connection key could not be issued.", retryable = false, mayHaveChanged = false }, statusCode: StatusCodes.Status409Conflict);
            return Results.Json(new
            {
                issuanceId = result.IssuanceId.ToString("N"),
                key = result.Key,
                expiresAt = result.ExpiresAt?.ToUnixTimeSeconds() ?? 0
            });
        });
        app.MapPost("/_vsix/connection-key/cancel", async (HttpContext context) =>
        {
            var issuanceId = await TryReadIssuanceIdAsync(context.Request);
            if (issuanceId is null) return Results.Json(new { code = "invalid_argument", message = "The issuance identifier is invalid.", retryable = false, mayHaveChanged = false }, statusCode: StatusCodes.Status400BadRequest);
            var result = connectionKeys?.CancelPending(issuanceId.Value) ?? ConnectionKeyResult.NotFound;
            return result == ConnectionKeyResult.Success ? Results.NoContent() : Results.NotFound();
        });
        app.MapPost("/_vsix/connection-key/revoke-all", () =>
        {
            connectionKeys?.RevokeAll();
            return Results.NoContent();
        });
        app.MapPost("/_vsix/connection-key/status", () =>
        {
            var status = connectionKeys?.GetStatus();
            if (status is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Json(new
            {
                pendingCount = status.PendingCount,
                activeCount = status.ActiveCount,
                nearestExpiry = status.NearestExpiry?.ToUnixTimeSeconds()
            });
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var baseAddress = addresses?.SingleOrDefault();
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("The HTTP listener did not publish its bound loopback address.");
        }

        var mcpUrl = new Uri(uri, "/mcp").ToString();
        var keyClock = new SystemConnectionKeyClock();
        connectionKeys = new ConnectionKeyStore(keyClock, keyClock, options.ParentPid,
            options.ParentStartTicks, Environment.ProcessId, serverInstanceId, mcpUrl);
        var endpoint = new ServerEndpoint(2, options.ParentPid, options.ParentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Environment.ProcessId, serverInstanceId, mcpUrl, token);
        var descriptorDirectory = Path.GetDirectoryName(options.EndpointFile)
            ?? throw new InvalidOperationException("Endpoint file directory is missing.");
        Directory.CreateDirectory(descriptorDirectory);
        await File.WriteAllTextAsync(options.EndpointFile,
            JsonSerializer.Serialize(endpoint), new UTF8Encoding(false));

        using var monitorCancellation = new CancellationTokenSource();
        var parentMonitor = MonitorParentAsync(parent, app.Lifetime, monitorCancellation.Token);
        try
        {
            await app.WaitForShutdownAsync();
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await parentMonitor; } catch (OperationCanceledException) { }
            connectionKeys.RevokeAll();
            ProjectInventoryTools.SnapshotStore.InvalidateAll();
            try { File.Delete(options.EndpointFile); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Monitors the parent Visual Studio process for exit.
    /// </summary>
    private static async Task MonitorParentAsync(Process parent, IHostApplicationLifetime lifetime, CancellationToken token)
    {
        await parent.WaitForExitAsync(token);
        lifetime.StopApplication();
    }

    /// <summary>
    /// Checks that the Origin is an HTTP loopback origin.
    /// </summary>
    private static bool IsAllowedOrigin(string origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
            return false;
        return parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(parsed.Host, out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// Compares bearer tokens in constant time.
    /// </summary>
    private static bool TokenEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static async Task<Guid?> TryReadIssuanceIdAsync(HttpRequest request)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 4 }, request.HttpContext.RequestAborted);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("issuanceId", out var value) &&
                value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "N", out var id)
                ? id : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private sealed record ServerEndpoint(int SchemaVersion, int ParentPid, string ParentStartTicks,
        int ServerPid, string ServerInstanceId, string Url, string Token);

    private sealed record HttpOptions(int ParentPid, long ParentStartTicks, string EndpointFile)
    {
        public static (int ProcessId, long StartTicks)? ParseOptionalParentIdentity(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            var pidValue = Value("--parent-pid");
            var ticksValue = Value("--parent-start-ticks");
            if (pidValue is null && ticksValue is null) return null;
            if (!int.TryParse(pidValue, out var pid) || pid <= 0 ||
                !long.TryParse(ticksValue, out var startTicks) || startTicks <= 0)
                throw new ArgumentException("Stdio target binding requires both --parent-pid and --parent-start-ticks.");
            return (pid, startTicks);
        }

        public static HttpOptions Parse(string[] args)
        {
            string? Value(string name)
            {
                var index = Array.IndexOf(args, name);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            if (!int.TryParse(Value("--parent-pid"), out var pid) || pid <= 0 ||
                !long.TryParse(Value("--parent-start-ticks"), out var startTicks) || startTicks <= 0 ||
                string.IsNullOrWhiteSpace(Value("--endpoint-file")))
            {
                throw new ArgumentException("HTTP mode requires --parent-pid, --parent-start-ticks, and --endpoint-file.");
            }

            return new HttpOptions(pid, startTicks, Path.GetFullPath(Value("--endpoint-file")!));
        }
    }
}

/// <summary>
/// Explicit allowlist of read-only solution tools exposed through the HTTP transport.
/// </summary>
[McpServerToolType]
public sealed class ReadOnlyHttpTools
{
    /// <summary>Gets information about the currently open solution.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns information about the currently open Visual Studio solution.")]
    public static string GetSolutionInfo(
        [System.ComponentModel.Description("Output format: 'text' or 'json'.")] string format = "text") =>
        GetSolutionInfoTool.GetSolutionInfo(format);

    /// <summary>Gets items from the Error List.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns errors and warnings from the Visual Studio Error List.")]
    public static string GetErrorList(
        [System.ComponentModel.Description("Output format: 'text' or 'json'.")] string format = "text") =>
        GetErrorListTool.GetErrorList(format);

    /// <summary>Lists Output Window pane names.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Lists available Visual Studio Output Window panes.")]
    public static string ListOutputPanes() => ListOutputPanesTool.ListOutputPanes();

    /// <summary>Gets the tail of an Output Window pane.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Reads a bounded number of lines from a Visual Studio Output Window pane.")]
    public static string ReadOutputPane(
        [System.ComponentModel.Description("The name or partial name of the pane.")] string paneName,
        [System.ComponentModel.Description("Maximum number of lines, capped at 2000.")] int maxLines = 50) =>
        ReadOutputPaneTool.ReadOutputPane(paneName, maxLines);
}

/// <summary>
/// Explicit allowlist of debugger tools exposed through authenticated VSIX-managed HTTP.
/// </summary>
[McpServerToolType]
public sealed class DebuggerHttpTools
{
    /// <summary>Lists breakpoints in the process bound to this server.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Lists up to 25 breakpoints in the Visual Studio instance bound to this authenticated server.")]
    public static string ListBreakpoints() => BreakpointManagementTool.ListBreakpoints();

    /// <summary>Adds a breakpoint with explicit mutation authorization.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Adds a source or function breakpoint. Set allowMutation=true; conditions also require allowConditionEvaluation=true.")]
    public static string SetBreakpoint(string? file = null, int line = 0, string? function = null,
        string? condition = null, bool allowMutation = false, bool allowConditionEvaluation = false) =>
        BreakpointManagementTool.SetBreakpoint(file, line, function, condition, allowMutation, allowConditionEvaluation);

    /// <summary>Removes a snapshot-validated breakpoint with explicit mutation authorization.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Removes a breakpoint from the latest list_breakpoints snapshot. Requires expected location/condition fields and allowMutation=true.")]
    public static string RemoveBreakpoint(int id, string? expectedFile = null, int expectedLine = 0,
        string? expectedFunction = null, string? expectedCondition = null, bool allowMutation = false) =>
        BreakpointManagementTool.RemoveBreakpoint(id, expectedFile, expectedLine, expectedFunction, expectedCondition, allowMutation);

    /// <summary>Lists threads in the bound paused debugger.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Lists up to 64 threads in the currently paused, bound Visual Studio debugger.")]
    public static string ListDebuggerThreads() => DebuggerInspectionTool.ListDebuggerThreads();

    /// <summary>Selects a current thread by its operating system ID.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Selects a thread ID from the latest list_debugger_threads response and revalidates it against the bound paused debugger.")]
    public static string SelectDebuggerThread(int threadId) => DebuggerInspectionTool.SelectDebuggerThread(threadId);

    /// <summary>Gets a bounded call stack for the selected or specified thread without changing selection.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Returns up to 128 stack frames for the current thread or an explicitly specified paused thread. The optional threadId is read-only and does not alter Visual Studio selection. Locals and expression evaluation remain bound to CurrentThreadId; select another thread before using those tools.")]
    public static string GetCallStack(
        [System.ComponentModel.Description("Optional positive thread ID from list_debugger_threads. Omit to inspect the currently selected thread.")]
        int? threadId = null) =>
        DebuggerInspectionTool.GetDebuggerStack(threadId);

    /// <summary>Selects a current stack frame after snapshot validation.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Selects a frame from the latest get_call_stack response after validating function, module, and total frame count.")]
    public static string SelectDebuggerFrame(
        [System.ComponentModel.Description("One-based frame index from the latest get_call_stack response, from 1 through 128.")]
        [System.ComponentModel.DataAnnotations.Range(1, 128)] int index,
        string expectedFunction, string expectedModule, int totalFrameCount) =>
        DebuggerInspectionTool.SelectDebuggerFrame(index, expectedFunction, expectedModule, totalFrameCount);

    /// <summary>Reads bounded local values from a validated frame.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Reads up to 40 local/member values at depth 3. Member expansion requires allowMemberExpansion=true.")]
    public static string GetDebuggerLocals(
        [System.ComponentModel.Description("One-based frame index from the latest get_call_stack response, from 1 through 128.")]
        [System.ComponentModel.DataAnnotations.Range(1, 128)] int frameIndex,
        string expectedFunction, string expectedModule,
        int totalFrameCount, int threadId, int maxDepth = 1, int maxItems = 40, bool allowMemberExpansion = false) =>
        DebuggerInspectionTool.GetDebuggerLocals(frameIndex, expectedFunction, expectedModule,
            totalFrameCount, threadId, maxDepth, maxItems, allowMemberExpansion);

    /// <summary>Evaluates an expression with explicit evaluation and side-effect policy.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Evaluates an expression from a current paused frame. Requires allowEvaluation=true; automatic property/function evaluation additionally requires allowSideEffects=true.")]
    public static string EvaluateExpression(string expression,
        [System.ComponentModel.Description("One-based frame index from the latest get_call_stack response, from 1 through 128.")]
        [System.ComponentModel.DataAnnotations.Range(1, 128)] int frameIndex,
        string expectedFunction,
        string expectedModule, int totalFrameCount, int threadId, bool allowEvaluation = false, bool allowSideEffects = false) =>
        DebuggerExpressionTool.EvaluateExpression(expression, frameIndex, expectedFunction, expectedModule,
            totalFrameCount, threadId, allowEvaluation, allowSideEffects);

    /// <summary>Controls the bound paused or running debugger with explicit authorization.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Pauses, continues, or steps the explicitly bound debugger. Requires allowDebuggerControl=true and waits for a bounded observed state transition.")]
    public static Task<string> ControlDebugger(string action, bool allowDebuggerControl = false,
        int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default) =>
        DebuggerControlTool.ControlDebugger(action, allowDebuggerControl, timeoutMilliseconds, cancellationToken);
}
