/**
  プロジェクト関連の探索予算と期限検査を共有します。
*/
namespace VisualStudioMcpServer;

internal sealed class ProjectTraversalBudget
{
    internal const int MaxNodes = 10000;
    internal const int MaxDepth = 32;

    private int _visited;

    internal int VisitedCount => _visited;

    internal bool TryVisit()
    {
        if (_visited >= MaxNodes) return false;
        _visited++;
        return true;
    }

    /**
      呼び出し期限とキャンセル状態を確認します。
    */
    internal static void CheckDeadline(DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The project traversal deadline expired.");
    }
}
