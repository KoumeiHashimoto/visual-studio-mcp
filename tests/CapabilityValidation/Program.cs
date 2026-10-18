using System.Text.Json;
using VisualStudioMcpServer.Capabilities;

var now = DateTimeOffset.UtcNow;
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
    checks++;
}
CapabilityRuntimeObservation Runtime(string transport, ProviderCapabilityObservation provider,
    string workspaceKind = "solution", IReadOnlyDictionary<string, FeatureProviderObservation>? features = null,
    string debugger = "unknown", string mutation = "unknown") => new(
        transport, "1.3.0.0", "assemblyVersion", "server-instance", Guid.NewGuid(), 40, 1001,
        new WorkspaceCapabilityObservation("observed", workspaceKind, 4, null), provider,
        features ?? new Dictionary<string, FeatureProviderObservation>(), debugger, "fixture_observation",
        mutation, "fixture_lease", now);
ProviderCapabilityObservation Provider(string state, string availability, DateTimeOffset? observedAt, string? reason = null) => new(
    state, availability, "provider", null, "software_version_not_supplied", 1, "epoch-1", observedAt, reason);

var missing = CapabilityRegistry.Create(Runtime("stdio", Provider("not_configured", "unsupported", null, "requires_vsix_provider")), () => now);
var connected = CapabilityRegistry.Create(Runtime("http", Provider("connected", "supported", now)), () => now);
var features = missing.Features.ToDictionary(feature => feature.Feature);
Check(missing.Features.Count <= CapabilityRegistry.MaximumFeatures, "feature count is bounded");
Check(features.Keys.Order().SequenceEqual(connected.Features.Select(feature => feature.Feature).Order()), "stdio and HTTP share the same feature contract");
Check(features["get_vs_capabilities"].Availability == "supported" && features["get_vs_capabilities"].HttpExposed && features["get_vs_capabilities"].StdioExposed,
    "capability query is exposed on both transports");
Check(features["debugger.execution"].Availability == "unsupported" && features["documents.read"].Availability != "supported",
    "unimplemented execution and document reads are not declared supported");
Check(missing.ProviderAvailability == "unsupported" && missing.ProviderReason == "requires_vsix_provider" &&
    connected.ProviderAvailability == "supported" && connected.ProviderVersion is null && connected.ProviderProtocolVersion == 1,
    "provider absent, connected, software version, and protocol version remain distinct");
Check(missing.VsixVersion == "unknown" && missing.VisualStudioVersion == "unknown" &&
    missing.VsixVersionReason is not null && missing.VisualStudioVersionReason is not null,
    "unprovided VSIX and IDE versions report unknown with reasons");

var busy = CapabilityRegistry.Create(Runtime("http", Provider("connected", "supported", now), debugger: "busy", mutation: "busy"), () => now);
Check(busy.TemporaryDebuggerState == "busy" && busy.TemporaryMutationState == "busy" &&
    busy.Features.Single(feature => feature.Feature == "solution.build").Availability == "unknown",
    "temporary busy state is separate from feature availability");
var pending = CapabilityRegistry.Create(Runtime("http", Provider("connecting", "unknown", null, "handshake_pending")), () => now);
Check(pending.ProviderState == "connecting" && pending.ProviderAvailability == "unknown", "incomplete provider handshake remains unknown");
var stale = CapabilityRegistry.Create(Runtime("http", Provider("connected", "supported", now.AddMinutes(-6))), () => now);
Check(stale.ProviderObservationStale == true && stale.ProviderAvailability == "unknown" && stale.ProviderReason == "provider_observation_stale",
    "old provider observation is stale and downgraded to unknown");
var future = CapabilityRegistry.Create(Runtime("http", Provider("connected", "supported", now.AddMinutes(1))), () => now);
Check(future.ProviderObservationStale == true && future.ProviderAvailability == "unknown", "future provider observation is rejected as stale");
var folder = CapabilityRegistry.Create(Runtime("http", Provider("not_configured", "unknown", null), "openFolder"), () => now);
Check(folder.Features.Single(feature => feature.Feature == "solution.build").Availability == "unsupported",
    "Open Folder build remains unsupported without dispatch");
var featureProbe = new Dictionary<string, FeatureProviderObservation>
{
    ["solution.read"] = new("supported", "fixture_confirmed", "epoch-1", now),
    ["debugger.execution"] = new("supported", "fixture_claim", "epoch-1", now)
};
var probed = CapabilityRegistry.Create(Runtime("http", Provider("connected", "supported", now), features: featureProbe), () => now);
Check(probed.Features.Single(feature => feature.Feature == "solution.read").Availability == "supported",
    "fresh provider capability updates can refine a feature");
Check(probed.Features.Single(feature => feature.Feature == "debugger.execution").Availability == "unsupported",
    "provider cannot promote an unimplemented backend feature");
CapabilityRuntimeContext.Initialize("http", "instance", providerConfigured: true);
CapabilityRuntimeContext.SetProvider(Provider("connected", "supported", now) with { Epoch = "epoch-a" });
CapabilityRuntimeContext.SetFeatureProviderAvailability("solution.read", "supported", "epoch_a_probe", "epoch-a", now);
var runtimeWorkspace = new WorkspaceCapabilityObservation("observed", "solution", 10, null);
var fromContext = CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now);
Check(fromContext.Features.Single(feature => feature.Feature == "solution.read").Availability == "supported",
    "provider epoch mutator publishes a fresh feature observation");
CapabilityRuntimeContext.SetFeatureProviderAvailability("solution.read", "unsupported", "wrong_epoch", "epoch-b", now);
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now)
    .Features.Single(feature => feature.Feature == "solution.read").Availability == "supported",
    "feature update from a replaced provider epoch is rejected");
CapabilityRuntimeContext.SetProvider(Provider("disconnected", "unknown", now) with { Epoch = "epoch-a" });
CapabilityRuntimeContext.SetProvider(Provider("connected", "supported", now) with { Epoch = "epoch-b" });
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now)
    .Features.Single(feature => feature.Feature == "solution.read").Availability == "unknown",
    "provider disconnect and replacement clear earlier feature observations");
CapabilityRuntimeContext.SetFeatureProviderAvailability("solution.read", "supported", "stale", "epoch-b", now.AddMinutes(-6));
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now)
    .Features.Single(feature => feature.Feature == "solution.read").Availability == "unknown",
    "provider mutator does not retain stale feature support");
CapabilityRuntimeContext.SetFeatureProviderAvailability("solution.read", "supported", "future", "epoch-b", now.AddMinutes(1));
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now)
    .Features.Single(feature => feature.Feature == "solution.read").Availability == "unknown",
    "provider mutator rejects future-dated feature support");
var json = JsonSerializer.Serialize(connected);
Check(json.Length <= 64 * 1024, "serialized capability document stays within the character bound");
Console.WriteLine($"{checks} capability checks passed");
