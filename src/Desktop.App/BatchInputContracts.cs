using System.ComponentModel;
using Desktop.Application.Tasks;

namespace Desktop.App;

public enum BatchInputUiStatus
{
    Draft,
    Scanning,
    ScanReady,
    ScanBlocked
}

public enum BatchSelectionMode
{
    Automatic,
    Manual
}

public enum BatchOutputMode { UnifiedDirectory, StyleSourceDirectories }

public sealed record BatchInputSubmission(
    string? BatchId,
    string TemplateGroupId,
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<string> SelectedPageIds,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    BatchOutputMode? OutputMode = null);

public sealed record BatchInputProjection(
    string BatchId,
    string? TemplateGroupId,
    BatchInputUiStatus Status,
    string InputDirectory,
    string OutputDirectory,
    IReadOnlyList<string> SelectedPageIds,
    BatchSelectionMode SelectionMode,
    bool IncludeEditablePsd,
    string RecognitionText,
    string StatusMessage,
    BatchOutputMode? OutputMode = null);

// Persisted task events are read without starting analysis or render recovery.
// No event projection means input-stage metadata only, not a completed task.
public sealed record BatchRecoverySummary(
    string BatchId,
    string? TemplateGroupId,
    string InputDirectory,
    BatchInputUiStatus Status,
    DateTimeOffset UpdatedAt)
{
    public TaskProjection? Task { get; init; }
    public bool TaskStateUnavailable { get; init; }
    public DateTimeOffset LatestUpdatedAt => !TaskStateUnavailable
        && Task is { UpdatedAt: { } updated } && Task.TaskId == BatchId && updated > UpdatedAt
            ? updated : UpdatedAt;
}

public interface IBatchInputWorkflow
{
    Task<BatchInputProjection?> RestoreLatestDraftAsync(CancellationToken cancellationToken);

    // Older hosts can still implement latest-only recovery. Do not silently
    // present that single batch as a complete history or substitute it by id.
    Task<IReadOnlyList<BatchRecoverySummary>> ListRecoverableAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("INPUT_BATCH_LIST_UNAVAILABLE");

    Task<BatchInputProjection?> RestoreBatchAsync(string batchId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("INPUT_BATCH_SELECTION_UNAVAILABLE");

    Task<BatchInputProjection> ScanAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken);

    Task<BatchInputProjection> SaveDraftAsync(
        BatchInputSubmission submission,
        CancellationToken cancellationToken);
}

public sealed class BatchPageOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public BatchPageOptionViewModel(string pageId, string name, string meta, bool isSelected)
    {
        PageId = pageId;
        Name = name;
        Meta = meta;
        _isSelected = isSelected;
    }

    public string PageId { get; }

    public string Name { get; }

    public string Meta { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
