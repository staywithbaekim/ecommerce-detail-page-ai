using System.IO;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Desktop.App;

/// <summary>
/// Converts a local preview path into a bounded, frozen WPF bitmap. Preview
/// bindings must not decode the source photo at its original dimensions.
/// </summary>
public sealed class BoundedPreviewImageConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var maxDimension = ParseMaxDimension(parameter);
        return BoundedPreviewImageCache.Get(path, maxDimension);
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => Binding.DoNothing;

    private static int ParseMaxDimension(object? parameter)
    {
        if (parameter is string text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, 64, 2048);
        }

        return BoundedPreviewImageCache.DefaultMaxDimension;
    }
}

internal static class BoundedPreviewImageCache
{
    public const int DefaultMaxDimension = 320;

    private const int MaxEntries = 96;
    private const long MaxEstimatedBytes = 64L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly LinkedList<CacheEntry> Entries = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Index =
        new(StringComparer.OrdinalIgnoreCase);
    private static long EstimatedBytes;

    public static BitmapImage? Get(string path, int maxDimension)
    {
        if (!TryGetFileKey(path, maxDimension, out var key))
        {
            return null;
        }

        lock (Gate)
        {
            if (Index.TryGetValue(key, out var existing))
            {
                Entries.Remove(existing);
                Entries.AddFirst(existing);
                return existing.Value.Image;
            }
        }

        var image = Load(path, maxDimension);
        if (image is null)
        {
            return null;
        }

        lock (Gate)
        {
            if (Index.TryGetValue(key, out var raced))
            {
                Entries.Remove(raced);
                Entries.AddFirst(raced);
                return raced.Value.Image;
            }

            var estimatedBytes = EstimateBytes(image);
            if (estimatedBytes > MaxEstimatedBytes)
            {
                // The caller still receives the bounded image, but an
                // unusually large one must not evict the whole thumbnail cache
                // or remain retained by this process-wide cache.
                return image;
            }

            var node = Entries.AddFirst(new CacheEntry(key, image, estimatedBytes));
            Index[key] = node;
            EstimatedBytes += estimatedBytes;
            while (Entries.Count > MaxEntries || EstimatedBytes > MaxEstimatedBytes)
            {
                var last = Entries.Last!;
                Entries.RemoveLast();
                Index.Remove(last.Value.Key);
                EstimatedBytes -= last.Value.EstimatedBytes;
            }
        }

        return image;
    }

    private static bool TryGetFileKey(string path, int maxDimension, out string key)
    {
        key = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0)
            {
                return false;
            }

            key = $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{maxDimension}";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static BitmapImage? Load(string path, int maxDimension)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var (sourceWidth, sourceHeight) = ReadDimensions(fullPath);
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (sourceWidth >= sourceHeight)
            {
                bitmap.DecodePixelWidth = Math.Min(sourceWidth, maxDimension);
            }
            else
            {
                bitmap.DecodePixelHeight = Math.Min(sourceHeight, maxDimension);
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or FileFormatException)
        {
            return null;
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("PREVIEW_IMAGE_FRAME_MISSING");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new InvalidOperationException("PREVIEW_IMAGE_DIMENSIONS_INVALID");
        }

        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static long EstimateBytes(BitmapImage image) =>
        Math.Max(1L, (long)image.PixelWidth * image.PixelHeight * 4L);

    private sealed record CacheEntry(string Key, BitmapImage Image, long EstimatedBytes);
}
