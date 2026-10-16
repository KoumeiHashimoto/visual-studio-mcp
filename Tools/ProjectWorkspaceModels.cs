namespace VisualStudioMcpServer.Tools;

internal sealed record ProjectInfoRecord(
    string ProjectId, string Name, string? UniqueName, string? ProjectPath, string? Kind,
    string? ParentProjectId, string LogicalPath, bool IsSolutionFolder, bool? IsLoaded,
    bool CanOpen, bool CanMutate, bool? IsSaved, bool? IsReadOnly,
    string? Configuration, string? Platform);

internal sealed record ProjectFileRecord(
    string ItemId, string Name, string LogicalPath, string? FullPath,
    IReadOnlyList<string> AssociatedFiles, bool AssociatedFilesIncomplete,
    string? Kind, string? ParentItemId, bool IsFolder, int FileCount, int ChildCount,
    bool? IsLinked, bool? IsGenerated, bool? IsReadOnly, string? ItemType,
    bool CanOpen, bool CanMutate);

internal sealed record ProjectInfoResult(
    bool Success, string Code, string Message, int? TargetProcessId, string? SolutionPath,
    string? SnapshotId, IReadOnlyList<ProjectInfoRecord>? Items, ProjectInfoRecord? Project,
    string? NextCursor, bool HasMore, bool Incomplete, int CapturedCount,
    int? TotalCount, IReadOnlyList<string> Warnings, long ElapsedMilliseconds);

internal sealed record ProjectFilesResult(
    bool Success, string Code, string Message, int? TargetProcessId, string? SolutionPath,
    string? ProjectPath, string? SnapshotId, IReadOnlyList<ProjectFileRecord>? Items,
    string? NextCursor, bool HasMore, bool Incomplete, int CapturedCount,
    int? TotalCount, IReadOnlyList<string> Warnings, long ElapsedMilliseconds);

internal sealed record ProjectSnapshotData(
    string SnapshotId, int TargetProcessId, long TargetProcessStartUtcTicks,
    string SolutionPath, IReadOnlyList<ProjectInfoRecord> Projects, bool Incomplete,
    int? TotalCount, IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt);

internal sealed record FileSnapshotData(
    string SnapshotId, string ProjectSnapshotId, string ProjectId,
    int TargetProcessId, long TargetProcessStartUtcTicks, string SolutionPath,
    string ProjectPath, IReadOnlyList<ProjectFileRecord> Items, bool Incomplete,
    int? TotalCount, IReadOnlyList<string> Warnings, DateTimeOffset CreatedAt);

internal sealed record SnapshotCursor(string Kind, string SnapshotId, string? ProjectId,
    string QueryKey, int Offset);
