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
Check(features["debugger.execution"].Implementation == "implemented" && features["debugger.execution"].HttpExposed &&
    features["debugger.execution"].StdioExposed && features["debugger.execution"].Availability == "unknown" &&
    features["documents.read"].Availability != "supported",
    "execution reflects HTTP exposure and stays unknown until the provider reports it");
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
Check(probed.Features.Single(feature => feature.Feature == "debugger.execution").Availability == "supported",
    "a fresh provider observation can report execution support");
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
// capabilities.probe の反映（#54 実機: 接続中に provider_observation_stale になっていた）。
CapabilityRuntimeContext.Initialize("http", "instance", providerConfigured: true);
CapabilityRuntimeContext.SetProvider(Provider("connected", "supported", now.AddMinutes(-10)) with { Epoch = "epoch-p" });
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now).ProviderObservationStale == true,
    "a handshake-only provider observation becomes stale after five minutes");
CapabilityRuntimeContext.SetImplementedOperations("epoch-p", new[] { "documents.read", "capabilities.probe", "debugger.getState" });
CapabilityRuntimeContext.ApplyProbe("epoch-p", "18.11.12306.390", "1.3.0.0", "1.3.0.0",
    new[] { ("debugger.execution", "supported", "provider_services_available", now), ("output.incremental", "unsupported", "output_buffer_unavailable", now) }, now);
var probedDocument = CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now);
Check(probedDocument.ProviderObservationStale == false && probedDocument.ProviderAvailability == "supported" &&
    probedDocument.ProviderReason == "authenticated_provider_channel_connected" && probedDocument.ProviderVersion == "1.3.0.0",
    "a probe refreshes the provider observation and version");
Check(probedDocument.VisualStudioVersion == "18.11.12306.390" && probedDocument.VsixVersion == "1.3.0.0",
    "probe-reported IDE and VSIX versions are published");
Check(probedDocument.ProviderImplementedOperations is { Count: 3 } ops && ops[0] == "capabilities.probe",
    "implemented operations are exposed in sorted order");
Check(probedDocument.Features.Single(feature => feature.Feature == "debugger.execution").Availability == "supported" &&
    probedDocument.Features.Single(feature => feature.Feature == "output.incremental").Availability == "unsupported" &&
    probedDocument.Features.Single(feature => feature.Feature == "solution.read").ProviderReason == "provider_feature_not_reported",
    "probe features are applied and unreported features are distinguished");
CapabilityRuntimeContext.ApplyProbe("epoch-other", "x", "x", "x", new[] { ("debugger.execution", "unsupported", "other", now) }, now);
Check(CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now)
    .Features.Single(feature => feature.Feature == "debugger.execution").Availability == "supported",
    "a probe from another provider epoch is ignored");
CapabilityRuntimeContext.SetProvider(Provider("disconnected", "unknown", now) with { Epoch = "epoch-p" });
var disconnected = CapabilityRegistry.Create(CapabilityRuntimeContext.Capture(40, 1001, runtimeWorkspace), () => now);
Check(disconnected.ProviderImplementedOperations is null && disconnected.VsixVersion == "unknown",
    "disconnect clears implemented operations and probe versions");
var json = JsonSerializer.Serialize(connected);
Check(json.Length <= 64 * 1024, "serialized capability document stays within the character bound");
Console.WriteLine($"{checks} capability checks passed");
