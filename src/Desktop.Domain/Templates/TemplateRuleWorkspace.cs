using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

/// <summary>
/// A user-selected candidate that is not a replaceable product slot. Layer
/// identity is scoped to the page's frozen PSD resource and is retained so the
/// choice can be restored and round-tripped with a template package.
/// </summary>
public sealed record TemplateCandidateExclusion(
    string PageId,
    string TemplateSha256,
    string StructureSignature,
    int LayerId,
    ImmutableArray<int> LayerPath);

/// <summary>Stable display numbers. Deleted targets remain reserved; liveness comes from the revision.</summary>
public sealed record TemplateReferenceAlias(string PageId, string? FrameId, int Number);

/// <summary>One rule authority per revision. Version is advanced by the existing transactional writer.</summary>
public sealed record TemplateRuleWorkspace(
    long Version,
    TemplateRuleSet? Rules,
    ImmutableArray<TemplateReferenceAlias> Aliases)
{
    /// <summary>
    /// Candidate frame identities the user explicitly marked as non-replaceable.
    /// These are reversible tombstones: the PSD layer remains untouched and the
    /// candidate can be restored without running inspection again.
    /// </summary>
    public ImmutableArray<TemplateCandidateExclusion> ExcludedCandidates { get; init; } = [];

    public static TemplateRuleWorkspace Empty { get; } = new(0, null, []);

    public TemplateRuleWorkspace Reconcile(TemplateRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var aliases = Aliases.ToBuilder();
        var pageNumber = Aliases.Where(alias => alias.FrameId is null).Select(alias => alias.Number).DefaultIfEmpty().Max();
        foreach (var page in revision.Pages.OrderBy(page => page.Order))
        {
            if (!aliases.Any(alias => alias.PageId == page.PageId && alias.FrameId is null))
                aliases.Add(new(page.PageId, null, checked(++pageNumber)));
            var frameNumber = aliases.Where(alias => alias.PageId == page.PageId && alias.FrameId is not null)
                .Select(alias => alias.Number).DefaultIfEmpty().Max();
            foreach (var frame in revision.FrameBindings.Where(frame => frame.PageId == page.PageId))
                if (!aliases.Any(alias => alias.PageId == page.PageId && alias.FrameId == frame.FrameId))
                    aliases.Add(new(page.PageId, frame.FrameId, checked(++frameNumber)));
        }
        return this with
        {
            Aliases = aliases.ToImmutable(),
            ExcludedCandidates = ExcludedCandidates.IsDefault ? [] : ExcludedCandidates
        };
    }

    public TemplateRuleWorkspace ReserveFrames(IEnumerable<(string PageId, string FrameId)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var aliases = Aliases.ToBuilder();
        foreach (var (pageId, frameId) in candidates)
        {
            TemplateValueRules.Identifier(pageId, "TPL_RULE_ALIAS_INVALID");
            TemplateValueRules.Identifier(frameId, "TPL_RULE_ALIAS_INVALID");
            if (!aliases.Any(alias => alias.PageId == pageId && alias.FrameId is null))
                throw new TemplateRuleViolationException("TPL_RULE_ALIAS_INVALID");
            if (aliases.Any(alias => alias.PageId == pageId && alias.FrameId == frameId)) continue;
            var next = aliases.Where(alias => alias.PageId == pageId && alias.FrameId is not null)
                .Select(alias => alias.Number).DefaultIfEmpty().Max();
            aliases.Add(new(pageId, frameId, checked(next + 1)));
        }
        return this with { Aliases = aliases.ToImmutable() };
    }

    public bool IsCandidateExcluded(string pageId, string templateSha256, string structureSignature,
        int layerId, IReadOnlyList<int> layerPath) =>
        !ExcludedCandidates.IsDefault && ExcludedCandidates.Any(item => item.PageId == pageId
            && string.Equals(item.TemplateSha256, templateSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.StructureSignature, structureSignature, StringComparison.OrdinalIgnoreCase)
            && item.LayerId == layerId && item.LayerPath.SequenceEqual(layerPath));

    public TemplateRuleWorkspace SetCandidateExcluded(
        string pageId,
        string templateSha256,
        string structureSignature,
        int layerId,
        IReadOnlyList<int> layerPath,
        bool excluded)
    {
        ArgumentNullException.ThrowIfNull(layerPath);
        var current = ExcludedCandidates.IsDefault ? ImmutableArray<TemplateCandidateExclusion>.Empty : ExcludedCandidates;
        var index = -1;
        for (var candidate = 0; candidate < current.Length; candidate++)
        {
            var item = current[candidate];
            if (item.PageId == pageId
                && string.Equals(item.TemplateSha256, templateSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.StructureSignature, structureSignature, StringComparison.OrdinalIgnoreCase)
                && item.LayerId == layerId && item.LayerPath.SequenceEqual(layerPath))
            {
                index = candidate;
                break;
            }
        }

        if (excluded)
        {
            if (index >= 0) return this with { ExcludedCandidates = current };
            return this with
            {
                ExcludedCandidates = current.Add(new TemplateCandidateExclusion(pageId, templateSha256,
                    structureSignature, layerId, [.. layerPath]))
            };
        }

        return index < 0 ? this : this with { ExcludedCandidates = current.RemoveAt(index) };
    }

    /// <summary>
    /// Assign numbers to the currently discovered, not-yet-bound candidates in
    /// their visual reading order. Existing bound/tombstoned aliases remain
    /// stable; only the unbound candidate aliases supplied by the caller may
    /// move. This fixes worker layer-order numbering without changing target
    /// identity or production bindings.
    /// </summary>
    public TemplateRuleWorkspace ReorderUnboundCandidateAliases(
        IEnumerable<(string PageId, string FrameId)> orderedCandidates,
        IReadOnlySet<string> fixedFrameIds)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        ArgumentNullException.ThrowIfNull(fixedFrameIds);

        var ordered = orderedCandidates.ToArray();
        var aliases = Aliases.ToBuilder();
        foreach (var pageGroup in ordered.GroupBy(item => item.PageId, StringComparer.Ordinal))
        {
            var pageId = pageGroup.Key;
            var movableIds = pageGroup
                .Select(item => item.FrameId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var candidateAliases = movableIds.Where(frameId => !fixedFrameIds.Contains(frameId)).ToArray();
            if (candidateAliases.Length == 0) continue;

            var occupied = aliases
                .Where(alias => alias.PageId == pageId
                    && alias.FrameId is not null
                    && !candidateAliases.Contains(alias.FrameId, StringComparer.Ordinal))
                .Select(alias => alias.Number)
                .ToHashSet();
            var next = 1;
            foreach (var frameId in candidateAliases)
            {
                while (occupied.Contains(next)) next++;
                var index = -1;
                for (var candidate = 0; candidate < aliases.Count; candidate++)
                {
                    if (aliases[candidate].PageId == pageId && aliases[candidate].FrameId == frameId)
                    {
                        index = candidate;
                        break;
                    }
                }
                if (index >= 0)
                {
                    aliases[index] = aliases[index] with { Number = next };
                    occupied.Add(next++);
                }
            }
        }

        return this with { Aliases = aliases.ToImmutable() };
    }
}
