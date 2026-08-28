using System.Security.Cryptography;
using Desktop.Application.Inputs;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Inputs;

public sealed class StreamingAssetMetadataReader : IAssetMetadataReader
{
    public async Task<AssetMetadataReadResult> ReadAsync(
        string inputRoot,
        string sourceRelativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = InputPathGuard.ResolveApprovedFile(inputRoot, sourceRelativePath);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0)
            {
                return AssetMetadataReadResult.Blocked(CorruptAsset());
            }

            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var parsed = ImageMetadataParser.Parse(stream, Path.GetExtension(path));
            return AssetMetadataReadResult.Success(new AssetFileMetadata(
                stream.Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                parsed.Width,
                parsed.Height,
                ToDomainFormat(parsed.Format),
                parsed.MimeType));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return AssetMetadataReadResult.Blocked(CorruptAsset());
        }
        catch (EndOfStreamException)
        {
            return AssetMetadataReadResult.Blocked(CorruptAsset());
        }
        catch (DirectoryNotFoundException)
        {
            return AssetMetadataReadResult.Blocked(MissingAsset());
        }
        catch (FileNotFoundException)
        {
            return AssetMetadataReadResult.Blocked(MissingAsset());
        }
        catch (UnauthorizedAccessException)
        {
            return AssetMetadataReadResult.Blocked(UnreadableAsset());
        }
        catch (IOException)
        {
            return AssetMetadataReadResult.Blocked(UnreadableAsset());
        }
    }

    private static SourceImageFormat ToDomainFormat(string format) => format switch
    {
        "jpeg" => SourceImageFormat.Jpeg,
        "png" => SourceImageFormat.Png,
        "tiff" => SourceImageFormat.Tiff,
        _ => throw new InvalidDataException("INPUT_IMAGE_FORMAT_UNSUPPORTED")
    };

    private static ScanBlockReason CorruptAsset() => ScanBlockReason.Create(
        "INPUT_ASSET_CORRUPT",
        ScanBlockCategory.SourceAsset,
        "发现损坏、格式不符或无法识别的商品图片。请替换该文件后重新扫描。");

    private static ScanBlockReason MissingAsset() => ScanBlockReason.Create(
        "INPUT_ASSET_DISAPPEARED",
        ScanBlockCategory.PathAccess,
        "扫描期间有商品图片或目录消失。请确认存储设备连接正常后重试。");

    private static ScanBlockReason UnreadableAsset() => ScanBlockReason.Create(
        "INPUT_ASSET_UNREADABLE",
        ScanBlockCategory.PathAccess,
        "无法读取部分商品图片。请检查文件权限或存储设备状态后重试。");
}
