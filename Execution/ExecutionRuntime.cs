using VisualStudioMcpServer.Capabilities;
using VisualStudioMcpServer.Coordination;
using VisualStudioMcpServer.Providers;

namespace VisualStudioMcpServer.Execution;

/// <summary>
/// server 内で 1 つだけの execution coordinator。HTTP と stdio の tool はすべてこの coordinator を通る。
/// provider の execution.observed は、同じ server 世代・IDE identity の operation だけを確定させる。
/// </summary>
internal static class ExecutionRuntime
{
    private static readonly Lazy<ExecutionOperationCoordinator> Instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static ExecutionOperationCoordinator Coordinator => Instance.Value;

    internal static (int ProcessId, long StartTicks)? Target() =>
        VisualStudioConnector.TryGetTargetProcessIdentity(out var processId, out var startTicks) ? (processId, startTicks) : null;

    private static ExecutionOperationCoordinator Create()
    {
        var adapter = new ProviderExecutionAdapter(
            () => ProviderRuntime.Channel,
            Target,
            () => CapabilityRuntimeContext.ServerInstanceId,
            () => CapabilityRuntimeContext.ServerEpoch,
            () => TargetMutationOwner.IsCurrentOwner ? TargetMutationOwner.CurrentOwnerFence : null);
        var coordinator = new ExecutionOperationCoordinator(adapter, () => TargetMutationOwner.IsCurrentOwner);
        ProviderRuntime.ExecutionObserved = (_, body) => Report(coordinator, body.OperationId, ProviderExecutionAdapter.MapObserved(body));
        ProviderRuntime.ExecutionSettled = (_, record) =>
        {
            // timeout 後の遅延応答が「未 dispatch」と確定した場合だけ、同じ operation を失敗として確定する。再送はしない。
            if (record.DispatchState == ProviderDispatchState.NotDispatched)
                Report(coordinator, record.OperationId, new ExecutionDispatchObservation(ExecutionState.Failed,
                    record.Code ?? ProviderErrorCodes.InternalError, "The provider later confirmed that the action was not dispatched.", false));
        };
        return coordinator;
    }

    private static void Report(ExecutionOperationCoordinator coordinator, string operationId, ExecutionDispatchObservation observation)
    {
        var target = Target();
        var serverInstanceId = CapabilityRuntimeContext.ServerInstanceId;
        var serverEpoch = CapabilityRuntimeContext.ServerEpoch;
        if (target is null || serverInstanceId is null || serverEpoch is null) return;
        coordinator.ReportObservation(operationId, target.Value.ProcessId, target.Value.StartTicks, serverInstanceId,
            serverEpoch.Value, observation);
    }
}
