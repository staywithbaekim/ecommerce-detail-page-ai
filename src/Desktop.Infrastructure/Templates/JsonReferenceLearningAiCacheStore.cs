using System.Text.Json;
using Desktop.Application.Files;
using Desktop.Application.Templates;

namespace Desktop.Infrastructure.Templates;

public sealed class JsonReferenceLearningAiCacheStore(IFileStore files) : IReferenceLearningAiCacheStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<ReferenceLearningAiCacheEntry?> FindAsync(string cacheKey, CancellationToken cancellationToken)
    {
        Validate(cacheKey);
        var bytes = await files.ReadAsync(PathFor(cacheKey), cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<ReferenceLearningAiCacheEntry>(bytes, Options);
            return entry is not null && string.Equals(entry.CacheKey, cacheKey, StringComparison.Ordinal)
                ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(ReferenceLearningAiCacheEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry.CacheKey);
        return files.WriteAtomicallyAsync(PathFor(entry.CacheKey), JsonSerializer.SerializeToUtf8Bytes(entry, Options), cancellationToken);
    }

    public async Task<bool> InvalidateAsync(string cacheKey, CancellationToken cancellationToken)
    {
        Validate(cacheKey);
        var entry = await FindAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (entry is null || !entry.IsReusable) return false;
        await SaveAsync(entry with { IsReusable = false }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ReferenceSemanticOverride?> FindOverrideAsync(
        string learningId,
        string frameId,
        CancellationToken cancellationToken)
    {
        ValidateId(learningId);
        ValidateId(frameId);
        var bytes = await files.ReadAsync(OverridePath(learningId, frameId), cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            var value = JsonSerializer.Deserialize<ReferenceSemanticOverride>(bytes, Options);
            return value is not null
                && value.LearningId == learningId
                && value.FrameId == frameId
                && ReferenceSelectionRequirementPolicy.IsValid(value.SelectionRequirement)
                ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveOverrideAsync(ReferenceSemanticOverride value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateId(value.LearningId);
        ValidateId(value.FrameId);
        if (!ReferenceSelectionRequirementPolicy.IsValid(value.SelectionRequirement))
            throw new InvalidOperationException("TPL_LEARN_SELECTION_REQUIREMENT_INVALID");
        return files.WriteAtomicallyAsync(
            OverridePath(value.LearningId, value.FrameId),
            JsonSerializer.SerializeToUtf8Bytes(value, Options),
            cancellationToken);
    }

    private static string PathFor(string cacheKey) =>
        $"ai-analysis/reference-learning-v2/{cacheKey[7..]}.json";

    private static string OverridePath(string learningId, string frameId) =>
        $"learning/reference-semantic-overrides/{learningId}/{frameId}.json";

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new InvalidOperationException("TPL_LEARN_ID_INVALID");
    }

    private static void Validate(string cacheKey)
    {
        if (cacheKey.Length != 71
            || !cacheKey.StartsWith("sha256:", StringComparison.Ordinal)
            || cacheKey[7..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("TPL_LEARN_AI_CACHE_KEY_INVALID");
    }
}
