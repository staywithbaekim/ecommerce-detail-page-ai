using Desktop.Infrastructure.Files;
using Desktop.Infrastructure.Templates;

namespace Desktop.Infrastructure.Tests;

public sealed class TemplateOwnedStorageTests
{
    [Fact]
    public async Task WindowsFileLockKeepsOriginalDirectoryUntilStagingCanSucceed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var paths = new LocalAppPathPolicy(directory.Path);
        var groupId = "group_locked_delete";
        var originalRelativePath = $"templates/groups/{groupId}";
        var lockedFile = paths.ResolveOwnedPath($"{originalRelativePath}/revisions/r1/template.psd");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedFile)!);
        await File.WriteAllBytesAsync(lockedFile, [1, 2, 3, 4]);
        var storage = new TemplateOwnedStorage(paths);

        await using (var locked = new FileStream(
                         lockedFile,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                storage.StageGroupFilesForDeletionAsync(groupId, CancellationToken.None));

            Assert.Equal("TPL_DELETE_FILES_LOCKED", failure.Message);
            Assert.True(Directory.Exists(paths.ResolveOwnedPath(originalRelativePath)));
        }

        var stage = await storage.StageGroupFilesForDeletionAsync(groupId, CancellationToken.None);
        Assert.NotNull(stage);
        Assert.False(Directory.Exists(paths.ResolveOwnedPath(originalRelativePath)));
        Assert.True(Directory.Exists(paths.ResolveOwnedPath(stage!.StagedRelativePath)));

        await storage.RestoreGroupFilesAsync(stage, CancellationToken.None);
        Assert.True(Directory.Exists(paths.ResolveOwnedPath(originalRelativePath)));
        Assert.False(Directory.Exists(paths.ResolveOwnedPath(stage.StagedRelativePath)));

        var finalStage = await storage.StageGroupFilesForDeletionAsync(groupId, CancellationToken.None);
        Assert.NotNull(finalStage);
        await storage.CommitGroupFilesDeletionAsync(finalStage!, CancellationToken.None);
        Assert.False(Directory.Exists(paths.ResolveOwnedPath(originalRelativePath)));
        Assert.False(Directory.Exists(paths.ResolveOwnedPath(finalStage!.StagedRelativePath)));
    }
}
