using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Desktop.Infrastructure.Bridge;

public sealed record RenderJpegEvidence(int Width, int Height, string IccSha256, int IccByteLength);

/// <summary>Bounded JPEG/ICC validation and complete independent pixel decoding before publication.</summary>
public static class RenderJpegValidator
{
    // Exact standard sRGB profile emitted by the approved Photoshop 26.8.1 host.
    // A name alone is not color-space evidence; other profiles need explicit support evidence.
    public const string SrgbProfileSha256 = "2b3aa1645779a9e634744faf9b01e9102b0c9b88fd6deced7934df86b949af7e";
    // The same approved host retains relative-colorimetric intent in an already
    // sRGB document. All bytes match the profile above except header byte 67
    // (ICC rendering intent 0 -> 1); the complete color tag table is identical.
    public const string SrgbRelativeIntentProfileSha256 = "3f6d674174f3804eb0dabdac90ae17486e898c5063a66f861c116ea033da8301";

    public static RenderJpegEvidence Validate(Stream stream, int width, int height, CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length is < 4 or > 2147483648
            || width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw Invalid();
        stream.Position = 0;
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) throw Invalid();
        var chunks = new SortedDictionary<int, byte[]>();
        var chunkCount = 0; var total = 0; var headerBytes = 2L; var sawFrame = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != 0xff) throw Invalid();
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker is < 0 or 0 or 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) throw Invalid();
            var size = Read16(stream) - 2;
            if (size < 0 || (headerBytes += size + 4) > 4 * 1024 * 1024 || stream.Position + size > stream.Length) throw Invalid();
            var data = new byte[size]; stream.ReadExactly(data);
            if (marker == 0xda) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (sawFrame || size < 6 || data[0] != 8 || Read16(data.AsSpan(1)) != height
                    || Read16(data.AsSpan(3)) != width || data[5] != 3) throw Invalid();
                sawFrame = true;
            }
            if (marker != 0xe2 || !data.AsSpan().StartsWith("ICC_PROFILE\0"u8)) continue;
            if (size < 15 || data[12] == 0 || data[13] == 0 || data[13] > 32 || data[12] > data[13]
                || chunkCount != 0 && chunkCount != data[13] || !chunks.TryAdd(data[12], data[14..])) throw Invalid();
            chunkCount = data[13]; total += size - 14;
            if (total > 1024 * 1024) throw Invalid();
        }
        if (!sawFrame || chunks.Count != chunkCount || chunkCount == 0 || total < 128) throw Invalid();
        var icc = chunks.Values.SelectMany(part => part).ToArray();
        if (BinaryPrimitives.ReadUInt32BigEndian(icc) != icc.Length || !icc.AsSpan(36, 4).SequenceEqual("acsp"u8)
            || !icc.AsSpan(16, 4).SequenceEqual("RGB "u8)) throw Invalid();
        var hash = Convert.ToHexStringLower(SHA256.HashData(icc));
        if (hash != SrgbProfileSha256 && hash != SrgbRelativeIntentProfileSha256) throw Invalid();
        stream.Position = stream.Length - 2;
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd9) throw Invalid();
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) throw Invalid();
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height || frame.Format.BitsPerPixel != 24) throw Invalid();
        var stride = checked(width * 3); var rows = Math.Max(1, 1024 * 1024 / stride);
        var buffer = new byte[checked(stride * Math.Min(rows, height))];
        for (var y = 0; y < height; y += rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.CopyPixels(new Int32Rect(0, y, width, Math.Min(rows, height - y)), buffer, stride, 0);
        }
        return new(width, height, hash, total);
    }

    private static int Read16(Stream stream)
    {
        var a = stream.ReadByte(); var b = stream.ReadByte();
        return a < 0 || b < 0 ? throw Invalid() : (a << 8) | b;
    }
    private static int Read16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static InvalidDataException Invalid() => new("RENDER_OUTPUT_INVALID");
}
