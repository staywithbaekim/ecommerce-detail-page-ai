using Desktop.Application.Files;
using Desktop.Application.Templates;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Templates;

public sealed class TemplateOwnedStorage(IPathPolicy paths) : ITemplateOwnedStorage
{
    private const int MaxAttempts = 3;

    public async Task<TemplateOwnedDeletionStage?> StageGroupFilesForDeletionAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID");
        cancellationToken.ThrowIfCancellationRequested();
        var originalRelativePath = $"templates/groups/{groupId}";
        var originalDirectory = paths.ResolveOwnedPath(originalRelativePath);
        if (!Directory.Exists(originalDirectory))
        {
            return null;
        }

        var stagedRelativePath = $"templates/deleting/{groupId}-{Guid.NewGuid():N}";
        var stagedDirectory = paths.ResolveOwnedPath(stagedRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedDirectory)!);
        await MoveDirectoryWithRetryAsync(
            originalDirectory,
            stagedDirectory,
            "TPL_DELETE_FILES_LOCKED",
            cancellationToken).ConfigureAwait(false);
        return new TemplateOwnedDeletionStage(groupId, originalRelativePath, stagedRelativePath);
    }

    public async Task CommitGroupFilesDeletionAsync(
        TemplateOwnedDeletionStage stage,
        CancellationToken cancellationToken)
    {
        ValidateStage(stage);
        var stagedDirectory = paths.ResolveOwnedPath(stage.StagedRelativePath);
        if (!Directory.Exists(stagedDirectory))
        {
            return;
        }

        await DeleteDirectoryWithRetryAsync(stagedDirectory, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreGroupFilesAsync(
        TemplateOwnedDeletionStage stage,
        CancellationToken cancellationToken)
    {
        ValidateStage(stage);
        var originalDirectory = paths.ResolveOwnedPath(stage.OriginalRelativePath);
        var stagedDirectory = paths.ResolveOwnedPath(stage.StagedRelativePath);
        if (!Directory.Exists(stagedDirectory))
        {
            return;
        }

        if (Directory.Exists(originalDirectory))
        {
            throw new IOException("TPL_DELETE_ROLLBACK_CONFLICT");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(originalDirectory)!);
        await MoveDirectoryWithRetryAsync(
            stagedDirectory,
            originalDirectory,
            "TPL_DELETE_ROLLBACK_FAILED",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteGroupFilesAsync(string groupId, CancellationToken cancellationToken)
    {
        var stage = await StageGroupFilesForDeletionAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (stage is not null)
        {
            await CommitGroupFilesDeletionAsync(stage, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string sourceDirectory,
        string destinationDirectory,
        string errorCode,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var retryDelay = TimeSpan.FromMilliseconds(100);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay += retryDelay;
                }

                Directory.Move(sourceDirectory, destinationDirectory);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
            }
        }

        throw new IOException(errorCode, lastError);
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var retryDelay = TimeSpan.FromMilliseconds(100);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay += retryDelay;
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lastError = error;
            }
        }

        throw new IOException("TPL_DELETE_FILES_LOCKED", lastError);
    }

    private static void ValidateStage(TemplateOwnedDeletionStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        TemplateValueRules.Identifier(stage.GroupId, "TPL_GROUP_ID_INVALID");
        var expectedOriginal = $"templates/groups/{stage.GroupId}";
        var expectedStagingPrefix = $"templates/deleting/{stage.GroupId}-";
        var stageId = stage.StagedRelativePath.Length > expectedStagingPrefix.Length
            ? stage.StagedRelativePath[expectedStagingPrefix.Length..]
            : string.Empty;
        if (!string.Equals(stage.OriginalRelativePath, expectedOriginal, StringComparison.Ordinal)
            || !stage.StagedRelativePath.StartsWith(expectedStagingPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(stageId, "N", out _))
        {
            throw new InvalidOperationException("TPL_DELETE_STAGE_INVALID");
        }
    }
}
