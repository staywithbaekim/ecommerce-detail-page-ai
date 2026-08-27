using System.Collections.Immutable;

namespace Desktop.Domain.Tasks;

public sealed record TaskState(
    string TaskId,
    TaskStatus Status,
    long LastSequence,
    ImmutableHashSet<Guid> AppliedEventIds,
    ImmutableHashSet<string> CommittedPageIds,
    int IssueCount)
{
    public static TaskState Empty(string taskId) => new(
        taskId,
        TaskStatus.None,
        0,
        ImmutableHashSet<Guid>.Empty,
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
        0);

    public int OutputCount => CommittedPageIds.Count;

    public bool IsTerminal => Status is TaskStatus.Failed
        or TaskStatus.Cancelled
        or TaskStatus.Completed
        or TaskStatus.CompletedWithIssues;
}
