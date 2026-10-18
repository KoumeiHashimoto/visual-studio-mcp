using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Tools;

/// <summary>
/// provider を使う read tool の結果を有効な JSON として組み立てる。上限を超えた JSON は切らずに構造化エラーにする。
/// </summary>
internal static class ProviderToolResults
{
    internal const int MaximumResponseCharacters = 64 * 1024;

    private static readonly JsonSerializerSettings ErrorSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None
    };

    internal static string Error(string code, string message, string? dispatchState = null,
        string? providerReason = null, string? providerEpoch = null) =>
        JsonConvert.SerializeObject(new ErrorResult(false, code, Bound(message, 512), dispatchState, providerReason, providerEpoch),
            ErrorSettings);

    /// <summary>provider の error/unavailable 結果をそのまま構造化エラーへ写す。channel code は MCP error にしない。</summary>
    internal static string FromOutcome(ProviderReadOutcome outcome) =>
        Error(outcome.Code ?? "internal_error", outcome.Message ?? "The Visual Studio provider could not complete the read.",
            outcome.DispatchState, outcome.ProviderReason, outcome.ProviderEpoch);

    internal static string InvalidProviderResponse(ProviderReadOutcome outcome) =>
        Error("provider_response_invalid", "The Visual Studio provider returned a response outside the read contract.",
            outcome.DispatchState, null, outcome.ProviderEpoch);

    /// <summary>provider 本文の DTO に Success と provider 情報を付けて返す。上限超過は response_too_large。</summary>
    internal static string Success(object body, ProviderReadOutcome outcome, Action<JObject>? decorate = null)
    {
        var result = new JObject
        {
            ["Success"] = true,
            ["Provider"] = "visualStudioProvider",
            ["ProviderEpoch"] = outcome.ProviderEpoch,
            ["ObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        };
        result.Merge(JObject.FromObject(body));
        decorate?.Invoke(result);
        var json = result.ToString(Formatting.None);
        return json.Length <= MaximumResponseCharacters
            ? json
            : Error("response_too_large", "The provider response exceeded the 64 Ki character tool limit. Request a smaller range.",
                outcome.DispatchState, null, outcome.ProviderEpoch);
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private sealed record ErrorResult(bool Success, string Code, string Message, string? DispatchState,
        string? ProviderReason, string? ProviderEpoch);
}
