/**
  能力情報の不変な転送モデルを定義します
*/
namespace VisualStudioMcpServer.Capabilities;

internal sealed record WorkspaceCapabilityObservation(string Status, string? Kind, long? Generation, string? Reason);

internal sealed record ProviderCapabilityObservation(
    string State,
    string Availability,
    string? Name,
    string? Version,
    string? VersionReason,
    int? ProtocolVersion,
    string? Epoch,
    DateTimeOffset? ObservedAtUtc,
    string? Reason);

internal sealed record FeatureProviderObservation(string Availability, string Reason, string ProviderEpoch, DateTimeOffset ObservedAtUtc);

internal sealed record CapabilityRuntimeObservation(
    string Transport,
    string ServerVersion,
    string ServerVersionKind,
    string? ServerInstanceId,
    Guid? ServerEpoch,
    int? VisualStudioPid,
    long? VisualStudioStartUtcTicks,
    WorkspaceCapabilityObservation Workspace,
    ProviderCapabilityObservation Provider,
    IReadOnlyDictionary<string, FeatureProviderObservation> FeatureProviders,
    string TemporaryDebuggerState,
    string TemporaryDebuggerReason,
    string TemporaryMutationState,
    string TemporaryMutationReason,
    DateTimeOffset ObservedAtUtc);

internal sealed record CapabilityFeature(
    string Feature,
    string Implementation,
    bool HttpExposed,
    bool StdioExposed,
    string Availability,
    string Reason,
    string? ProviderAvailability,
    string? ProviderReason,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyDictionary<string, int> Limits);

internal sealed record CapabilityDocument(
    int SchemaVersion,
    string Product,
    string VsixVersion,
    string VsixVersionReason,
    string VisualStudioVersion,
    string VisualStudioVersionReason,
    string ServerVersion,
    string ServerVersionKind,
    string Transport,
    int? VisualStudioPid,
    string? VisualStudioStartUtcTicks,
    string? ServerInstanceId,
    string? ServerEpoch,
    string WorkspaceStatus,
    string? WorkspaceKind,
    long? WorkspaceGeneration,
    string? WorkspaceReason,
    string ProviderState,
    string ProviderAvailability,
    string? ProviderName,
    string? ProviderVersion,
    string? ProviderVersionReason,
    int? ProviderProtocolVersion,
    string? ProviderEpoch,
    DateTimeOffset? ProviderObservedAtUtc,
    long? ProviderAgeMilliseconds,
    bool? ProviderObservationStale,
    string? ProviderReason,
    string TemporaryDebuggerState,
    string TemporaryDebuggerReason,
    string TemporaryMutationState,
    string TemporaryMutationReason,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<CapabilityFeature> Features);
