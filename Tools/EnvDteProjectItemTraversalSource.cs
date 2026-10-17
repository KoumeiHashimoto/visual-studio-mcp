/**
  EnvDTEのProjectItemsを上限付きプロジェクト項目探索へ接続します。
*/
using System.Runtime.InteropServices;
using EnvDTE;

namespace VisualStudioMcpServer.Tools;

/**
  EnvDTEのProjectItemsを項目探索用の読み取り元へ接続します。
*/
internal sealed class EnvDteProjectItemTraversalSource : IProjectItemTraversalSource<ProjectItems, ProjectItem>
{
    /**
      コレクション件数を取得します。
    */
    public int GetCount(ProjectItems collection)
    {
        try { return collection.Count; }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      指定位置のプロジェクト項目を取得します。
    */
    public ProjectItem? GetItem(ProjectItems collection, int oneBasedIndex)
    {
        try { return collection.Item(oneBasedIndex); }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      プロジェクト項目名を取得します。
    */
    public string GetName(ProjectItem item)
    {
        try { return item.Name; }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      プロジェクト項目が持つ物理ファイル数を取得します。
    */
    public int GetFileCount(ProjectItem item)
    {
        try { return item.FileCount; }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      プロジェクト項目の物理ファイル名を取得します。
    */
    public string GetFileName(ProjectItem item, int oneBasedIndex)
    {
        try { return item.FileNames[checked((short)oneBasedIndex)]; }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      子のプロジェクト項目コレクションを取得します。
    */
    public ProjectItems? GetChildren(ProjectItem item)
    {
        try { return item.ProjectItems; }
        catch (COMException) { throw ReadFailed(); }
    }

    /**
      EnvDTEコレクションの読み取り失敗を構造化エラーへ変換します。
    */
    private static ProjectItemTraversalException ReadFailed() =>
        new("operation_failed", "Visual Studio could not complete the bounded project-file traversal.");
}
