using System.Runtime.CompilerServices;
using VisualStudioMcpServer.Capabilities;

namespace VisualStudioMcpServer.Providers;

/// <summary>
/// 現在の provider epoch の channel を使う本番の read 経路。channel がない stdio や未接続の HTTP では
/// requires_vsix_provider を返し、DTE や別経路で代用しない（代用の可否は各 tool が決める）。
/// </summary>
internal sealed class ProviderReadChannelTransport : IProviderReadTransport
{
    /// <summary>module 初期化時に本番経路を設定する。検証 project はこの file を含めず fake を設定する。</summary>
    [ModuleInitializer]
    internal static void Install() => ProviderReadGateway.Transport ??= new ProviderReadChannelTransport();

    public async Task<ProviderReadOutcome> SendAsync(string operation, Func<ProviderTarget, object> bodyFactory,
        string? workspacePath, TimeSpan timeout)
    {
        var provider = CapabilityRuntimeContext.Capture(null, null,
            new WorkspaceCapabilityObservation("unknown", null, null, null)).Provider;
        var channel = ProviderClient.ActiveChannel;
        if (channel is null || channel.IsTerminated)
        {
            var reason = provider.Reason == ProviderErrorCodes.ProviderProtocolMismatch
                ? ProviderErrorCodes.ProviderProtocolMismatch
                : provider.State is "disconnected" or "stopped" || channel is not null
                    ? ProviderErrorCodes.ProviderDisconnected
                    : "provider_not_configured";
            return ProviderReadGateway.Unavailable(reason);
        }
        if (!channel.ImplementedOperations.Contains(operation))
            return ProviderReadGateway.Unavailable("operation_not_implemented", provider.Epoch);
        if (!VisualStudioConnector.TryGetTargetProcessIdentity(out var pid, out var startTicks))
            return new ProviderReadOutcome(ProviderReadGateway.StatusError, "target_identity_required",
                "The companion is not bound to a Visual Studio process identity.", null,
                ProviderDispatchState.NotDispatched, provider.Epoch, null);

        var target = new ProviderTarget
        {
            VisualStudioPid = pid,
            VisualStudioStartTicks = startTicks,
            WorkspacePath = workspacePath
        };
        var pending = await channel.SendAsync(operation, bodyFactory(target), timeout).ConfigureAwait(false);
        var result = await pending.Result.ConfigureAwait(false);
        if (result.IsOk)
            return new ProviderReadOutcome(ProviderReadGateway.StatusOk, null, null, result.Body,
                result.DispatchState, provider.Epoch, null);
        if (result.Code == ProviderErrorCodes.UnsupportedOperation)
            return ProviderReadGateway.Unavailable("operation_not_implemented", provider.Epoch);
        return new ProviderReadOutcome(ProviderReadGateway.StatusError, result.Code ?? ProviderErrorCodes.InternalError,
            BoundMessage(result.Message), null, result.DispatchState, provider.Epoch, null);
    }

    private static string BoundMessage(string? message) =>
        string.IsNullOrEmpty(message) ? "The Visual Studio provider could not complete the read." :
        message.Length <= ProviderProtocol.MaxErrorMessageCharacters ? message : message[..ProviderProtocol.MaxErrorMessageCharacters];
}
