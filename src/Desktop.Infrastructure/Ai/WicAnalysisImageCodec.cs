using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Desktop.Application.Ai;
using Desktop.Domain.Inputs;

namespace Desktop.Infrastructure.Ai;

public sealed record AiAnalysisCopyEncodingOptions(
    int MaxLongestEdge = 1280,
    int JpegQuality = 82,
    long MaxSourceBytes = 64L * 1024 * 1024,
    long MaxOutputBytes = 8L * 1024 * 1024,
    long MaxSourcePixels = 100_000_000,
    string CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV4)
{
    public void Validate()
    {
        if (MaxLongestEdge is < 256 or > 8192
            || JpegQuality is < 40 or > 100
            || MaxSourceBytes is < 1024 or > 512L * 1024 * 1024
            || MaxOutputBytes is < 1024 or > 64L * 1024 * 1024
            || MaxSourcePixels is < 1_000_000 or > 500_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(AiAnalysisCopyEncodingOptions));
        }

        if (string.IsNullOrWhiteSpace(CompressionProfileVersion)
            || CompressionProfileVersion.Length > 128
            || CompressionProfileVersion.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or '-')))
        {
            throw new ArgumentException("AI_COPY_COMPRESSION_PROFILE_INVALID", nameof(CompressionProfileVersion));
        }

        if ((AiAnalysisCopyProfiles.IsWicBoundedV1(CompressionProfileVersion)
                || AiAnalysisCopyProfiles.IsWicBoundedV2(CompressionProfileVersion))
            && (MaxLongestEdge != 1600
                || JpegQuality != 82
                || MaxSourceBytes != 64L * 1024 * 1024
                || MaxOutputBytes != 8L * 1024 * 1024
                || MaxSourcePixels != 100_000_000))
        {
            throw new InvalidOperationException("AI_COPY_COMPRESSION_PROFILE_OPTIONS_MISMATCH");
        }

        if (AiAnalysisCopyProfiles.IsWicBoundedV3(CompressionProfileVersion)
            && (MaxLongestEdge != 1280
                || JpegQuality != 76
                || MaxSourceBytes != 64L * 1024 * 1024
                || MaxOutputBytes != 8L * 1024 * 1024
                || MaxSourcePixels != 100_000_000))
        {
            throw new InvalidOperationException("AI_COPY_COMPRESSION_PROFILE_OPTIONS_MISMATCH");
        }

        if (AiAnalysisCopyProfiles.IsWicBoundedV4(CompressionProfileVersion)
            && (MaxLongestEdge != 1280
                || JpegQuality != 82
                || MaxSourceBytes != 64L * 1024 * 1024
                || MaxOutputBytes != 8L * 1024 * 1024
                || MaxSourcePixels != 100_000_000))
        {
            throw new InvalidOperationException("AI_COPY_COMPRESSION_PROFILE_OPTIONS_MISMATCH");
        }
    }
}

public sealed record EncodedAnalysisImage(
    byte[] Content,
    string MimeType,
    int PixelWidth,
    int PixelHeight,
    int SourcePixelWidth,
    int SourcePixelHeight);

public sealed record InspectedAnalysisImage(
    string MimeType,
    int PixelWidth,
    int PixelHeight);

/// <summary>
/// Windows-only analysis image codec backed by the framework WIC stack.
/// It emits a single metadata-free JPEG or PNG frame and never writes the
/// source file.
/// </summary>
public static class WicAnalysisImageCodec
{
    public static EncodedAnalysisImage Encode(
        string sourcePath,
        SourceImageFormat expectedFormat,
        AiAnalysisCopyEncodingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return EncodeCore(sourcePath, expectedFormat, options, cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or COMException or ArgumentException)
        {
            throw new InvalidDataException("AI_COPY_SOURCE_CONTENT_INVALID", exception);
        }
    }

    private static EncodedAnalysisImage EncodeCore(
        string sourcePath,
        SourceImageFormat expectedFormat,
        AiAnalysisCopyEncodingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("AI_COPY_SOURCE_NOT_FOUND", sourcePath);
        }

        if (info.Length is <= 0 || info.Length > options.MaxSourceBytes)
        {
            throw new InvalidDataException("AI_COPY_SOURCE_TOO_LARGE");
        }

        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);

        cancellationToken.ThrowIfCancellationRequested();
        if (decoder.Frames.Count != 1 || !DecoderMatches(decoder, expectedFormat))
        {
            throw new InvalidDataException("AI_COPY_SOURCE_FORMAT_MISMATCH");
        }

        var source = decoder.Frames[0];
        ValidateDimensions(source.PixelWidth, source.PixelHeight, options.MaxSourcePixels);
        var oriented = ApplyExifOrientation(source, ReadOrientation(source.Metadata));
        var resized = ResizeToLongestEdge(oriented, options.MaxLongestEdge);
        cancellationToken.ThrowIfCancellationRequested();

        // The v2 profile closes the real request-body gap by converting PNG
        // input that has no meaningful transparency to the already-approved
        // JPEG q82 representation.  A PNG with any non-opaque pixel remains
        // PNG so transparent product artwork is never flattened accidentally.
        var outputMime = expectedFormat == SourceImageFormat.Png
            && (!AiAnalysisCopyProfiles.ConvertsOpaquePngToJpeg(options.CompressionProfileVersion)
                || HasNonOpaqueAlpha(resized, cancellationToken))
            ? "image/png"
            : "image/jpeg";
        var prepared = ConvertForEncoder(resized, outputMime);
        BitmapEncoder encoder = outputMime == "image/png"
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = options.JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(prepared, null, null, null));

        using var output = new BoundedMemoryStream(options.MaxOutputBytes);
        try
        {
            encoder.Save(output);
        }
        catch (InvalidOperationException exception) when (output.LimitExceeded)
        {
            throw new InvalidDataException("AI_COPY_OUTPUT_TOO_LARGE", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (output.Length <= 0)
        {
            throw new InvalidDataException("AI_COPY_OUTPUT_TOO_LARGE");
        }

        var bytes = output.ToArray();
        var inspected = Inspect(bytes, options.MaxSourcePixels);
        if (!string.Equals(inspected.MimeType, outputMime, StringComparison.Ordinal)
            || inspected.PixelWidth != prepared.PixelWidth
            || inspected.PixelHeight != prepared.PixelHeight
            || Math.Max(inspected.PixelWidth, inspected.PixelHeight) > options.MaxLongestEdge)
        {
            throw new InvalidDataException("AI_COPY_OUTPUT_VALIDATION_FAILED");
        }

        return new EncodedAnalysisImage(
            bytes,
            outputMime,
            inspected.PixelWidth,
            inspected.PixelHeight,
            source.PixelWidth,
            source.PixelHeight);
    }

    public static InspectedAnalysisImage Inspect(ReadOnlyMemory<byte> content, long maxPixels = 100_000_000)
    {
        if (content.IsEmpty)
        {
            throw new InvalidDataException("AI_COPY_CONTENT_EMPTY");
        }

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1)
            {
                throw new InvalidDataException("AI_COPY_FRAME_COUNT_INVALID");
            }

            var mime = decoder switch
            {
                JpegBitmapDecoder => "image/jpeg",
                PngBitmapDecoder => "image/png",
                TiffBitmapDecoder => "image/tiff",
                _ => throw new InvalidDataException("AI_COPY_FORMAT_UNSUPPORTED")
            };
            var frame = decoder.Frames[0];
            ValidateDimensions(frame.PixelWidth, frame.PixelHeight, maxPixels);
            return new InspectedAnalysisImage(mime, frame.PixelWidth, frame.PixelHeight);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or COMException or ArgumentException)
        {
            throw new InvalidDataException("AI_COPY_CONTENT_INVALID", exception);
        }
    }

    private static bool DecoderMatches(BitmapDecoder decoder, SourceImageFormat expectedFormat) => expectedFormat switch
    {
        SourceImageFormat.Jpeg => decoder is JpegBitmapDecoder,
        SourceImageFormat.Png => decoder is PngBitmapDecoder,
        SourceImageFormat.Tiff => decoder is TiffBitmapDecoder,
        _ => false
    };

    private static bool HasNonOpaqueAlpha(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        // Inspect the post-orientation/post-resize pixels, rather than the
        // source metadata, because indexed PNGs and premultiplied formats can
        // carry an alpha channel whose pixels are nevertheless all opaque.
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = checked(converted.PixelWidth * 4);
        var buffer = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(buffer, stride, 0);
        for (var index = 3; index < buffer.Length; index += 4)
        {
            if ((index & 0xFFFFF) == 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (buffer[index] != byte.MaxValue)
            {
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static FormatConvertedBitmap ConvertForEncoder(BitmapSource source, string mimeType)
    {
        var converted = new FormatConvertedBitmap(
            source,
            mimeType == "image/png" ? PixelFormats.Pbgra32 : PixelFormats.Bgr24,
            null,
            0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource ResizeToLongestEdge(BitmapSource source, int maxLongestEdge)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxLongestEdge)
        {
            return source;
        }

        var scale = maxLongestEdge / (double)longest;
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        RenderOptions.SetBitmapScalingMode(resized, BitmapScalingMode.HighQuality);
        resized.Freeze();
        return resized;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, ushort orientation)
    {
        if (orientation is <= 1 or > 8)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, width, 0),
            3 => new Matrix(-1, 0, 0, -1, width, height),
            4 => new Matrix(1, 0, 0, -1, 0, height),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, height, 0),
            7 => new Matrix(0, -1, -1, 0, height, width),
            8 => new Matrix(0, -1, 1, 0, 0, width),
            _ => Matrix.Identity
        };
        var transformed = new TransformedBitmap(source, new MatrixTransform(matrix));
        transformed.Freeze();
        return transformed;
    }

    private static ushort ReadOrientation(ImageMetadata? metadata)
    {
        if (metadata is not BitmapMetadata bitmapMetadata)
        {
            return 1;
        }

        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                var value = bitmapMetadata.GetQuery(query);
                if (value is ushort orientation)
                {
                    return orientation;
                }
            }
            catch (NotSupportedException)
            {
                // The container has no matching metadata query.
            }
        }

        return 1;
    }

    private static void ValidateDimensions(int width, int height, long maxPixels)
    {
        if (width <= 0 || height <= 0
            || width > 65_535 || height > 65_535
            || (long)width * height > maxPixels)
        {
            throw new InvalidDataException("AI_COPY_PIXEL_LIMIT_EXCEEDED");
        }
    }

    private sealed class BoundedMemoryStream(long maxLength) : MemoryStream
    {
        public bool LimitExceeded { get; private set; }

        public override long Position
        {
            get => base.Position;
            set
            {
                EnsureWithinLimit(value);
                base.Position = value;
            }
        }

        public override void SetLength(long value)
        {
            EnsureWithinLimit(value);
            base.SetLength(value);
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            var target = loc switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(Position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(loc))
            };
            EnsureWithinLimit(target);
            return base.Seek(offset, loc);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(checked(Position + count));
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(checked(Position + buffer.Length));
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWithinLimit(checked(Position + 1));
            base.WriteByte(value);
        }

        private void EnsureWithinLimit(long value)
        {
            if (value < 0 || value > maxLength)
            {
                LimitExceeded = true;
                throw new InvalidDataException("AI_COPY_OUTPUT_TOO_LARGE");
            }
        }
    }
}
