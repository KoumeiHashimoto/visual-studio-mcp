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
using VisualStudioMcpServer.Workspace;
using VisualStudioMcpServer.Providers;
using VisualStudioMcpServer.Coordination;
using VisualStudioMcpServer.Capabilities;

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

            Console.Error.WriteLine("Usage: VisualStudioMcpServer.exe [stdio [--parent-pid PID --parent-start-ticks TICKS] | http --parent-pid PID --parent-start-ticks TICKS --endpoint-file PATH [--provider-bootstrap-file PATH]]");
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
        var serverInstanceId = ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        CapabilityRuntimeContext.Initialize("stdio", serverInstanceId, providerConfigured: false);
        var targetIdentity = HttpOptions.ParseOptionalParentIdentity(args);
        if (targetIdentity.HasValue)
        {
            VisualStudioConnector.SetTargetProcessIdentity(targetIdentity.Value.ProcessId, targetIdentity.Value.StartTicks);
            TargetMutationOwner.Initialize(targetIdentity.Value.ProcessId, targetIdentity.Value.StartTicks);
        }
        StaComDispatcher.Start();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(GetSolutionInfoTool).Assembly);

        try { await builder.Build().RunAsync(); }
        finally { TargetMutationOwner.Shutdown(); }
    }

    /// <summary>
    /// Starts the authenticated loopback HTTP host managed by the VSIX.
    /// </summary>
    /// <param name="args">The parent process identity and endpoint descriptor path.</param>
    private static async Task RunHttpAsync(string[] args)
    {
        var options = HttpOptions.Parse(args);
        var providerBootstrap = ProviderClient.ReadBootstrap(options.ProviderBootstrapFile);
        if (providerBootstrap is not null && (providerBootstrap.VisualStudioPid != options.ParentPid ||
            providerBootstrap.VisualStudioStartTicks != options.ParentStartTicks))
            throw new InvalidDataException("The provider bootstrap does not match the Visual Studio parent identity.");
        using var parent = Process.GetProcessById(options.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks)
        {
            throw new InvalidOperationException("The specified Visual Studio process identity is stale.");
        }

        VisualStudioConnector.SetTargetProcessIdentity(options.ParentPid, options.ParentStartTicks);
        TargetMutationOwner.Initialize(options.ParentPid, options.ParentStartTicks);
        StaComDispatcher.Start();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var serverInstanceId = ConnectionKeyCodec.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        CapabilityRuntimeContext.Initialize("http", serverInstanceId, providerBootstrap is not null);
        ConnectionKeyStore? connectionKeys = null;
        var workspaceSnapshots = new ActiveWorkspaceSnapshotStore(options.ParentPid, options.ParentStartTicks);
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
            if (!ConnectionKeyHttpAuthorization.IsAuthorizationHeaderAllowed(authorization,
                    context.Request.Headers.Authorization.Count))
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
        app.MapPost("/_vsix/workspace/snapshot", async (HttpContext context) =>
        {
            var update = await TryReadWorkspaceSnapshotAsync(context.Request);
            if (update is null) return Results.Json(new { code = "invalid_workspace_snapshot", retryable = false, mayHaveChanged = false }, statusCode: StatusCodes.Status400BadRequest);
            if (!workspaceSnapshots.TryUpdate(update.ToSnapshot()))
                return Results.Json(new { code = "workspace_identity_mismatch", retryable = false, mayHaveChanged = false }, statusCode: StatusCodes.Status409Conflict);
            return Results.NoContent();
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
        ActiveWorkspaceSnapshotStoreAccessor.Set(workspaceSnapshots);
        var endpoint = new ServerEndpoint(2, options.ParentPid, options.ParentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Environment.ProcessId, serverInstanceId, mcpUrl, token);
        var descriptorDirectory = Path.GetDirectoryName(options.EndpointFile)
            ?? throw new InvalidOperationException("Endpoint file directory is missing.");
        Directory.CreateDirectory(descriptorDirectory);
        await File.WriteAllTextAsync(options.EndpointFile,
            JsonSerializer.Serialize(endpoint), new UTF8Encoding(false));

        using var monitorCancellation = new CancellationTokenSource();
        using var providerCancellation = new CancellationTokenSource();
        var parentMonitor = MonitorParentAsync(parent, app.Lifetime, monitorCancellation.Token);
        var providerTask = providerBootstrap is null ? null :
            ProviderClient.RunAsync(providerBootstrap, serverInstanceId, providerCancellation.Token,
                CapabilityRuntimeContext.SetProvider);
        try
        {
            var shutdownTask = app.WaitForShutdownAsync();
            if (providerTask is null) await shutdownTask;
            else
            {
                var completed = await Task.WhenAny(shutdownTask, providerTask);
                if (completed == providerTask)
                {
                    await providerTask;
                    app.Lifetime.StopApplication();
                }
                await shutdownTask;
            }
        }
        finally
        {
            monitorCancellation.Cancel();
            providerCancellation.Cancel();
            try { await parentMonitor; } catch (OperationCanceledException) { }
            if (providerTask is not null)
            {
                try { await providerTask; } catch (OperationCanceledException) { }
            }
            connectionKeys.RevokeAll();
            ProjectInventoryTools.SnapshotStore.InvalidateAll();
            ActiveWorkspaceSnapshotStoreAccessor.Clear(workspaceSnapshots);
            try { File.Delete(options.EndpointFile); } catch (IOException) { }
            TargetMutationOwner.Shutdown();
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

    private static async Task<WorkspaceSnapshotUpdate?> TryReadWorkspaceSnapshotAsync(HttpRequest request)
    {
        if (request.ContentLength is > 4096) return null;
        request.EnableBuffering(4096, 4096);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        try
        {
            while (buffer.Length <= 4096)
            {
                var count = await request.Body.ReadAsync(chunk.AsMemory(0,
                    Math.Min(chunk.Length, 4097 - (int)buffer.Length)), request.HttpContext.RequestAborted);
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length > 4096) return null;
            request.Body.Position = 0;
            return JsonSerializer.Deserialize<WorkspaceSnapshotUpdate>(buffer.ToArray(),
                new JsonSerializerOptions { MaxDepth = 8, PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        finally { if (request.Body.CanSeek) request.Body.Position = 0; }
    }

    private sealed class WorkspaceSnapshotUpdate
    {
        public bool Active { get; set; }
        public int VisualStudioPid { get; set; }
        public long VisualStudioStartTicks { get; set; }
        public long Generation { get; set; }
        public string Root { get; set; } = string.Empty;
        public string WorkspaceKind { get; set; } = string.Empty;
        public string CMakeMetadataStatus { get; set; } = string.Empty;
        public string CMakeMetadataReason { get; set; } = string.Empty;

        public ActiveWorkspaceSnapshot ToSnapshot() => new(Active, VisualStudioPid, VisualStudioStartTicks,
            Generation, Root, WorkspaceKind, CMakeMetadataStatus, CMakeMetadataReason);
    }

    private sealed record ServerEndpoint(int SchemaVersion, int ParentPid, string ParentStartTicks,
        int ServerPid, string ServerInstanceId, string Url, string Token);

    private sealed record HttpOptions(int ParentPid, long ParentStartTicks, string EndpointFile, string? ProviderBootstrapFile)
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

            var bootstrapFile = Value("--provider-bootstrap-file");
            return new HttpOptions(pid, startTicks, Path.GetFullPath(Value("--endpoint-file")!),
                string.IsNullOrWhiteSpace(bootstrapFile) ? null : Path.GetFullPath(bootstrapFile));
        }
    }
}

/// <summary>
/// Explicit allowlist of read-only solution tools exposed through the HTTP transport.
/// </summary>
[McpServerToolType]
public sealed class ReadOnlyHttpTools
{
    /// <summary>このtransportの能力情報とprovider状態を取得します。</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns bounded capabilities and provider availability for this Visual Studio companion instance.")]
    public static string GetVsCapabilities() => GetVsCapabilitiesTool.GetVsCapabilities();

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

    /// <summary>Gets a bounded, stable page from the Error List.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns a stable, bounded page from a captured Visual Studio Error List snapshot. Prefers the VSIX Error List table provider for code, source, project identity and Open Folder workspaces; otherwise falls back to the DTE reader, whose fields remain explicitly unavailable. Provenance reports which reader captured the snapshot.")]
    public static Task<string> GetDiagnostics(
        [System.ComponentModel.Description("Exact active solution path. Required for solution workspaces.")] string? expectedSolutionPath = null,
        [System.ComponentModel.Description("Expected Open Folder root. Supported through the VSIX provider; the DTE fallback returns unsupported for Open Folder.")] string? expectedWorkspaceRoot = null,
        [System.ComponentModel.Description("Optional exact severity filter: Error, Warning, Message, or Unknown.")] string? severity = null,
        [System.ComponentModel.Description("Optional stable project identity filter (the project GUID reported by the VSIX provider). The DTE fallback cannot filter by project.")] string? projectId = null,
        [System.ComponentModel.Description("Optional case-insensitive file path substring filter.")] string? file = null,
        [System.ComponentModel.Description("Optional source filter: build, intellisense, other, or unknown. The provider reports build or other from the Error List source and never infers intellisense; the DTE fallback supports only unknown.")] string? source = null,
        [System.ComponentModel.Description("Page size from 1 to 200, default 100.")] int pageSize = 100,
        [System.ComponentModel.Description("Opaque cursor returned by a prior page. Cursors expire with their bounded snapshot.")] string? cursor = null,
        [System.ComponentModel.Description("Build observation annotation only. Rows are never attributed to an operation without provider evidence.")] string? operationId = null) =>
        GetDiagnosticsTool.GetDiagnostics(expectedSolutionPath, expectedWorkspaceRoot, severity, projectId, file, source, pageSize, cursor, operationId);

    /// <summary>Lists Output Window pane names.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Lists available Visual Studio Output Window panes.")]
    public static string ListOutputPanes(
        [System.ComponentModel.Description("Output format: 'text' preserves the legacy list; 'json' includes GUID, display name, and availability.")]
        string format = "text") => ListOutputPanesTool.ListOutputPanes(format);

    /// <summary>Gets the tail of an Output Window pane.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Reads a bounded number of lines from a Visual Studio Output Window pane.")]
    public static string ReadOutputPane(
        [System.ComponentModel.Description("The name or partial name of the pane.")] string paneName,
        [System.ComponentModel.Description("Maximum number of lines, capped at 2000.")] int maxLines = 50) =>
        ReadOutputPaneTool.ReadOutputPane(paneName, maxLines);

    /// <summary>Reads a bounded Output Window snapshot by exact pane GUID.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Reads a bounded snapshot from an Output Window pane selected by its exact GUID. Delta continuity is currently unavailable.")]
    public static string ReadOutputSince(
        [System.ComponentModel.Description("Exact pane GUID returned by list_output_panes(format='json').")] string paneId,
        [System.ComponentModel.Description("Opaque cursor from a previous snapshot.")] string? cursor = null,
        [System.ComponentModel.Description("Maximum tail lines, capped at 2000.")] int maxLines = 200,
        [System.ComponentModel.Description("Maximum UTF-16 code units, capped at 32768.")] int maxCharacters = 16384) =>
        ReadOutputSinceTool.ReadOutputSince(paneId, cursor, maxLines, maxCharacters);

    /// <summary>Lists Running Document Table entries through the VSIX provider.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Lists Running Document Table entries from the bound Visual Studio instance as a bounded snapshot with opaque document IDs, dirty/read-only state, buffer generation, and tab visibility. RDT entries are not identical to visible tabs. Requires the VSIX provider; reads never save, open, or activate documents.")]
    public static Task<string> ListOpenDocuments(
        [System.ComponentModel.Description("'workspace' (default) returns registered project items and the active Open Folder root only; 'all' explicitly includes external and untitled documents.")] string scope = "workspace",
        [System.ComponentModel.Description("Page size from 1 to 100, default 100.")] int pageSize = 100,
        [System.ComponentModel.Description("Opaque cursor from a previous page of the same snapshot and scope.")] string? cursor = null) =>
        DocumentReadTools.ListOpenDocuments(scope, pageSize, cursor);

    /// <summary>Reads the focused or last active code view through the VSIX provider.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns the focused or last active code view in the bound Visual Studio instance: document ID, buffer generation, one-based UTF-16 caret and end-exclusive selection ranges, and content type. Selected text is opt-in and omitted for documents outside the workspace scope. Requires the VSIX provider; never changes focus or selection.")]
    public static Task<string> GetEditorContext(
        [System.ComponentModel.Description("Include the primary selection text. Defaults to false.")] bool includeSelectedText = false,
        [System.ComponentModel.Description("Maximum selected-text UTF-16 code units, from 0 to 4096, default 4096.")] int maxCharacters = 4096) =>
        DocumentReadTools.GetEditorContext(includeSelectedText, maxCharacters);

    /// <summary>Reads a bounded range from a live editor buffer through the VSIX provider.</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Reads a bounded line range from one immutable live editor-buffer snapshot, including unsaved changes. Lines and columns are one-based UTF-16 code units and the end position is exclusive; surrogate pairs are never split. A stale expectedBufferVersion returns document_changed. Requires the VSIX provider; never saves or reloads.")]
    public static Task<string> ReadDocument(
        [System.ComponentModel.Description("Opaque document ID from list_open_documents or get_editor_context.")] string documentId,
        [System.ComponentModel.Description("Optional buffer version from a previous response. A different current version returns document_changed instead of mixing revisions.")] string? expectedBufferVersion = null,
        [System.ComponentModel.Description("One-based first line, default 1.")] int startLine = 1,
        [System.ComponentModel.Description("Number of lines from 1 to 500, default 200.")] int lineCount = 200,
        [System.ComponentModel.Description("Maximum UTF-16 code units from 1 to 32768, default 16384.")] int maxCharacters = 16384) =>
        DocumentReadTools.ReadDocument(documentId, expectedBufferVersion, startLine, lineCount, maxCharacters);
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

    /// <summary>Evaluates an expression with explicit evaluation, sensitive-read, and side-effect policies.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Evaluates an expression from a current paused frame. Requires allowEvaluation=true; sensitive pseudovariables additionally require allowSensitiveRead=true; automatic property/function evaluation additionally requires allowSideEffects=true.")]
    public static string EvaluateExpression(string expression,
        [System.ComponentModel.Description("One-based frame index from the latest get_call_stack response, from 1 through 128.")]
        [System.ComponentModel.DataAnnotations.Range(1, 128)] int frameIndex,
        string expectedFunction,
        string expectedModule, int totalFrameCount, int threadId, bool allowEvaluation = false, bool allowSideEffects = false,
        bool allowSensitiveRead = false) =>
        DebuggerExpressionTool.EvaluateExpression(expression, frameIndex, expectedFunction, expectedModule,
            totalFrameCount, threadId, allowEvaluation, allowSideEffects, allowSensitiveRead);

    /// <summary>Controls the bound paused or running debugger with explicit authorization.</summary>
    [McpServerTool]
    [System.ComponentModel.Description("Pauses, continues, or steps the explicitly bound debugger. Requires allowDebuggerControl=true and waits for a bounded observed state transition.")]
    public static Task<string> ControlDebugger(string action, bool allowDebuggerControl = false,
        int timeoutMilliseconds = 5000, CancellationToken cancellationToken = default) =>
        DebuggerControlTool.ControlDebugger(action, allowDebuggerControl, timeoutMilliseconds, cancellationToken);
}
