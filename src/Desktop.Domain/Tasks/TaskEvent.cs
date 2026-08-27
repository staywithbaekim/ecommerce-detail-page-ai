namespace Desktop.Domain.Tasks;

public sealed record TaskEvent(
    Guid EventId,
    string TaskId,
    long Sequence,
    TaskEventType Type,
    DateTimeOffset OccurredAt,
    string? PageId = null,
    string PayloadDigest = "")
{
    public static TaskEvent Create(
        string taskId,
        long sequence,
        TaskEventType type,
        string? pageId = null,
        string payloadDigest = "") =>
        new(Guid.NewGuid(), taskId, sequence, type, DateTimeOffset.UtcNow, pageId, payloadDigest);
}
