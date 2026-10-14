using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

/// <summary>Common read model; implementations have distinct creation and persistence contracts.</summary>
public interface IBatchTemplateContext
{
    string SnapshotId { get; }
    string BatchId { get; }
    string RevisionId { get; }
    ImmutableArray<BatchTemplatePageSnapshot> Pages { get; }
    string RulesDigest { get; }
    DateTimeOffset CreatedAt { get; }
}

/// <summary>New-rule context. Never converted to, or restored through, the legacy snapshot.</summary>
public sealed record BatchRuleTemplateContext : IBatchTemplateContext
{
    public const string SchemaVersion = "batch-rule-template-context/v1";
    private BatchRuleTemplateContext(string snapshotId, string batchId, string revisionId,
        ImmutableArray<BatchTemplatePageSnapshot> pages, string rulesDigest, string businessRuleDigest, DateTimeOffset createdAt)
    {
        SnapshotId = TemplateValueRules.Identifier(snapshotId, "TPL_SNAPSHOT_ID_INVALID");
        BatchId = TemplateValueRules.Identifier(batchId, "TPL_BATCH_ID_INVALID");
        RevisionId = TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID");
        RulesDigest = TemplateValueRules.Digest(rulesDigest, "TPL_REVISION_RULE_DIGEST_INVALID");
        BusinessRuleDigest = TemplateValueRules.Digest(businessRuleDigest, "TPL_RULE_DIGEST_INVALID");
        if (pages.IsDefaultOrEmpty || pages.Any(page => page is null)
            || pages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count() != pages.Length
            || pages.Select(page => page.Order).Distinct().Count() != pages.Length)
            throw new TemplateRuleViolationException("TPL_RULE_CONTEXT_INVALID");
        Pages = pages;
        CreatedAt = createdAt;
    }
    public string SnapshotId { get; }
    public string BatchId { get; }
    public string RevisionId { get; }
    public ImmutableArray<BatchTemplatePageSnapshot> Pages { get; }
    public string RulesDigest { get; }
    public string BusinessRuleDigest { get; }
    public DateTimeOffset CreatedAt { get; }

    public static BatchRuleTemplateContext Create(string snapshotId, string batchId, TemplateRevision revision,
        IEnumerable<string> selectedPageIds, string businessRuleDigest, DateTimeOffset createdAt)
    {
        if (revision.State != TemplateRevisionState.Committed || revision.RuleDigest is null
            || revision.RuleWorkspace is not { Version: > 0, Rules: not null })
            throw new TemplateRuleViolationException("TPL_RULE_CONTEXT_REVISION_INVALID");
        // E0 evaluates the whole rule graph. Partial graph selection is not an implicit new rule.
        if (!selectedPageIds.ToHashSet(StringComparer.Ordinal).SetEquals(revision.Pages.Select(page => page.PageId)))
            throw new TemplateRuleViolationException("TPL_RULE_CONTEXT_ALL_PAGES_REQUIRED");
        var pages = revision.Pages.OrderBy(page => page.Order).Select(page => new BatchTemplatePageSnapshot(
            page.PageId, page.Order, page.ContentType, page.GenerationMode, page.VariantCapacity, page.TemplateResourceId,
            revision.ActiveFrameBindings.Where(frame => frame.PageId == page.PageId).Select(frame => frame.FrameId)
                .Order(StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray();
        return new(snapshotId, batchId, revision.RevisionId, pages, revision.RuleDigest, businessRuleDigest, createdAt);
    }

    public static BatchRuleTemplateContext Restore(string version, string snapshotId, string batchId, string revisionId,
        IEnumerable<BatchTemplatePageSnapshot> pages, string rulesDigest, string businessRuleDigest, DateTimeOffset createdAt) =>
        version == SchemaVersion ? new(snapshotId, batchId, revisionId, pages.ToImmutableArray(), rulesDigest, businessRuleDigest, createdAt)
            : throw new TemplateRuleViolationException("TPL_RULE_CONTEXT_VERSION_UNSUPPORTED");
}
