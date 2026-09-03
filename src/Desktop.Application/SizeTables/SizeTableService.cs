using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Inputs;
using Desktop.Contracts.Ai;
using Desktop.Domain.SizeTables;

namespace Desktop.Application.SizeTables;

public sealed class SizeTableService
{
    public static SizeTablePreparationResult Prepare(PrepareSizeTableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateManifest(command.Manifest, command.StyleId);
        if (command.Candidates is null)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_CANDIDATES_INVALID");
        }

        var resolution = ResolveSources(
            command.Manifest,
            command.StyleId,
            command.Candidates,
            command.SelectionRevision);
        if (resolution.Status == SizeTableSourceResolutionStatus.Conflict
            && command.SelectedSourceAssetId is not null)
        {
            resolution = resolution.Select(command.SelectedSourceAssetId, command.SelectionRevision);
        }

        if (resolution.Selected is null)
        {
            return new SizeTablePreparationResult(
                resolution,
                null,
                null,
                resolution.ReasonCode);
        }

        var candidateContract = command.Candidates
            .Where(candidate => resolution.Selected.DuplicateAssetIds.Contains(
                candidate.SourceAssetId,
                StringComparer.Ordinal))
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();
        if (candidateContract is null)
        {
            return new SizeTablePreparationResult(
                resolution,
                null,
                null,
                "SIZE_TABLE_RELIABLE_CANDIDATE_MISSING");
        }

        var source = SizeTableSource.Create(
            candidateContract.SourceAssetId,
            resolution.Selected.ContentDigest);
        var candidate = SizeTableCandidate.Create(
            CandidateId(command.Manifest.BatchId, command.StyleId, command.PageId, candidateContract),
            source,
            candidateContract.Confidence,
            candidateContract.Columns,
            candidateContract.Rows);
        var table = SizeTable.FromCandidate(
            TableId(command.Manifest.BatchId, command.StyleId, command.PageId),
            command.Manifest.BatchId,
            command.StyleId,
            command.PageId,
            candidate,
            command.CreatedAt);
        return new SizeTablePreparationResult(
            resolution,
            candidate,
            table,
            table.Status switch
            {
                SizeTableStatus.Valid => "SIZE_TABLE_VALID",
                SizeTableStatus.ReviewRequired => "SIZE_TABLE_REVIEW_REQUIRED",
                _ => "SIZE_TABLE_INVALID"
            });
    }

    public static SizeTable Edit(EditSizeTableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Current.Edit(
            command.ExpectedRevision,
            command.Columns,
            command.Rows,
            command.UpdatedAt);
    }

    public static SizeTableSourceResolution ResolveSources(
        AssetManifestV1 manifest,
        string styleId,
        IReadOnlyList<AiSizeTableCandidateV1> candidates,
        long selectionRevision = 0)
    {
        ValidateManifest(manifest, styleId);
        if (selectionRevision < 0 || candidates is null)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_SOURCE_SELECTION_STATE_INVALID");
        }

        var style = manifest.Styles.Single(item => string.Equals(item.StyleId, styleId, StringComparison.Ordinal));
        var ownedIds = style.AssetIds.ToHashSet(StringComparer.Ordinal);
        var assetsById = manifest.Assets.ToDictionary(asset => asset.AssetId, StringComparer.Ordinal);
        if (candidates.Any(candidate => candidate is null
            || !ownedIds.Contains(candidate.SourceAssetId)
            || !assetsById.ContainsKey(candidate.SourceAssetId)))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_SOURCE_OWNERSHIP_MISMATCH");
        }

        var referencedAssets = candidates
            .Select(candidate => assetsById[candidate.SourceAssetId])
            .GroupBy(asset => NormalizeDigest(asset.Sha256), StringComparer.Ordinal)
            .Select(group =>
            {
                var assets = group.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray();
                var primary = assets[0];
                return new SizeTableSourceOption(
                    primary.AssetId,
                    group.Key,
                    primary.Length,
                    primary.MimeType,
                    assets.Select(asset => asset.AssetId).ToArray());
            })
            .OrderBy(option => option.AssetId, StringComparer.Ordinal)
            .ToArray();
        return referencedAssets.Length switch
        {
            0 => new SizeTableSourceResolution(
                SizeTableSourceResolutionStatus.Missing,
                [],
                null,
                selectionRevision,
                "SIZE_TABLE_SOURCE_MISSING"),
            1 => new SizeTableSourceResolution(
                SizeTableSourceResolutionStatus.Selected,
                referencedAssets,
                referencedAssets[0],
                selectionRevision,
                "SIZE_TABLE_SOURCE_SELECTED"),
            _ => new SizeTableSourceResolution(
                SizeTableSourceResolutionStatus.Conflict,
                referencedAssets,
                null,
                selectionRevision,
                "SIZE_TABLE_SOURCE_CONFLICT")
        };
    }

    private static void ValidateManifest(AssetManifestV1 manifest, string styleId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.SchemaVersion, AssetManifestProtocol.Version, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.BatchId)
            || manifest.Styles is null
            || manifest.Assets is null
            || manifest.Styles.Count(style => string.Equals(style.StyleId, styleId, StringComparison.Ordinal)) != 1)
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_MANIFEST_INVALID");
        }

        var style = manifest.Styles.Single(item => string.Equals(item.StyleId, styleId, StringComparison.Ordinal));
        var assetIds = manifest.Assets.Select(asset => asset.AssetId).ToHashSet(StringComparer.Ordinal);
        if (style.AssetIds is null || style.AssetIds.Any(assetId => !assetIds.Contains(assetId)))
        {
            throw new SizeTableRuleViolationException("SIZE_TABLE_MANIFEST_INVALID");
        }
    }

    private static string TableId(string batchId, string styleId, string pageId) =>
        "size_" + StableSuffix($"{batchId}\n{styleId}\n{pageId}");

    private static string CandidateId(
        string batchId,
        string styleId,
        string pageId,
        AiSizeTableCandidateV1 candidate)
    {
        var builder = new StringBuilder()
            .Append(batchId).Append('\n')
            .Append(styleId).Append('\n')
            .Append(pageId).Append('\n')
            .Append(candidate.SourceAssetId).Append('\n')
            .Append(candidate.Confidence).Append('\n');
        foreach (var column in candidate.Columns)
        {
            builder.Append(column?.Length ?? -1).Append(':').Append(column).Append('\n');
        }
        foreach (var row in candidate.Rows)
        {
            foreach (var cell in row)
            {
                builder.Append(cell?.Length ?? -1).Append(':').Append(cell).Append('\n');
            }
        }
        return "candidate_" + StableSuffix(builder.ToString());
    }

    private static string StableSuffix(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    private static string NormalizeDigest(string value) => SizeTableSource.Create("digest_probe", value).ContentDigest;
}
