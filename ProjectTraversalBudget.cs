namespace VisualStudioMcpServer;

internal sealed class ProjectTraversalBudget
{
    internal const int MaxNodes = 10000;

    private int _visited;

    internal int VisitedCount => _visited;

    internal bool TryVisit()
    {
        if (_visited >= MaxNodes) return false;
        _visited++;
        return true;
    }
}
