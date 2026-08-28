using System.Collections.Immutable;

namespace Desktop.Domain.Templates;

public sealed record TemplateGroup
{
    private TemplateGroup(
        string groupId,
        string name,
        string? currentRevisionId,
        TemplateGroupStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        GroupId = groupId;
        Name = name;
        CurrentRevisionId = currentRevisionId;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string GroupId { get; }

    public string Name { get; }

    public string? CurrentRevisionId { get; }

    public TemplateGroupStatus Status { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public static TemplateGroup Create(string groupId, string name, DateTimeOffset createdAt) => new(
        TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID"),
        TemplateValueRules.RequiredText(name, 256, "TPL_GROUP_NAME_INVALID"),
        null,
        TemplateGroupStatus.Active,
        createdAt,
        createdAt);

    public static TemplateGroup Restore(
        string groupId,
        string name,
        string? currentRevisionId,
        TemplateGroupStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) => new(
            TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID"),
            TemplateValueRules.RequiredText(name, 256, "TPL_GROUP_NAME_INVALID"),
            currentRevisionId is null
                ? null
                : TemplateValueRules.Identifier(currentRevisionId, "TPL_REVISION_ID_INVALID"),
            status,
            createdAt,
            updatedAt);

    public TemplateGroup PointTo(
        TemplateRevision revision,
        string? expectedCurrentRevisionId,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!string.Equals(GroupId, revision.GroupId, StringComparison.Ordinal)
            || revision.State != TemplateRevisionState.Committed)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        }

        if (!string.Equals(CurrentRevisionId, expectedCurrentRevisionId, StringComparison.Ordinal))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_CONCURRENCY_CONFLICT");
        }

        return new TemplateGroup(GroupId, Name, revision.RevisionId, Status, CreatedAt, updatedAt);
    }
}

public sealed record TemplatePage
{
    private TemplatePage(
        string pageId,
        string revisionId,
        int order,
        string name,
        TemplatePageContentType contentType,
        TemplateGenerationMode generationMode,
        int? variantCapacity,
        bool enabled,
        string templateResourceId)
    {
        PageId = pageId;
        RevisionId = revisionId;
        Order = order;
        Name = name;
        ContentType = contentType;
        GenerationMode = generationMode;
        VariantCapacity = variantCapacity;
        Enabled = enabled;
        TemplateResourceId = templateResourceId;
    }

    public string PageId { get; }

    public string RevisionId { get; }

    public int Order { get; }

    public string Name { get; }

    public TemplatePageContentType ContentType { get; }

    public TemplateGenerationMode GenerationMode { get; }

    public int? VariantCapacity { get; }

    public bool Enabled { get; }

    public string TemplateResourceId { get; }

    public static TemplatePage Create(
        string pageId,
        string revisionId,
        int order,
        string name,
        TemplatePageContentType contentType,
        TemplateGenerationMode generationMode,
        int? variantCapacity,
        bool enabled,
        string templateResourceId)
    {
        if (order < 0)
        {
            throw new TemplateRuleViolationException("TPL_PAGE_ORDER_INVALID");
        }

        if (generationMode == TemplateGenerationMode.VariantCapacityCombination)
        {
            if (variantCapacity is null or <= 0)
            {
                throw new TemplateRuleViolationException("TPL_PAGE_VARIANT_CAPACITY_INVALID");
            }
        }
        else if (variantCapacity is not null)
        {
            throw new TemplateRuleViolationException("TPL_PAGE_VARIANT_CAPACITY_FORBIDDEN");
        }

        return new TemplatePage(
            TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID"),
            TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID"),
            order,
            TemplateValueRules.RequiredText(name, 256, "TPL_PAGE_NAME_INVALID"),
            contentType,
            generationMode,
            variantCapacity,
            enabled,
            TemplateValueRules.Identifier(templateResourceId, "TPL_TEMPLATE_RESOURCE_ID_INVALID"));
    }

    public TemplatePage InheritTo(string revisionId, int? order = null) => Create(
        PageId,
        revisionId,
        order ?? Order,
        Name,
        ContentType,
        GenerationMode,
        VariantCapacity,
        Enabled,
        TemplateResourceId);

    public TemplatePage Reconfigure(
        int order,
        string name,
        TemplatePageContentType contentType,
        TemplateGenerationMode generationMode,
        int? variantCapacity,
        bool enabled) => Create(
            PageId,
            RevisionId,
            order,
            name,
            contentType,
            generationMode,
            variantCapacity,
            enabled,
            TemplateResourceId);
}

public sealed record FrameBinding
{
    private FrameBinding(
        string frameId,
        string pageId,
        LayerPath layerPath,
        ExpectedLayerEvidence expectedLayer,
        ImmutableArray<SupersededLayerEvidence> supersededLayers,
        SelectionRequirement selectionRequirement,
        LayoutIntent layoutIntent)
    {
        FrameId = frameId;
        PageId = pageId;
        LayerPath = layerPath;
        ExpectedLayer = expectedLayer;
        SupersededLayers = supersededLayers;
        SelectionRequirement = selectionRequirement;
        LayoutIntent = layoutIntent;
    }

    public string FrameId { get; }

    public string PageId { get; }

    public LayerPath LayerPath { get; }

    public ExpectedLayerEvidence ExpectedLayer { get; }

    public ImmutableArray<SupersededLayerEvidence> SupersededLayers { get; }

    public SelectionRequirement SelectionRequirement { get; }

    public LayoutIntent LayoutIntent { get; }

    public static FrameBinding Create(
        string frameId,
        string pageId,
        LayerPath layerPath,
        ExpectedLayerEvidence expectedLayer,
        IEnumerable<SupersededLayerEvidence>? supersededLayers,
        SelectionRequirement selectionRequirement,
        LayoutIntent layoutIntent)
    {
        ArgumentNullException.ThrowIfNull(layerPath);
        ArgumentNullException.ThrowIfNull(expectedLayer);
        ArgumentNullException.ThrowIfNull(selectionRequirement);
        ArgumentNullException.ThrowIfNull(layoutIntent);
        var frozenSuperseded = supersededLayers?.ToImmutableArray() ?? [];
        if (frozenSuperseded.Any(layer => layer is null))
        {
            throw new TemplateRuleViolationException("TPL_FRAME_STRUCTURE_EVIDENCE_INCOMPLETE");
        }

        return new FrameBinding(
            TemplateValueRules.Identifier(frameId, "TPL_FRAME_ID_INVALID"),
            TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID"),
            layerPath,
            expectedLayer,
            frozenSuperseded,
            selectionRequirement,
            layoutIntent);
    }

    public FrameBinding Reconfigure(
        SelectionRequirement? selectionRequirement = null,
        LayoutIntent? layoutIntent = null) => Create(
            FrameId,
            PageId,
            LayerPath,
            ExpectedLayer,
            SupersededLayers,
            selectionRequirement ?? SelectionRequirement,
            layoutIntent ?? LayoutIntent);
}

public sealed record TemplateRevision
{
    private TemplateRevision(
        string revisionId,
        string groupId,
        int revisionNo,
        TemplateRevisionState state,
        string? manifestRef,
        string? ruleDigest,
        DateTimeOffset createdAt,
        ImmutableArray<TemplatePage> pages,
        ImmutableArray<FrameBinding> frameBindings)
    {
        RevisionId = revisionId;
        GroupId = groupId;
        RevisionNo = revisionNo;
        State = state;
        ManifestRef = manifestRef;
        RuleDigest = ruleDigest;
        CreatedAt = createdAt;
        Pages = pages;
        FrameBindings = frameBindings;
    }

    public string RevisionId { get; }

    public string GroupId { get; }

    public int RevisionNo { get; }

    public TemplateRevisionState State { get; }

    public string? ManifestRef { get; }

    public string? RuleDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public ImmutableArray<TemplatePage> Pages { get; }

    public ImmutableArray<FrameBinding> FrameBindings { get; }

    public static TemplateRevision CreateDraft(
        string revisionId,
        string groupId,
        int revisionNo,
        DateTimeOffset createdAt,
        IEnumerable<TemplatePage>? pages = null,
        IEnumerable<FrameBinding>? frameBindings = null) => CreateValidated(
            revisionId,
            groupId,
            revisionNo,
            TemplateRevisionState.Draft,
            null,
            null,
            createdAt,
            pages,
            frameBindings);

    public static TemplateRevision CreateIncrement(
        string revisionId,
        TemplateRevision baseRevision,
        int revisionNo,
        DateTimeOffset createdAt,
        IEnumerable<TemplatePage> newPages,
        IEnumerable<FrameBinding>? newBindings = null)
    {
        ArgumentNullException.ThrowIfNull(baseRevision);
        ArgumentNullException.ThrowIfNull(newPages);
        if (baseRevision.State != TemplateRevisionState.Committed || revisionNo <= baseRevision.RevisionNo)
        {
            throw new TemplateRuleViolationException("TPL_INCREMENT_BASE_INVALID");
        }

        var inheritedPages = baseRevision.Pages.Select(page => page.InheritTo(revisionId));
        var additions = newPages.ToImmutableArray();
        if (additions.Any(page => !string.Equals(page.RevisionId, revisionId, StringComparison.Ordinal)))
        {
            throw new TemplateRuleViolationException("TPL_PAGE_REVISION_MISMATCH");
        }

        return CreateValidated(
            revisionId,
            baseRevision.GroupId,
            revisionNo,
            TemplateRevisionState.Draft,
            null,
            null,
            createdAt,
            inheritedPages.Concat(additions),
            baseRevision.FrameBindings.Concat(newBindings ?? []));
    }

    public static TemplateRevision Restore(
        string revisionId,
        string groupId,
        int revisionNo,
        TemplateRevisionState state,
        string? manifestRef,
        string? ruleDigest,
        DateTimeOffset createdAt,
        IEnumerable<TemplatePage> pages,
        IEnumerable<FrameBinding> frameBindings) => CreateValidated(
            revisionId,
            groupId,
            revisionNo,
            state,
            manifestRef,
            ruleDigest,
            createdAt,
            pages,
            frameBindings);

    public TemplateRevision AdvanceTo(
        TemplateRevisionState nextState,
        RevisionTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (State == TemplateRevisionState.Committed || IsFailureState(State)
            || !IsAllowedTransition(State, nextState))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_TRANSITION_INVALID");
        }

        EnsureTransitionEvidence(nextState, evidence);
        return new TemplateRevision(
            RevisionId,
            GroupId,
            RevisionNo,
            nextState,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages,
            FrameBindings);
    }

    public TemplateRevision Commit(
        string manifestRef,
        string ruleDigest,
        RevisionTransitionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (State != TemplateRevisionState.Committing || !evidence.CommitArtifactsConsistent)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_COMMIT_EVIDENCE_MISSING");
        }

        return new TemplateRevision(
            RevisionId,
            GroupId,
            RevisionNo,
            TemplateRevisionState.Committed,
            TemplateValueRules.RelativeOwnedPath(manifestRef, "TPL_REVISION_MANIFEST_INVALID"),
            TemplateValueRules.Digest(ruleDigest, "TPL_REVISION_RULE_DIGEST_INVALID"),
            CreatedAt,
            Pages,
            FrameBindings);
    }

    public TemplateRevision AddPage(TemplatePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsureDraftEditable();
        if (!string.Equals(page.RevisionId, RevisionId, StringComparison.Ordinal)
            || Pages.Any(existing => string.Equals(existing.PageId, page.PageId, StringComparison.Ordinal)))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_INVALID");
        }

        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages.Add(page),
            FrameBindings);
    }

    public TemplateRevision RemovePage(string pageId)
    {
        EnsureDraftEditable();
        var index = -1;
        for (var candidate = 0; candidate < Pages.Length; candidate++)
        {
            if (string.Equals(Pages[candidate].PageId, pageId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
        }

        var remainingPages = Pages
            .Where(page => !string.Equals(page.PageId, pageId, StringComparison.Ordinal))
            .OrderBy(page => page.Order)
            .Select((page, order) => page.Reconfigure(
                order,
                page.Name,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.Enabled));
        var remainingBindings = FrameBindings.Where(binding =>
            !string.Equals(binding.PageId, pageId, StringComparison.Ordinal));
        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            remainingPages,
            remainingBindings);
    }

    public TemplateRevision ConfigurePage(
        string pageId,
        string name,
        TemplatePageContentType contentType,
        TemplateGenerationMode generationMode,
        int? variantCapacity,
        bool enabled)
    {
        EnsureDraftEditable();
        var index = -1;
        for (var candidate = 0; candidate < Pages.Length; candidate++)
        {
            if (string.Equals(Pages[candidate].PageId, pageId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
        {
            throw new TemplateRuleViolationException("TPL_PAGE_NOT_FOUND");
        }

        var configured = Pages[index].Reconfigure(
            Pages[index].Order,
            name,
            contentType,
            generationMode,
            variantCapacity,
            enabled);
        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages.SetItem(index, configured),
            FrameBindings);
    }

    public TemplateRevision ReorderPages(IEnumerable<string> orderedPageIds)
    {
        ArgumentNullException.ThrowIfNull(orderedPageIds);
        EnsureDraftEditable();
        var order = orderedPageIds.ToImmutableArray();
        if (order.Length != Pages.Length
            || order.Distinct(StringComparer.Ordinal).Count() != Pages.Length
            || order.Any(pageId => Pages.All(page => !string.Equals(
                page.PageId,
                pageId,
                StringComparison.Ordinal))))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_INVALID");
        }

        var byId = Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var reordered = order.Select((pageId, index) => byId[pageId].Reconfigure(
            index,
            byId[pageId].Name,
            byId[pageId].ContentType,
            byId[pageId].GenerationMode,
            byId[pageId].VariantCapacity,
            byId[pageId].Enabled));
        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            reordered,
            FrameBindings);
    }

    public TemplateRevision AddFrameBinding(FrameBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        EnsureDraftEditable();
        if (!Pages.Any(page => string.Equals(page.PageId, binding.PageId, StringComparison.Ordinal))
            || FrameBindings.Any(existing => string.Equals(existing.FrameId, binding.FrameId, StringComparison.Ordinal)))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_BINDING_SET_INVALID");
        }

        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages,
            FrameBindings.Add(binding));
    }

    public TemplateRevision UpdateFrameBinding(
        string frameId,
        SelectionRequirement? selectionRequirement = null,
        LayoutIntent? layoutIntent = null)
    {
        ArgumentNullException.ThrowIfNull(frameId);
        EnsureDraftEditable();
        var index = -1;
        for (var candidate = 0; candidate < FrameBindings.Length; candidate++)
        {
            if (string.Equals(FrameBindings[candidate].FrameId, frameId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            throw new TemplateRuleViolationException("TPL_FRAME_NOT_FOUND");
        }

        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages,
            FrameBindings.SetItem(index, FrameBindings[index].Reconfigure(selectionRequirement, layoutIntent)));
    }

    public TemplateRevision RemoveFrameBinding(string frameId)
    {
        ArgumentNullException.ThrowIfNull(frameId);
        EnsureDraftEditable();
        var index = -1;
        for (var candidate = 0; candidate < FrameBindings.Length; candidate++)
        {
            if (string.Equals(FrameBindings[candidate].FrameId, frameId, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            throw new TemplateRuleViolationException("TPL_FRAME_NOT_FOUND");
        }

        return CreateValidated(
            RevisionId,
            GroupId,
            RevisionNo,
            State,
            ManifestRef,
            RuleDigest,
            CreatedAt,
            Pages,
            FrameBindings.RemoveAt(index));
    }

    private void EnsureDraftEditable()
    {
        if (State == TemplateRevisionState.Committed || IsFailureState(State))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_IMMUTABLE");
        }
    }

    private static TemplateRevision CreateValidated(
        string revisionId,
        string groupId,
        int revisionNo,
        TemplateRevisionState state,
        string? manifestRef,
        string? ruleDigest,
        DateTimeOffset createdAt,
        IEnumerable<TemplatePage>? pages,
        IEnumerable<FrameBinding>? frameBindings)
    {
        if (revisionNo <= 0)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_NUMBER_INVALID");
        }

        var normalizedRevisionId = TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID");
        var frozenPages = pages?.ToImmutableArray() ?? [];
        var frozenBindings = frameBindings?.ToImmutableArray() ?? [];
        if (frozenPages.Any(page => page is null
                || !string.Equals(page.RevisionId, normalizedRevisionId, StringComparison.Ordinal))
            || frozenPages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count() != frozenPages.Length
            || frozenPages.Select(page => page.Order).Distinct().Count() != frozenPages.Length)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_PAGE_SET_INVALID");
        }

        var pageIds = frozenPages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        if (frozenBindings.Any(binding => binding is null || !pageIds.Contains(binding.PageId))
            || frozenBindings.Select(binding => binding.FrameId).Distinct(StringComparer.Ordinal).Count()
                != frozenBindings.Length)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_BINDING_SET_INVALID");
        }

        if (state == TemplateRevisionState.Committed && (manifestRef is null || ruleDigest is null))
        {
            throw new TemplateRuleViolationException("TPL_REVISION_COMMIT_EVIDENCE_MISSING");
        }


        if (manifestRef is not null)
        {
            manifestRef = TemplateValueRules.RelativeOwnedPath(manifestRef, "TPL_REVISION_MANIFEST_INVALID");
        }

        if (ruleDigest is not null)
        {
            ruleDigest = TemplateValueRules.Digest(ruleDigest, "TPL_REVISION_RULE_DIGEST_INVALID");
        }

        return new TemplateRevision(
            normalizedRevisionId,
            TemplateValueRules.Identifier(groupId, "TPL_GROUP_ID_INVALID"),
            revisionNo,
            state,
            manifestRef,
            ruleDigest,
            createdAt,
            frozenPages,
            frozenBindings);
    }

    private static bool IsFailureState(TemplateRevisionState state) => state is
        TemplateRevisionState.ImportFailed
        or TemplateRevisionState.InspectionBlocked
        or TemplateRevisionState.CommitFailed
        or TemplateRevisionState.Abandoned;

    private static bool IsAllowedTransition(TemplateRevisionState current, TemplateRevisionState next) =>
        IsFailureState(next)
        || (current, next) is
            (TemplateRevisionState.Draft, TemplateRevisionState.ImportingCopy)
            or (TemplateRevisionState.ImportingCopy, TemplateRevisionState.DuplicateReview)
            or (TemplateRevisionState.ImportingCopy, TemplateRevisionState.StructureInspecting)
            or (TemplateRevisionState.DuplicateReview, TemplateRevisionState.StructureInspecting)
            or (TemplateRevisionState.StructureInspecting, TemplateRevisionState.FrameBinding)
            or (TemplateRevisionState.FrameBinding, TemplateRevisionState.Calibrating)
            or (TemplateRevisionState.Calibrating, TemplateRevisionState.AwaitingReview)
            or (TemplateRevisionState.AwaitingReview, TemplateRevisionState.Previewing)
            or (TemplateRevisionState.AwaitingReview, TemplateRevisionState.Committing)
            or (TemplateRevisionState.Previewing, TemplateRevisionState.Committing);

    private static void EnsureTransitionEvidence(
        TemplateRevisionState nextState,
        RevisionTransitionEvidence evidence)
    {
        var valid = nextState switch
        {
            TemplateRevisionState.ImportingCopy => evidence.SourceReadable,
            TemplateRevisionState.DuplicateReview => evidence.CopyMatchesSource,
            TemplateRevisionState.StructureInspecting => evidence.CopyMatchesSource,
            TemplateRevisionState.FrameBinding => evidence.StructureReadable,
            TemplateRevisionState.Calibrating => evidence.RegisteredFramesHaveEvidence,
            TemplateRevisionState.AwaitingReview => evidence.CalibrationComplete,
            TemplateRevisionState.Previewing => evidence.ConfigurationValid,
            TemplateRevisionState.Committing => evidence.ConfigurationValid,
            _ when IsFailureState(nextState) => true,
            _ => false
        };

        if (!valid)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_TRANSITION_EVIDENCE_MISSING");
        }
    }
}

public sealed record CalibrationDraft
{
    private CalibrationDraft(
        string draftId,
        string baseRevisionId,
        ImmutableArray<string> affectedPageIds,
        TemplateRevisionState state,
        long lastEventSequence)
    {
        DraftId = draftId;
        BaseRevisionId = baseRevisionId;
        AffectedPageIds = affectedPageIds;
        State = state;
        LastEventSequence = lastEventSequence;
    }

    public string DraftId { get; }

    public string BaseRevisionId { get; }

    public ImmutableArray<string> AffectedPageIds { get; }

    public TemplateRevisionState State { get; }

    public long LastEventSequence { get; }

    public static CalibrationDraft Create(
        string draftId,
        string baseRevisionId,
        IEnumerable<string> affectedPageIds,
        TemplateRevisionState state = TemplateRevisionState.Draft,
        long lastEventSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(affectedPageIds);
        var frozen = affectedPageIds
            .Select(pageId => TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID"))
            .ToImmutableArray();
        if (frozen.IsDefaultOrEmpty || frozen.Distinct(StringComparer.Ordinal).Count() != frozen.Length
            || lastEventSequence < 0 || state == TemplateRevisionState.Committed)
        {
            throw new TemplateRuleViolationException("TPL_CALIBRATION_DRAFT_INVALID");
        }

        return new CalibrationDraft(
            TemplateValueRules.Identifier(draftId, "TPL_DRAFT_ID_INVALID"),
            TemplateValueRules.Identifier(baseRevisionId, "TPL_REVISION_ID_INVALID"),
            frozen,
            state,
            lastEventSequence);
    }
}

public sealed record CalibrationSample
{
    private CalibrationSample(
        string frameId,
        string assetReference,
        LayoutIntent geometry,
        bool confirmed,
        CalibrationSampleSource source)
    {
        FrameId = frameId;
        AssetReference = assetReference;
        Geometry = geometry;
        Confirmed = confirmed;
        Source = source;
    }

    public string FrameId { get; }

    public string AssetReference { get; }

    public LayoutIntent Geometry { get; }

    public bool Confirmed { get; }

    public CalibrationSampleSource Source { get; }

    public static CalibrationSample CreateUserSelected(
        string frameId,
        string assetReference,
        LayoutIntent geometry,
        bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return new CalibrationSample(
            TemplateValueRules.Identifier(frameId, "TPL_FRAME_ID_INVALID"),
            TemplateValueRules.Identifier(assetReference, "TPL_CALIBRATION_ASSET_REF_INVALID"),
            geometry,
            confirmed,
            CalibrationSampleSource.UserSelected);
    }
}

public sealed record TemplatePreviewState
{
    private TemplatePreviewState(
        string revisionId,
        string pageId,
        TemplatePreviewStatus status,
        string? evidenceReference,
        TemplatePreviewEvidenceKind? evidenceKind,
        DateTimeOffset createdAt)
    {
        RevisionId = revisionId;
        PageId = pageId;
        Status = status;
        EvidenceReference = evidenceReference;
        EvidenceKind = evidenceKind;
        CreatedAt = createdAt;
    }

    public string RevisionId { get; }

    public string PageId { get; }

    public TemplatePreviewStatus Status { get; }

    public string? EvidenceReference { get; }

    public TemplatePreviewEvidenceKind? EvidenceKind { get; }

    public DateTimeOffset CreatedAt { get; }

    public static TemplatePreviewState NotPreviewed(
        string revisionId,
        string pageId,
        DateTimeOffset createdAt) => new(
            TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID"),
            TemplateValueRules.Identifier(pageId, "TPL_PAGE_ID_INVALID"),
            TemplatePreviewStatus.NotPreviewed,
            null,
            null,
            createdAt);

    public TemplatePreviewState RecordSuccess(
        string evidenceReference,
        TemplatePreviewEvidenceKind evidenceKind,
        DateTimeOffset createdAt) => new(
            RevisionId,
            PageId,
            TemplatePreviewStatus.Succeeded,
            TemplateValueRules.Identifier(evidenceReference, "TPL_PREVIEW_EVIDENCE_INVALID"),
            evidenceKind,
            createdAt);
}

public sealed record BatchTemplatePageSnapshot(
    string PageId,
    int Order,
    TemplatePageContentType ContentType,
    TemplateGenerationMode GenerationMode,
    int? VariantCapacity,
    string TemplateResourceId,
    ImmutableArray<string> FrameIds);

public sealed record BatchTemplateSnapshot
{
    private BatchTemplateSnapshot(
        string snapshotId,
        string batchId,
        string revisionId,
        ImmutableArray<BatchTemplatePageSnapshot> pages,
        string rulesDigest,
        DateTimeOffset createdAt)
    {
        SnapshotId = snapshotId;
        BatchId = batchId;
        RevisionId = revisionId;
        Pages = pages;
        RulesDigest = rulesDigest;
        CreatedAt = createdAt;
    }

    public string SnapshotId { get; }

    public string BatchId { get; }

    public string RevisionId { get; }

    public ImmutableArray<BatchTemplatePageSnapshot> Pages { get; }

    public string RulesDigest { get; }

    public DateTimeOffset CreatedAt { get; }

    public static BatchTemplateSnapshot Create(
        string snapshotId,
        string batchId,
        TemplateRevision revision,
        IEnumerable<string> selectedPageIds,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(selectedPageIds);
        if (revision.State != TemplateRevisionState.Committed || revision.RuleDigest is null)
        {
            throw new TemplateRuleViolationException("TPL_REVISION_NOT_COMMITTED");
        }

        var selected = selectedPageIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            throw new TemplateRuleViolationException("TPL_BATCH_PAGE_SELECTION_INVALID");
        }

        var revisionPages = revision.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        if (selected.Any(pageId => !revisionPages.TryGetValue(pageId, out var page) || !page.Enabled))
        {
            throw new TemplateRuleViolationException("TPL_BATCH_PAGE_SELECTION_INVALID");
        }

        var bindingsByPage = revision.FrameBindings
            .GroupBy(binding => binding.PageId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(binding => binding.FrameId).Order(StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        var pages = revision.Pages
            .Where(page => selected.Contains(page.PageId))
            .OrderBy(page => page.Order)
            .Select(page => new BatchTemplatePageSnapshot(
                page.PageId,
                page.Order,
                page.ContentType,
                page.GenerationMode,
                page.VariantCapacity,
                page.TemplateResourceId,
                bindingsByPage.GetValueOrDefault(page.PageId, [])))
            .ToImmutableArray();

        return new BatchTemplateSnapshot(
            TemplateValueRules.Identifier(snapshotId, "TPL_SNAPSHOT_ID_INVALID"),
            TemplateValueRules.Identifier(batchId, "TPL_BATCH_ID_INVALID"),
            revision.RevisionId,
            pages,
            revision.RuleDigest,
            createdAt);
    }

    public static BatchTemplateSnapshot Restore(
        string snapshotId,
        string batchId,
        string revisionId,
        IEnumerable<BatchTemplatePageSnapshot> pages,
        string rulesDigest,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var frozenPages = pages.ToImmutableArray();
        if (frozenPages.IsDefaultOrEmpty
            || frozenPages.Any(page => page is null)
            || frozenPages.Select(page => page.PageId).Distinct(StringComparer.Ordinal).Count() != frozenPages.Length
            || frozenPages.Select(page => page.Order).Distinct().Count() != frozenPages.Length)
        {
            throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_INVALID");
        }

        return new BatchTemplateSnapshot(
            TemplateValueRules.Identifier(snapshotId, "TPL_SNAPSHOT_ID_INVALID"),
            TemplateValueRules.Identifier(batchId, "TPL_BATCH_ID_INVALID"),
            TemplateValueRules.Identifier(revisionId, "TPL_REVISION_ID_INVALID"),
            frozenPages,
            TemplateValueRules.Digest(rulesDigest, "TPL_REVISION_RULE_DIGEST_INVALID"),
            createdAt);
    }

    public BatchTemplateSnapshot ReplaceForDraftBatch(
        string newSnapshotId,
        TemplateRevision newRevision,
        IEnumerable<string> selectedPageIds,
        bool analysisStarted,
        DateTimeOffset createdAt)
    {
        if (analysisStarted)
        {
            throw new TemplateRuleViolationException("TPL_BATCH_SNAPSHOT_FROZEN");
        }

        return Create(newSnapshotId, BatchId, newRevision, selectedPageIds, createdAt);
    }
}
