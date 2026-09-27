using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class RenderJpegValidatorTests
{
    [Fact]
    public void DecodesEveryPixelAndChecksTheExactEmbeddedSrgbProfile()
    {
        using var file = new MemoryStream(Jpeg());
        var evidence = RenderJpegValidator.Validate(file, 3, 2, CancellationToken.None);
        Assert.Equal(RenderJpegValidator.SrgbProfileSha256, evidence.IccSha256);
        Assert.Equal(3144, evidence.IccByteLength);
    }

    [Fact]
    public void RetainedRelativeIntentProfileHasTheSameExactSrgbColorTags()
    {
        using var file = new MemoryStream(Jpeg("relative_intent"));
        var evidence = RenderJpegValidator.Validate(file, 3, 2, CancellationToken.None);
        Assert.Equal(RenderJpegValidator.SrgbRelativeIntentProfileSha256, evidence.IccSha256);
        Assert.Equal(3144, evidence.IccByteLength);
    }

    [Theory]
    [InlineData("missing_icc")]
    [InlineData("forged_icc")]
    [InlineData("relative_forged_icc")]
    [InlineData("unsupported_intent")]
    [InlineData("duplicate_icc")]
    [InlineData("dimensions")]
    [InlineData("truncated")]
    [InlineData("trailing_data")]
    public void InvalidJpegCannotPassOnFilenameOrProfileDescriptionAlone(string mutation)
    {
        var bytes = Jpeg(mutation);
        using var stream = new MemoryStream(bytes);
        Assert.ThrowsAny<Exception>(() => RenderJpegValidator.Validate(stream, mutation == "dimensions" ? 4 : 3, 2, CancellationToken.None));
    }

    internal static byte[] Jpeg(string mutation = "", int width = 3, int height = 2)
    {
        var pixels = Enumerable.Repeat((byte)127, width * height * 3).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var raw = new MemoryStream(); encoder.Save(raw);
        using var profileStream = new ColorContext(PixelFormats.Rgb24).OpenProfileStream();
        using var iccBuffer = new MemoryStream(); profileStream.CopyTo(iccBuffer);
        var icc = iccBuffer.ToArray();
        if (mutation is "relative_intent" or "relative_forged_icc") icc[67] = 1;
        if (mutation == "unsupported_intent") icc[67] = 2;
        if (mutation is "forged_icc" or "relative_forged_icc") icc[^1] ^= 1;
        using var output = new MemoryStream(); output.Write(raw.ToArray().AsSpan(0, 2));
        if (mutation != "missing_icc")
        {
            for (var i = 0; i < (mutation == "duplicate_icc" ? 2 : 1); i++)
            {
                var length = icc.Length + 16;
                output.Write([0xff, 0xe2, (byte)(length >> 8), (byte)length]);
                output.Write("ICC_PROFILE\0"u8); output.Write([1, 1]); output.Write(icc);
            }
        }
        output.Write(raw.ToArray().AsSpan(2));
        if (mutation == "trailing_data") output.WriteByte(0);
        return mutation == "truncated" ? output.ToArray()[..^4] : output.ToArray();
    }
}
