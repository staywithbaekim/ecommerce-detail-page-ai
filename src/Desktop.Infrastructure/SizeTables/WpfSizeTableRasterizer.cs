using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Application.Files;
using Desktop.Application.SizeTables;
using Desktop.Domain.SizeTables;

namespace Desktop.Infrastructure.SizeTables;

public sealed class WpfSizeTableRasterizer(IFileStore files) : ISizeTableRasterizer
{
    private const string ResourceRole = "deterministic_size_table";
    private const string MediaType = "image/png";
    private const string RasterizerVersion = "wpf-size-table-rasterizer/v1";

    public async Task<SizeTableRasterizedResource> RasterizeAsync(
        SizeTableRasterizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Table);
        ArgumentNullException.ThrowIfNull(request.Style);
        if (!request.Table.CanSave)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_RASTER_REQUIRES_VALID_TABLE");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var rendered = await RenderOnStaThreadAsync(request, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(rendered.Content)).ToLowerInvariant();
        var resourceId = ResourceId(request.Table.DataDigest, request.Style, rendered.FontSignature);
        var relativePath = $"batches/{request.Table.BatchId}/size-tables/{request.Table.StyleId}/" +
            $"{request.Table.PageId}/resources/{resourceId}.png";
        await files.WriteAtomicallyAsync(relativePath, rendered.Content, cancellationToken).ConfigureAwait(false);
        return new SizeTableRasterizedResource(
            resourceId,
            ResourceRole,
            MediaType,
            sha256,
            rendered.Content.LongLength,
            rendered.Width,
            rendered.Height,
            request.Table.DataDigest,
            request.Style.StyleVersion,
            request.Style.LayoutVersion,
            relativePath,
            RasterizerVersion,
            rendered.FontSignature,
            96);
    }

    private static Task<RenderedPng> RenderOnStaThreadAsync(
        SizeTableRasterizationRequest request,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RenderedPng>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(Render(request));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "size-table-rasterizer"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RenderedPng Render(SizeTableRasterizationRequest request)
    {
        var table = request.Table;
        var style = request.Style;
        var typeface = new Typeface(
            new FontFamily(style.FontFamily),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_FONT_UNAVAILABLE");
        }
        var fontSignature = FontSignature(style.FontFamily, glyphTypeface);
        var columnWidths = MeasureColumns(table, style, typeface);
        var rowHeight = Math.Ceiling(Math.Max(
            style.MinimumRowHeight,
            MeasureText("国", style, typeface).Height + (2 * style.VerticalCellPadding)));
        var heightValue = checked((long)Math.Ceiling(rowHeight * (table.Rows.Length + 1)) + 1);
        if (heightValue is <= 0 or > 16384)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_LAYOUT_HEIGHT_EXCEEDED");
        }
        var height = (int)heightValue;

        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.ClearType);
        using (var drawing = visual.RenderOpen())
        {
            var background = Brush(style.BackgroundColor);
            var headerBackground = Brush(style.HeaderBackgroundColor);
            var textBrush = Brush(style.TextColor);
            var gridPen = new Pen(Brush(style.GridColor), 1);
            gridPen.Freeze();
            drawing.DrawRectangle(background, null, new Rect(0, 0, style.TargetWidth, height));
            drawing.DrawRectangle(headerBackground, null, new Rect(0, 0, style.TargetWidth, rowHeight));

            var x = 0d;
            for (var columnIndex = 0; columnIndex < table.Columns.Length; columnIndex++)
            {
                var width = columnWidths[columnIndex];
                DrawCenteredText(
                    drawing,
                    table.Columns[columnIndex].SourceText,
                    new Rect(x, 0, width, rowHeight),
                    style,
                    typeface,
                    textBrush);
                for (var rowIndex = 0; rowIndex < table.Rows.Length; rowIndex++)
                {
                    DrawCenteredText(
                        drawing,
                        table.Rows[rowIndex].Cells[columnIndex].SourceText,
                        new Rect(x, rowHeight * (rowIndex + 1), width, rowHeight),
                        style,
                        typeface,
                        textBrush);
                }
                x += width;
                drawing.DrawLine(gridPen, new Point(Math.Round(x) - 0.5, 0), new Point(Math.Round(x) - 0.5, height));
            }

            for (var rowIndex = 0; rowIndex <= table.Rows.Length + 1; rowIndex++)
            {
                var y = Math.Min(height - 0.5, Math.Round(rowHeight * rowIndex) - 0.5);
                if (rowIndex == 0) y = 0.5;
                drawing.DrawLine(gridPen, new Point(0, y), new Point(style.TargetWidth, y));
            }
            drawing.DrawLine(gridPen, new Point(0.5, 0), new Point(0.5, height));
        }

        var bitmap = new RenderTargetBitmap(
            style.TargetWidth,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new RenderedPng(stream.ToArray(), style.TargetWidth, height, fontSignature);
    }

    private static double[] MeasureColumns(
        SizeTable table,
        SizeTableRasterStyle style,
        Typeface typeface)
    {
        var natural = table.Columns.Select((column, columnIndex) =>
        {
            var texts = table.Rows.Select(row => row.Cells[columnIndex].SourceText).Prepend(column.SourceText);
            return Math.Ceiling(texts.Max(text => MeasureText(text, style, typeface).WidthIncludingTrailingWhitespace)
                + (2 * style.HorizontalCellPadding));
        }).ToArray();
        var naturalTotal = natural.Sum();
        if (naturalTotal > style.TargetWidth)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_LAYOUT_WIDTH_EXCEEDED");
        }

        var extraPerColumn = (style.TargetWidth - naturalTotal) / natural.Length;
        var result = natural.Select(width => width + extraPerColumn).ToArray();
        result[^1] += style.TargetWidth - result.Sum();
        return result;
    }

    private static FormattedText MeasureText(
        string text,
        SizeTableRasterStyle style,
        Typeface typeface) => new(
            text,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            typeface,
            style.FontSize,
            Brushes.Black,
            pixelsPerDip: 1);

    private static void DrawCenteredText(
        DrawingContext drawing,
        string text,
        Rect bounds,
        SizeTableRasterStyle style,
        Typeface typeface,
        Brush brush)
    {
        var formatted = MeasureText(text, style, typeface);
        formatted.SetForegroundBrush(brush);
        var x = bounds.Left + Math.Max(style.HorizontalCellPadding, (bounds.Width - formatted.Width) / 2);
        var y = bounds.Top + Math.Max(style.VerticalCellPadding, (bounds.Height - formatted.Height) / 2);
        drawing.DrawText(formatted, new Point(Math.Round(x), Math.Round(y)));
    }

    private static SolidColorBrush Brush(string color)
    {
        var argb = Convert.FromHexString(color[1..]);
        var brush = new SolidColorBrush(Color.FromArgb(argb[0], argb[1], argb[2], argb[3]));
        brush.Freeze();
        return brush;
    }

    private static string ResourceId(
        string tableDigest,
        SizeTableRasterStyle style,
        string fontSignature)
    {
        var preimage = Encoding.UTF8.GetBytes(
            $"{tableDigest}\n{style.StyleVersion}\n{style.LayoutVersion}\n{style.FontFamily}\n" +
            $"{style.FontSize:R}\n{style.TargetWidth}\n{style.MinimumRowHeight:R}\n" +
            $"{style.HorizontalCellPadding:R}\n{style.VerticalCellPadding:R}\n" +
            $"{style.BackgroundColor}\n{style.HeaderBackgroundColor}\n{style.TextColor}\n{style.GridColor}\n" +
            $"{RasterizerVersion}\n{fontSignature}\n96");
        return "size_raster_" + Convert.ToHexString(SHA256.HashData(preimage))[..24].ToLowerInvariant();
    }

    private static string FontSignature(string fontFamily, GlyphTypeface glyphTypeface)
    {
        var versions = string.Join('|', glyphTypeface.VersionStrings
            .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key.Name}:{pair.Value}"));
        var preimage = Encoding.UTF8.GetBytes($"{fontFamily}\n{versions}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
    }

    private sealed record RenderedPng(byte[] Content, int Width, int Height, string FontSignature);
}
