using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Templates;

namespace Desktop.Infrastructure.Templates;

/// <summary>
/// Read-only, bounded fingerprinting for the reference photo folder used by
/// PSD learning.  It never follows directories, writes thumbnails, or keeps
/// image bytes in memory beyond a small header buffer.
/// </summary>
public sealed class ReferencePhotoFingerprintPort : IReferencePhotoFingerprintPort
{
    private const int HeaderBytes = 64 * 1024;
    private const int MaxPhotoCount = 512;
    private const long MaxPhotoBytes = 256L * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff"
    };

    public async Task<ReferencePhotoInventory> ScanAsync(
        string referencePhotoDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(referencePhotoDirectory)
            || !Path.IsPathFullyQualified(referencePhotoDirectory))
        {
            throw new InvalidOperationException("TPL_LEARN_REFERENCE_FOLDER_INVALID");
        }

        var root = Path.GetFullPath(referencePhotoDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var paths = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Extensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidOperationException("TPL_LEARN_REFERENCE_PHOTOS_MISSING");
        }

        if (paths.Length > MaxPhotoCount)
        {
            throw new InvalidOperationException("TPL_LEARN_REFERENCE_PHOTO_LIMIT");
        }

        var photos = new List<ReferencePhotoFingerprint>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(root, path));
            var photo = await FingerprintAsync(path, relative, cancellationToken).ConfigureAwait(false);
            photos.Add(photo);
        }

        var digestPayload = string.Join(
            "\n",
            photos.Select(photo => string.Join(
                "|",
                photo.RelativePath,
                photo.Length,
                photo.Sha256,
                photo.MimeType,
                photo.Width,
                photo.Height)));
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(digestPayload))).ToLowerInvariant()}";
        return new ReferencePhotoInventory(digest, photos);
    }

    private static async Task<ReferencePhotoFingerprint> FingerprintAsync(
        string path,
        string relative,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaxPhotoBytes)
        {
            throw new InvalidOperationException("TPL_LEARN_REFERENCE_PHOTO_INVALID");
        }

        var header = new byte[(int)Math.Min(HeaderBytes, stream.Length)];
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var mime = DetectMime(path, header.AsSpan(0, read));
        var dimensions = ReadDimensions(mime, header.AsSpan(0, read));
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            throw new InvalidOperationException("TPL_LEARN_REFERENCE_PHOTO_DIMENSIONS_INVALID");
        }

        var digest = Convert.ToHexString(hash).ToLowerInvariant();
        var identityDigest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{digest}\n{relative}"))).ToLowerInvariant();
        return new ReferencePhotoFingerprint(
            $"asset_{identityDigest[..24]}",
            Path.GetFileName(path),
            relative,
            stream.Length,
            digest,
            mime,
            dimensions.Width,
            dimensions.Height);
    }

    private static string DetectMime(string path, ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8
            && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 12
            && header[..4].SequenceEqual("RIFF"u8)
            && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return "image/bmp";
        }

        if (header.Length >= 4
            && (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8)))
        {
            return "image/tiff";
        }

        throw new InvalidOperationException($"TPL_LEARN_REFERENCE_PHOTO_MIME_INVALID:{Path.GetFileName(path)}");
    }

    private static (int Width, int Height) ReadDimensions(string mime, ReadOnlySpan<byte> data)
    {
        if (mime == "image/png" && data.Length >= 24)
        {
            return (
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4))),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4))));
        }

        if (mime == "image/bmp" && data.Length >= 26)
        {
            var width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
            var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4)));
            return (width, height);
        }

        if (mime == "image/jpeg")
        {
            return ReadJpegDimensions(data);
        }

        // WebP/TIFF dimension decoding is not part of this bounded reader yet.
        // Reject instead of inventing dimensions or promoting the file to an
        // exact match with fabricated geometry evidence.
        return (0, 0);
    }

    private static (int Width, int Height) ReadJpegDimensions(ReadOnlySpan<byte> data)
    {
        var index = 2;
        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            while (index < data.Length && data[index] == 0xFF)
            {
                index++;
            }

            if (index >= data.Length)
            {
                break;
            }

            var marker = data[index++];
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (index + 2 > data.Length)
            {
                break;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            if (segmentLength < 2 || index + segmentLength > data.Length)
            {
                break;
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xC3
                or >= 0xC5 and <= 0xC7
                or >= 0xC9 and <= 0xCB
                or >= 0xCD and <= 0xCF;
            if (isStartOfFrame && segmentLength >= 7)
            {
                return (
                    BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index + 3, 2)));
            }

            index += segmentLength;
        }

        return (0, 0);
    }

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
