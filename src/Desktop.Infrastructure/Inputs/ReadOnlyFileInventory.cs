using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Inputs;

public sealed class ReadOnlyFileInventory : IFileInventory
{
    public Task<FileInventoryReadResult> ReadApprovedLevelsAsync(
        string inputRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(FileInventoryReadResult.Success(
                InputInventoryReader.ReadApprovedLevels(inputRoot)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(FileInventoryReadResult.Blocked(MissingDirectory()));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(FileInventoryReadResult.Blocked(MissingDirectory()));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(FileInventoryReadResult.Blocked(PathRejected()));
        }
        catch (IOException)
        {
            return Task.FromResult(FileInventoryReadResult.Blocked(PathRejected()));
        }
    }

    private static ScanBlockReason MissingDirectory() => ScanBlockReason.Create(
        "INPUT_DIRECTORY_DISAPPEARED",
        ScanBlockCategory.PathAccess,
        "所选目录已不存在或暂时不可用。请重新连接存储设备或重新选择目录。");

    private static ScanBlockReason PathRejected() => ScanBlockReason.Create(
        "INPUT_DIRECTORY_ACCESS_DENIED",
        ScanBlockCategory.PathAccess,
        "无法安全读取所选目录。请检查访问权限，并选择不包含链接或受保护位置的目录。");
}
