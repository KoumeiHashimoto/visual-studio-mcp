using System.Reflection;

namespace VisualStudioMcpServer.Capabilities;

/**
  transportとproviderの状態を管理し、読み取り専用の実行時情報を取得します
*/
internal static class CapabilityRuntimeContext
{
    private static readonly object Sync = new();
    private static string _transport = "unknown";
    private static string? _serverInstanceId;
    private static Guid? _serverEpoch;
    private static ProviderCapabilityObservation _provider = new("not_configured", "unknown", null, null, null, null, null, null, "provider_not_configured");
    private static readonly Dictionary<string, FeatureProviderObservation> FeatureProviders = new(StringComparer.Ordinal);

    public static void Initialize(string transport, string? serverInstanceId, bool providerConfigured)
    {
        lock (Sync)
        {
            _transport = transport;
            _serverInstanceId = serverInstanceId;
            _serverEpoch = Guid.NewGuid();
            var unavailableForTransport = transport == "stdio";
            _provider = new ProviderCapabilityObservation(
                providerConfigured ? "connecting" : "not_configured",
                unavailableForTransport ? "unsupported" : "unknown",
                null, null, null, null, null, null,
                providerConfigured ? "provider_handshake_pending" :
                    unavailableForTransport ? "requires_vsix_provider" : "provider_bootstrap_missing");
            FeatureProviders.Clear();
        }
    }

    /// <summary>この server instance の識別子。未初期化なら null。</summary>
    public static string? ServerInstanceId
    {
        get { lock (Sync) return _serverInstanceId; }
    }

    /// <summary>この server の世代。execution operation の server generation として使う。</summary>
    public static Guid? ServerEpoch
    {
        get { lock (Sync) return _serverEpoch; }
    }

    public static void SetProvider(ProviderCapabilityObservation observation)
    {
        lock (Sync)
        {
            if (_provider.Epoch != observation.Epoch || observation.State != "connected") FeatureProviders.Clear();
            _provider = observation;
        }
    }

    public static void SetFeatureProviderAvailability(string feature, string availability, string reason,
        string providerEpoch, DateTimeOffset observedAtUtc)
    {
        if (feature.Length is 0 or > 64 || availability is not ("supported" or "unsupported" or "unknown") ||
            reason.Length > 256 || providerEpoch.Length is 0 or > 64 || !CapabilityRegistry.ContainsFeature(feature) ||
            availability == "supported" && !CapabilityRegistry.CanReportSupported(feature)) return;
        lock (Sync)
        {
            if (_provider.State != "connected" || _provider.Epoch != providerEpoch) return;
            FeatureProviders[feature] = new FeatureProviderObservation(availability, reason, providerEpoch, observedAtUtc);
        }
    }

    public static CapabilityRuntimeObservation Capture(int? targetPid, long? targetStartTicks, WorkspaceCapabilityObservation workspace)
    {
        lock (Sync)
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            return new CapabilityRuntimeObservation(_transport, version, "assemblyVersion", _serverInstanceId, _serverEpoch,
                targetPid, targetStartTicks, workspace, _provider,
                new Dictionary<string, FeatureProviderObservation>(FeatureProviders, StringComparer.Ordinal),
                NormalizeDebuggerState("unknown"), "debugger_state_not_probed",
                NormalizeMutationState("unknown"), "mutation_lease_not_probed", DateTimeOffset.UtcNow);
        }
    }

    private static string NormalizeDebuggerState(string value) =>
        value is "running" or "break" or "design" or "busy" ? value : "unknown";

    private static string NormalizeMutationState(string value) => value is "idle" or "busy" ? value : "unknown";
}

/**
  宣言済みの機能と読み取り専用観測から、上限付きの能力情報を構成します
*/
internal static class CapabilityRegistry
{
    public const int MaximumFeatures = 32;
    public static readonly TimeSpan ProviderFreshness = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<CapabilityFeature> DeclaredFeatures = new[]
    {
        Feature("get_vs_capabilities", "implemented", true, true, "supported", "The capability contract is implemented without IDE side effects.", null, null, [], new Dictionary<string, int> { ["maxFeatures"] = MaximumFeatures, ["maxResponseCharacters"] = 65536 }),
        Feature("solution.read", "implemented", true, true, "unknown", "Requires a fresh read-only observation from the bound Visual Studio instance.", [], new Dictionary<string, int> { ["maxProjects"] = 100 }),
        Feature("diagnostics.read", "implemented", true, true, "unknown", "Error List provider capability has not been probed.", [], new Dictionary<string, int> { ["maxItems"] = 2000 }),
        Feature("output.read", "implemented", true, true, "unknown", "Output pane provider capability has not been probed.", [], new Dictionary<string, int> { ["maxPanes"] = 100, ["maxLines"] = 2000 }),
        Feature("debugger.inspect", "implemented", true, true, "unknown", "Debugger inspection requires a live bound DTE observation.", [], new Dictionary<string, int> { ["maxThreads"] = 64, ["maxFrames"] = 128, ["maxLocals"] = 40, ["maxDepth"] = 3 }),
        Feature("debugger.control", "implemented", true, true, "unknown", "A live debugger state and owner lease must be revalidated for each request.", ["allowDebuggerControl"], new Dictionary<string, int> { ["maxTimeoutMilliseconds"] = 10000 }),
        Feature("debugger.execution", "partial", false, true, "unsupported", "The execution coordinator has no provider dispatch adapter; legacy start and stop reject without side effects.", ["allowDebuggerControl", "allowLaunch", "allowBuild"], new Dictionary<string, int> { ["maxActiveOperations"] = 8, ["maxCompletedOperations"] = 64, ["completedTtlSeconds"] = 300 }),
        Feature("solution.build", "implemented", true, true, "unknown", "Build capability requires a fresh target and workspace observation.", ["allowBuild"], new Dictionary<string, int> { ["maxTimeoutMilliseconds"] = 30000 }),
        Feature("project.inventory", "implemented", true, true, "unknown", "Project inventory support depends on the active project system.", [], new Dictionary<string, int> { ["maxPageSize"] = 200, ["maxSnapshotCount"] = 8 }),
        Feature("project.mutation", "implemented", true, true, "unknown", "Project mutations require an exact fresh snapshot and workspace lease.", ["allowProjectMutation"], new Dictionary<string, int> { ["maxTraversalNodes"] = 20000, ["maxDepth"] = 32 }),
        Feature("expression.evaluation", "implemented", true, true, "unknown", "Expression evaluation is sensitive and requires per-request authorization.", ["allowEvaluation", "allowSideEffects", "allowSensitiveRead"], new Dictionary<string, int> { ["maxExpressionCharacters"] = 4096 }),
        Feature("documents.read", "not_implemented", false, false, "unsupported", "Live document buffers are not implemented.", [], new Dictionary<string, int> { ["maxLines"] = 500, ["maxCharacters"] = 32768 }),
        Feature("diagnostics.paging", "not_implemented", false, false, "unsupported", "Snapshot-backed diagnostics paging is not implemented.", [], new Dictionary<string, int> { ["maxPageSize"] = 200 }),
        Feature("output.incremental", "not_implemented", false, false, "unsupported", "Incremental output snapshots are not implemented.", [], new Dictionary<string, int> { ["maxPanes"] = 64, ["maxHistoryEntries"] = 10000 })
    };

    public static bool ContainsFeature(string key) => DeclaredFeatures.Any(feature => feature.Feature == key);
    public static bool CanReportSupported(string key) => DeclaredFeatures.Any(feature =>
        feature.Feature == key && feature.Implementation == "implemented");

    public static CapabilityDocument Create(CapabilityRuntimeObservation runtime, Func<DateTimeOffset>? utcNow = null)
    {
        var now = utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
        var features = runtime.Workspace.Kind == "openFolder"
            ? DeclaredFeatures.Select(feature => feature.Feature == "solution.build"
                ? feature with { Availability = "unsupported", Reason = "Open Folder CMake build dispatch is not bound by a documented public API." }
                : feature).Take(MaximumFeatures).ToArray()
            : DeclaredFeatures.Take(MaximumFeatures).ToArray();
        var provider = NormalizeProvider(runtime.Provider);
        var invalidFutureObservation = provider.ObservedAtUtc is { } observedAt && observedAt > now.AddSeconds(30);
        var age = provider.ObservedAtUtc is { } observed && !invalidFutureObservation
            ? Math.Max(0, (long)(now - observed).TotalMilliseconds)
            : (long?)null;
        var stale = invalidFutureObservation || age is { } ageMilliseconds && ageMilliseconds > ProviderFreshness.TotalMilliseconds;
        var providerAvailability = stale ? "unknown" : provider.Availability;
        var providerReason = stale
            ? invalidFutureObservation ? "provider_observation_time_invalid" : "provider_observation_stale"
            : provider.Reason;
        var reportedFeatures = features.Select(feature =>
        {
            if (feature.Feature == "get_vs_capabilities" || feature.Implementation == "not_implemented")
                return feature with { ProviderAvailability = null, ProviderReason = null };
            if (!runtime.FeatureProviders.TryGetValue(feature.Feature, out var observation))
                return feature with { ProviderAvailability = "unknown", ProviderReason = "provider_feature_capability_probe_unavailable" };
            if (runtime.Provider.State != "connected" || runtime.Provider.Epoch != observation.ProviderEpoch)
                return feature with { ProviderAvailability = "unknown", ProviderReason = "provider_epoch_changed" };
            var invalidTime = observation.ObservedAtUtc > now.AddSeconds(30);
            var observationAge = invalidTime ? TimeSpan.MaxValue : now - observation.ObservedAtUtc;
            var expired = invalidTime || observationAge > ProviderFreshness;
            var availability = expired ? "unknown" : observation.Availability;
            if (feature.Implementation != "implemented" ||
                feature.Feature == "solution.build" && runtime.Workspace.Kind == "openFolder")
                return feature with
                {
                    ProviderAvailability = availability,
                    ProviderReason = expired ? "provider_feature_observation_stale" : observation.Reason
                };
            return feature with
            {
                Availability = availability,
                Reason = expired ? "provider_feature_observation_stale" : observation.Reason,
                ProviderAvailability = availability,
                ProviderReason = expired ? "provider_feature_observation_stale" : observation.Reason
            };
        }).ToArray();
        return new CapabilityDocument(
            1,
            "Visual Studio Debug MCP",
            "unknown",
            "VSIX version is not supplied to the companion process.",
            "unknown",
            "Visual Studio product version is not supplied by the companion bootstrap.",
            runtime.ServerVersion,
            runtime.ServerVersionKind,
            runtime.Transport,
            runtime.VisualStudioPid,
            runtime.VisualStudioStartUtcTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            runtime.ServerInstanceId,
            runtime.ServerEpoch?.ToString("N"),
            runtime.Workspace.Status,
            runtime.Workspace.Kind,
            runtime.Workspace.Generation,
            runtime.Workspace.Reason,
            provider.State,
            providerAvailability,
            provider.Name,
            provider.Version,
            provider.VersionReason,
            provider.ProtocolVersion,
            provider.Epoch,
            provider.ObservedAtUtc,
            age,
            stale,
            providerReason,
            runtime.TemporaryDebuggerState,
            runtime.TemporaryDebuggerReason,
            runtime.TemporaryMutationState,
            runtime.TemporaryMutationReason,
            now,
            reportedFeatures);
    }

    private static ProviderCapabilityObservation NormalizeProvider(ProviderCapabilityObservation value)
    {
        var state = value.State is "not_configured" or "connecting" or "connected" or "disconnected" or "stopped"
            ? value.State : "unknown";
        var availability = value.Availability is "supported" or "unsupported" or "unknown"
            ? value.Availability : "unknown";
        return value with
        {
            State = state,
            Availability = availability,
            Name = Bound(value.Name, 64),
            Version = Bound(value.Version, 64),
            VersionReason = Bound(value.VersionReason, 256),
            Epoch = Bound(value.Epoch, 64),
            Reason = Bound(value.Reason, 256)
        };
    }

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maximumLength ? value : value[..maximumLength];

    private static CapabilityFeature Feature(string feature, string implementation, bool http, bool stdio, string availability,
        string reason, IReadOnlyList<string> permissions, IReadOnlyDictionary<string, int> limits) =>
        Feature(feature, implementation, http, stdio, availability, reason, null, null, permissions, limits);

    private static CapabilityFeature Feature(string feature, string implementation, bool http, bool stdio, string availability,
        string reason, string? providerAvailability, string? providerReason,
        IReadOnlyList<string> permissions, IReadOnlyDictionary<string, int> limits) =>
        new(feature, implementation, http, stdio, availability, reason, providerAvailability, providerReason, permissions, limits);
}
