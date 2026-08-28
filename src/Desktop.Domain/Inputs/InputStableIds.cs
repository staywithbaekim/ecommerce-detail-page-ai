using System.Security.Cryptography;
using System.Text;

namespace Desktop.Domain.Inputs;

public static class InputStableIds
{
    public static string ForStyle(string batchId, string sourceDirectoryRelativePath) =>
        "style_" + Digest(
            InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID"),
            InputValueRules.RelativeSourcePath(sourceDirectoryRelativePath, allowRoot: true));

    public static string ForAsset(
        string batchId,
        string styleId,
        string sourceRelativePath,
        string contentSha256) => "asset_" + Digest(
            InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID"),
            InputValueRules.Identifier(styleId, "INPUT_STYLE_ID_INVALID"),
            InputValueRules.RelativeSourcePath(sourceRelativePath, allowRoot: false),
            InputValueRules.Sha256(contentSha256));

    public static string ForManifest(string batchId, string inventorySha256) =>
        "assetmanifest_" + Digest(
            InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID"),
            InputValueRules.Sha256(inventorySha256));

    public static string ForTemplateSnapshot(
        string batchId,
        string revisionId,
        IEnumerable<string> selectedPageIds)
    {
        ArgumentNullException.ThrowIfNull(selectedPageIds);
        var pages = selectedPageIds
            .Select(pageId => InputValueRules.Identifier(pageId, "INPUT_PAGE_ID_INVALID"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InputRuleViolationException("INPUT_PAGE_SELECTION_INVALID");
        }

        return "snapshot_" + Digest(
            InputValueRules.Identifier(batchId, "INPUT_BATCH_ID_INVALID"),
            InputValueRules.Identifier(revisionId, "INPUT_REVISION_ID_INVALID"),
            string.Join(',', pages));
    }

    private static string Digest(params string[] components)
    {
        var canonical = string.Join('\n', components.Select(component =>
            component.Normalize(NormalizationForm.FormC).ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..32];
    }
}
