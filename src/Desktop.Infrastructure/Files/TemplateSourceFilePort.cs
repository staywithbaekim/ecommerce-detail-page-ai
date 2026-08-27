using System.Buffers;
using System.Security.Cryptography;
using Desktop.Application.Files;
using Desktop.Application.Templates;

namespace Desktop.Infrastructure.Files;

public sealed class TemplateSourceFilePort(IPathPolicy pathPolicy) : ITemplateSourceFilePort
{
    private const int BufferSize = 128 * 1024;

    public async Task<SourceFingerprintResult> FingerprintAsync(
        string sourcePsdPath,
        CancellationToken cancellationToken)
    {
        if (!IsPsdPath(sourcePsdPath))
        {
            return SourceFingerprintResult.Failure(Error(
                "TPL_SOURCE_UNREADABLE",
                retryable: false,
                "Select a readable PSD file."));
        }

        try
        {
            var fingerprint = await ComputeFingerprintAsync(sourcePsdPath, cancellationToken)
                .ConfigureAwait(false);
            return fingerprint.Length > 0
                ? SourceFingerprintResult.Success(fingerprint)
                : SourceFingerprintResult.Failure(Error(
                    "TPL_SOURCE_UNREADABLE",
                    retryable: false,
                    "Select a non-empty PSD file."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SourceFingerprintResult.Failure(Error(
                "TPL_SOURCE_UNREADABLE",
                retryable: true,
                "Close applications writing the PSD or check its permissions, then retry."));
        }
    }

    public async Task<OwnedTemplateCopyResult> CopyVerifiedAsync(
        string sourcePsdPath,
        TemplateSourceFingerprint expectedSource,
        string destinationRelativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (!IsPsdPath(sourcePsdPath)
            || expectedSource.Length <= 0
            || !IsSha256(expectedSource.Sha256))
        {
            return ChangedDuringCopy();
        }

        string targetPath;
        try
        {
            targetPath = pathPolicy.ResolveOwnedPath(destinationRelativePath);
        }
        catch (InvalidOperationException)
        {
            return OwnedTemplateCopyResult.Failure(Error(
                "TPL_INTERNAL_PATH_INVALID",
                retryable: false,
                "Restart the template import."));
        }

        if (!IsPsdPath(targetPath))
        {
            return OwnedTemplateCopyResult.Failure(Error(
                "TPL_INTERNAL_PATH_INVALID",
                retryable: false,
                "Restart the template import."));
        }

        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("TPL_INTERNAL_PATH_INVALID");
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.part");
        OwnedTemplateCopyResult result;
        try
        {
            Directory.CreateDirectory(parent);
            if (File.Exists(targetPath))
            {
                var existing = await ComputeFingerprintAsync(targetPath, cancellationToken).ConfigureAwait(false);
                var sourceNow = await ComputeFingerprintAsync(sourcePsdPath, cancellationToken).ConfigureAwait(false);
                result = Matches(expectedSource, existing) && Matches(expectedSource, sourceNow)
                    ? OwnedTemplateCopyResult.Success(new OwnedTemplateCopy(
                        destinationRelativePath,
                        existing.Length,
                        existing.Sha256))
                    : ChangedDuringCopy();
            }
            else
            {
                var copied = await CopyAndHashAsync(sourcePsdPath, temporaryPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!Matches(expectedSource, copied))
                {
                    result = ChangedDuringCopy();
                }
                else
                {
                    var staged = await ComputeFingerprintAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                    var sourceAfter = await ComputeFingerprintAsync(sourcePsdPath, cancellationToken).ConfigureAwait(false);
                    if (!Matches(expectedSource, staged) || !Matches(expectedSource, sourceAfter))
                    {
                        result = ChangedDuringCopy();
                    }
                    else
                    {
                        File.Move(temporaryPath, targetPath, overwrite: false);
                        result = OwnedTemplateCopyResult.Success(new OwnedTemplateCopy(
                            destinationRelativePath,
                            staged.Length,
                            staged.Sha256));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = OwnedTemplateCopyResult.Failure(Error(
                "TPL_SOURCE_UNREADABLE",
                retryable: true,
                "Close applications writing the PSD or check local storage permissions, then retry."));
        }
        catch
        {
            TryDeleteTemporaryCopy(temporaryPath);
            throw;
        }

        return TryDeleteTemporaryCopy(temporaryPath)
            ? result
            : OwnedTemplateCopyResult.Failure(Error(
                "TPL_IMPORT_CLEANUP_REQUIRED",
                retryable: true,
                "Restart the application to recover the incomplete internal staging copy."));
    }

    public Task<bool> DeleteOwnedCopyAsync(
        string internalRelativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = pathPolicy.ResolveOwnedPath(internalRelativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(!File.Exists(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    private static async Task<TemplateSourceFingerprint> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = OpenReadOnly(sourcePath);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long length = 0;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                length += count;
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return new TemplateSourceFingerprint(
                length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<TemplateSourceFingerprint> ComputeFingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenReadOnly(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new TemplateSourceFingerprint(
            stream.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static FileStream OpenReadOnly(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool Matches(TemplateSourceFingerprint expected, TemplateSourceFingerprint actual) =>
        expected.Length == actual.Length
        && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsPsdPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool TryDeleteTemporaryCopy(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            return !File.Exists(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static OwnedTemplateCopyResult ChangedDuringCopy() => OwnedTemplateCopyResult.Failure(Error(
        "TPL_SOURCE_CHANGED_DURING_COPY",
        retryable: true,
        "Wait until the PSD is stable, then retry the import."));

    private static TemplateImportError Error(string code, bool retryable, string userAction) => new(
        code,
        retryable,
        "template-page",
        userAction);
}
