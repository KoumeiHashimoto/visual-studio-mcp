/**
  プロジェクト項目の再帰探索と登録状態確認を上限付きで実行します。
*/
namespace VisualStudioMcpServer.Tools;

/**
  登録済みプロジェクト項目を上限付きで再帰探索します。
*/
internal interface IProjectItemTraversalSource<TCollection, TItem>
    where TCollection : class
    where TItem : class
{
    /**
      項目コレクションの件数を取得します。
    */
    int GetCount(TCollection collection);

    /**
      1始まりの位置で項目を取得します。
    */
    TItem? GetItem(TCollection collection, int oneBasedIndex);

    /**
      項目名を取得します。
    */
    string GetName(TItem item);

    /**
      項目が持つ物理ファイル数を取得します。
    */
    int GetFileCount(TItem item);

    /**
      1始まりの位置で物理ファイル名を取得します。
    */
    string GetFileName(TItem item, int oneBasedIndex);

    /**
      子項目のコレクションを取得します。
    */
    TCollection? GetChildren(TItem item);
}

/**
  プロジェクト項目の識別付き探索と登録状態確認を行います。
*/
internal static class ProjectItemTraversal
{
    /**
      論理パスと物理パスが一致する項目を、共有予算と期限内で探します。
    */
    internal static TItem? FindItem<TCollection, TItem>(TCollection? items, string logicalPath, string parent,
        string expectedPath, int depth, ProjectTraversalBudget traversal, DateTimeOffset deadline,
        CancellationToken cancellationToken, IProjectItemTraversalSource<TCollection, TItem> source)
        where TCollection : class
        where TItem : class
    {
        ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
        if (items is null) return null;
        if (depth > ProjectTraversalBudget.MaxDepth) throw TraversalLimitExceeded();
        var count = source.GetCount(items);
        ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
        for (var index = 1; index <= count; index++)
        {
            VisitTraversalNode(traversal, deadline, cancellationToken);
            var item = source.GetItem(items, index) ?? throw TraversalReadFailed();
            ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
            var current = parent + "\\" + source.GetName(item);
            ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
            if (string.Equals(current, logicalPath, StringComparison.OrdinalIgnoreCase))
            {
                var fileCount = source.GetFileCount(item);
                ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
                if (fileCount == 1)
                {
                    var fileName = source.GetFileName(item, 1);
                    ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
                    if (PathsEqual(fileName, expectedPath)) return item;
                }
            }
            var found = FindItem(source.GetChildren(item), logicalPath, current, expectedPath, depth + 1,
                traversal, deadline, cancellationToken, source);
            if (found is not null) return found;
        }
        return null;
    }

    /**
      指定された物理ファイルが登録されているかを、共有予算と期限内で調べます。
    */
    internal static bool ContainsFile<TCollection, TItem>(TCollection? items, string expectedPath, int depth,
        ProjectTraversalBudget traversal, DateTimeOffset deadline, CancellationToken cancellationToken,
        IProjectItemTraversalSource<TCollection, TItem> source)
        where TCollection : class
        where TItem : class
    {
        ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
        if (items is null) return false;
        if (depth > ProjectTraversalBudget.MaxDepth) throw TraversalLimitExceeded();
        var count = source.GetCount(items);
        ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
        for (var index = 1; index <= count; index++)
        {
            VisitTraversalNode(traversal, deadline, cancellationToken);
            var item = source.GetItem(items, index) ?? throw TraversalReadFailed();
            ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
            var fileCount = source.GetFileCount(item);
            ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
            if (fileCount == 1)
            {
                var fileName = source.GetFileName(item, 1);
                ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
                if (PathsEqual(fileName, expectedPath)) return true;
            }
            var children = source.GetChildren(item);
            ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
            if (ContainsFile(children, expectedPath, depth + 1, traversal, deadline, cancellationToken, source)) return true;
        }
        return false;
    }

    /**
      期限を確認して共有予算から1ノードを予約します。
    */
    /**
      期限を確認して共有予算から1ノードを予約します。
    */
    internal static void VisitTraversalNode(ProjectTraversalBudget traversal, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ProjectTraversalBudget.CheckDeadline(deadline, cancellationToken);
        if (!traversal.TryVisit()) throw TraversalLimitExceeded();
    }

    /**
      ノード数または深度上限を超えた結果を返します。
    */
    private static ProjectItemTraversalException TraversalLimitExceeded() =>
        new("operation_failed", "Project-file operation exceeded the 10000-node traversal limit or depth limit.");

    /**
      プロジェクト項目の物理パスを比較します。
    */
    private static bool PathsEqual(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /**
      項目列挙を完了できなかったエラーを作成します。
    */
    private static ProjectItemTraversalException TraversalReadFailed() =>
        new("operation_failed", "Visual Studio could not complete the bounded project-file traversal.");
}

/**
  プロジェクト項目探索の構造化エラーを保持します。
*/
internal sealed class ProjectItemTraversalException(string code, string message) : Exception(message)
{
    /**
      呼び出し側へ返すエラーコードを取得します。
    */
    internal string Code { get; } = code;
}
