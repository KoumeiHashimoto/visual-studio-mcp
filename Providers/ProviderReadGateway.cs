namespace VisualStudioMcpServer.Providers;

/// <summary>
/// provider read 要求 1 件の結果。Status は ok / error / unavailable。
/// unavailable は provider が存在しない、または操作を実装していないことを表し、呼び出し側は fallback を選べる。
/// </summary>
internal sealed record ProviderReadOutcome(
    string Status,
    string? Code,
    string? Message,
    string? Body,
    string DispatchState,
    string? ProviderEpoch,
    string? ProviderReason)
{
    internal bool IsOk => Status == ProviderReadGateway.StatusOk;
    internal bool IsUnavailable => Status == ProviderReadGateway.StatusUnavailable;
}

/// <summary>read 要求を VSIX provider へ送る経路。本番は channel、検証は fake を差し込む。</summary>
internal interface IProviderReadTransport
{
    /// <summary>
    /// 要求本文を target 付きで生成して送り、応答を待つ。provider がない場合は unavailable を返し、例外を投げない。
    /// </summary>
    Task<ProviderReadOutcome> SendAsync(string operation, Func<ProviderTarget, object> bodyFactory,
        string? workspacePath, TimeSpan timeout);
}

/// <summary>
/// read track（#58/#59/#60）の provider 呼び出し口。channel の失敗は MCP protocol error にせず、構造化結果へ変換する。
/// </summary>
internal static class ProviderReadGateway
{
    internal const string StatusOk = "ok";
    internal const string StatusError = "error";
    internal const string StatusUnavailable = "unavailable";
    internal const string RequiresVsixProvider = "requires_vsix_provider";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>本番では channel 実装が module 初期化時に設定する。未設定は provider なしとして扱う。</summary>
    internal static IProviderReadTransport? Transport { get; set; }

    internal static async Task<ProviderReadOutcome> ReadAsync(string operation, Func<ProviderTarget, object> bodyFactory,
        string? workspacePath = null, TimeSpan? timeout = null)
    {
        var transport = Transport;
        if (transport is null)
            return Unavailable("provider_not_configured");
        try
        {
            return await transport.SendAsync(operation, bodyFactory, workspacePath, timeout ?? DefaultTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 送信経路の想定外の失敗は結果を確定できない。本文や例外文は返さない。
            return new ProviderReadOutcome(StatusError, "internal_error", "The provider request failed unexpectedly.",
                null, ProviderDispatchState.Unknown, null, null);
        }
    }

    internal static ProviderReadOutcome Unavailable(string reason, string? epoch = null) =>
        new(StatusUnavailable, RequiresVsixProvider, UnavailableMessage(reason), null,
            ProviderDispatchState.NotDispatched, epoch, reason);

    internal static string UnavailableMessage(string reason) => reason switch
    {
        "operation_not_implemented" =>
            "The connected Visual Studio provider does not implement this read. Update the VSIX and its companion together.",
        "provider_disconnected" =>
            "The Visual Studio provider channel is disconnected. Restart the MCP server from Visual Studio to reconnect.",
        "provider_protocol_mismatch" =>
            "The Visual Studio provider protocol does not match this companion. Install a matching VSIX build.",
        _ => "This read requires the in-process Visual Studio provider, which is available only through the VSIX-managed HTTP endpoint."
    };
}
