using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Infrastructure.Inputs;

internal static class InputCommitJournalConsistency
{
    public static bool BatchDraftMatches(BatchDraft expected, BatchDraft actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return expected.ScanStatus == actual.ScanStatus
            && expected.InputType == actual.InputType
            && string.Equals(expected.BatchId, actual.BatchId, StringComparison.Ordinal)
            && string.Equals(expected.InputRoot, actual.InputRoot, StringComparison.Ordinal)
            && string.Equals(expected.OutputRoot, actual.OutputRoot, StringComparison.Ordinal)
            && expected.SelectionMode == actual.SelectionMode
            && expected.IncludeEditablePsd == actual.IncludeEditablePsd
            && string.Equals(expected.ManifestReference, actual.ManifestReference, StringComparison.Ordinal)
            && string.Equals(expected.ManifestDigest, actual.ManifestDigest, StringComparison.Ordinal)
            && expected.CreatedAt == actual.CreatedAt
            && expected.UpdatedAt == actual.UpdatedAt
            && SnapshotMatches(expected.TemplateSnapshot, actual.TemplateSnapshot)
            && expected.Styles.OrderBy(item => item.Order).SequenceEqual(
                actual.Styles.OrderBy(item => item.Order),
                StyleComparer.Instance)
            && expected.Assets.OrderBy(item => item.SourceRelativePath, StringComparer.Ordinal)
                .SequenceEqual(
                    actual.Assets.OrderBy(item => item.SourceRelativePath, StringComparer.Ordinal),
                    AssetComparer.Instance);
    }

    private static bool SnapshotMatches(
        BatchTemplateSnapshot expected,
        BatchTemplateSnapshot actual)
    {
        return string.Equals(expected.SnapshotId, actual.SnapshotId, StringComparison.Ordinal)
            && string.Equals(expected.BatchId, actual.BatchId, StringComparison.Ordinal)
            && string.Equals(expected.RevisionId, actual.RevisionId, StringComparison.Ordinal)
            && string.Equals(expected.RulesDigest, actual.RulesDigest, StringComparison.Ordinal)
            && expected.CreatedAt == actual.CreatedAt
            && expected.Pages.OrderBy(page => page.Order).SequenceEqual(
                actual.Pages.OrderBy(page => page.Order),
                PageComparer.Instance);
    }

    private sealed class StyleComparer : IEqualityComparer<StyleDraft>
    {
        public static StyleComparer Instance { get; } = new();

        public bool Equals(StyleDraft? x, StyleDraft? y) => x is not null
            && y is not null
            && string.Equals(x.StyleId, y.StyleId, StringComparison.Ordinal)
            && string.Equals(x.DefaultStyleCode, y.DefaultStyleCode, StringComparison.Ordinal)
            && string.Equals(x.SourceDirectoryRelativePath, y.SourceDirectoryRelativePath, StringComparison.Ordinal)
            && x.Order == y.Order
            // AssetIds is a membership list, not an ordered sequence.  The
            // journal preserves scan order while SQLite reloads assets by
            // source path, and those two orderings can differ by collation.
            && x.AssetIds.Order(StringComparer.Ordinal)
                .SequenceEqual(y.AssetIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);

        public int GetHashCode(StyleDraft obj) => obj.StyleId.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class AssetComparer : IEqualityComparer<SourceAssetMetadata>
    {
        public static AssetComparer Instance { get; } = new();

        public bool Equals(SourceAssetMetadata? x, SourceAssetMetadata? y) => x is not null
            && y is not null
            && string.Equals(x.AssetId, y.AssetId, StringComparison.Ordinal)
            && string.Equals(x.StyleId, y.StyleId, StringComparison.Ordinal)
            && string.Equals(x.SourceRelativePath, y.SourceRelativePath, StringComparison.Ordinal)
            && string.Equals(x.FileName, y.FileName, StringComparison.Ordinal)
            && x.Length == y.Length
            && string.Equals(x.Sha256, y.Sha256, StringComparison.Ordinal)
            && x.PixelWidth == y.PixelWidth
            && x.PixelHeight == y.PixelHeight
            && x.Format == y.Format
            && string.Equals(x.MimeType, y.MimeType, StringComparison.Ordinal)
            && x.Readability == y.Readability;

        public int GetHashCode(SourceAssetMetadata obj) => obj.AssetId.GetHashCode(StringComparison.Ordinal);
    }

    private sealed class PageComparer : IEqualityComparer<BatchTemplatePageSnapshot>
    {
        public static PageComparer Instance { get; } = new();

        public bool Equals(BatchTemplatePageSnapshot? x, BatchTemplatePageSnapshot? y) => x is not null
            && y is not null
            && string.Equals(x.PageId, y.PageId, StringComparison.Ordinal)
            && x.Order == y.Order
            && x.ContentType == y.ContentType
            && x.GenerationMode == y.GenerationMode
            && x.VariantCapacity == y.VariantCapacity
            && string.Equals(x.TemplateResourceId, y.TemplateResourceId, StringComparison.Ordinal)
            && x.FrameIds.SequenceEqual(y.FrameIds, StringComparer.Ordinal);

        public int GetHashCode(BatchTemplatePageSnapshot obj) => obj.PageId.GetHashCode(StringComparison.Ordinal);
    }
}
