using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

/// <summary>Stable display numbers. Deleted targets remain reserved; liveness comes from the revision.</summary>
public sealed record TemplateReferenceAlias(string PageId, string? FrameId, int Number);

/// <summary>One rule authority per revision. Version is advanced by the existing transactional writer.</summary>
public sealed record TemplateRuleWorkspace(
    long Version,
    TemplateRuleSet? Rules,
    ImmutableArray<TemplateReferenceAlias> Aliases)
{
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
        return this with { Aliases = aliases.ToImmutable() };
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
}
