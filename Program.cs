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

[assembly: SupportedOSPlatform("windows")]

/// <summary>
/// Visual Studio向けMCPサーバーのstdioとHTTPホストを起動します。
/// </summary>
internal static class Program
{
    /// <summary>
    /// MCPサーバーを起動します。
    /// </summary>
    /// <param name="args">起動モードとVisual Studio所有者情報。</param>
    /// <returns>終了コード。</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0].Equals("stdio", StringComparison.OrdinalIgnoreCase))
            {
                await RunStdioAsync();
                return 0;
            }

            if (args[0].Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                await RunHttpAsync(args[1..]);
                return 0;
            }

            Console.Error.WriteLine("Usage: VisualStudioMcpServer.exe [stdio | http --parent-pid PID --parent-start-ticks TICKS --endpoint-file PATH]");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 標準入出力トランスポートを起動します。
    /// </summary>
    private static async Task RunStdioAsync()
    {
        StaComDispatcher.Start();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(GetSolutionInfoTool).Assembly);

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// VSIX管理下の認証付きloopback HTTPホストを起動します。
    /// </summary>
    /// <param name="args">親プロセスと接続情報ファイル。</param>
    private static async Task RunHttpAsync(string[] args)
    {
        var options = HttpOptions.Parse(args);
        using var parent = Process.GetProcessById(options.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks)
        {
            throw new InvalidOperationException("The specified Visual Studio process identity is stale.");
        }

        VisualStudioConnector.SetTargetProcessId(options.ParentPid);
        StaComDispatcher.Start();
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<ReadOnlyHttpTools>();

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
            var suppliedToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..].Trim()
                : string.Empty;
            if (!TokenEquals(token, suppliedToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });
        app.MapPost("/_vsix/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok();
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

        var endpoint = new ServerEndpoint(options.ParentPid, Environment.ProcessId,
            new Uri(uri, "/mcp").ToString(), token);
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
            try { File.Delete(options.EndpointFile); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 親Visual Studioの終了を監視します。
    /// </summary>
    private static async Task MonitorParentAsync(Process parent, IHostApplicationLifetime lifetime, CancellationToken token)
    {
        await parent.WaitForExitAsync(token);
        lifetime.StopApplication();
    }

    /// <summary>
    /// OriginがloopbackのHTTP originであることを確認します。
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
    /// トークンを一定時間比較で照合します。
    /// </summary>
    private static bool TokenEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed record ServerEndpoint(int ParentPid, int ServerPid, string Url, string Token);

    private sealed record HttpOptions(int ParentPid, long ParentStartTicks, string EndpointFile)
    {
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
/// HTTPトランスポートで公開する読み取り専用ツールの許可リストです。
/// </summary>
[McpServerToolType]
public sealed class ReadOnlyHttpTools
{
    /// <summary>現在開いているソリューションの情報を取得します。</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns information about the currently open Visual Studio solution.")]
    public static string GetSolutionInfo(
        [System.ComponentModel.Description("Output format: 'text' or 'json'.")] string format = "text") =>
        GetSolutionInfoTool.GetSolutionInfo(format);

    /// <summary>Error Listの項目を取得します。</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Returns errors and warnings from the Visual Studio Error List.")]
    public static string GetErrorList(
        [System.ComponentModel.Description("Output format: 'text' or 'json'.")] string format = "text") =>
        GetErrorListTool.GetErrorList(format);

    /// <summary>Output Windowのpane名を取得します。</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Lists available Visual Studio Output Window panes.")]
    public static string ListOutputPanes() => ListOutputPanesTool.ListOutputPanes();

    /// <summary>Output Window paneの末尾を取得します。</summary>
    [ModelContextProtocol.Server.McpServerTool]
    [System.ComponentModel.Description("Reads a bounded number of lines from a Visual Studio Output Window pane.")]
    public static string ReadOutputPane(
        [System.ComponentModel.Description("The name or partial name of the pane.")] string paneName,
        [System.ComponentModel.Description("Maximum number of lines, capped at 2000.")] int maxLines = 50) =>
        ReadOutputPaneTool.ReadOutputPane(paneName, maxLines);
}
