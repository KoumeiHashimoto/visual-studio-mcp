namespace VisualStudioMcpServer;

/// <summary>Process-wide serialization for build and solution file mutations.</summary>
internal sealed class WorkspaceMutationGate
{
    internal static readonly WorkspaceMutationGate Shared = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal bool TryAcquire() => _semaphore.Wait(0);

    internal Task<bool> TryAcquireAsync(CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(0, cancellationToken);

    internal void Release() => _semaphore.Release();

    /// <summary>
    /// Retains the lease until queued COM work has settled. A failed cleanup task
    /// deliberately leaves the gate closed so a possibly active mutation cannot overlap.
    /// </summary>
    internal Task ReleaseAfter(Task cleanup) => ReleaseAfterCoreAsync(cleanup);

    private async Task ReleaseAfterCoreAsync(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch { return; }
        Release();
    }
}
