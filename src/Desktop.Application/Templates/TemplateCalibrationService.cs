using System.Collections.Concurrent;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed class TemplateCalibrationService(
    ITemplateRepository templates,
    ITemplateResourceRepository resources,
    ICalibrationStateRepository? calibrationStates = null,
    ICalibrationAssetSourcePort? assetSources = null,
    IPathPolicy? paths = null,
    TemplatePageImportService? importer = null) : ITemplateCalibrationService
{
    private const string DefaultSelectionRequirement = "商品主体清晰完整，构图适合当前图片框。";
    private readonly ConcurrentDictionary<string, CalibrationState> _states = new(StringComparer.Ordinal);

    public async Task<TemplateCalibrationDraftView> LoadAsync(
        TemplateDraftSession session,
        string pageId,
        CancellationToken cancellationToken)
    {
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
        var page = revision.Pages.SingleOrDefault(item => string.Equals(item.PageId, pageId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("TPL_PAGE_NOT_FOUND");
        if (page.ContentType != TemplatePageContentType.NormalComposition)
            throw new InvalidOperationException("TPL_PAGE_TYPE_NOT_NORMAL");
        var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");

        if (session.CanEditPage(pageId) && importer is not null)
            resource = await importer.EnsureCurrentInspectionAsync(resource, cancellationToken).ConfigureAwait(false);

        var key = Key(session, pageId);
        if (!_states.TryGetValue(key, out var state))
        {
            state = CreateState(
                pageId,
                resource.Inspection,
                GetTemplatePreviewSource(resource));
            var persisted = await LoadPersistedStateAsync(
                session,
                pageId,
                cancellationToken).ConfigureAwait(false);
            state.Apply(persisted);
            foreach (var binding in revision.FrameBindings.Where(binding => string.Equals(
                         binding.PageId,
                         pageId,
                         StringComparison.Ordinal)))
            {
                state.ApplyBinding(binding);
            }

            // Older page-edit drafts stored their calibration checkpoint under
            // the committed revision id.  Once the rule has been projected
            // onto the current page graph, copy the normalized snapshot to the
            // active draft so a later restart does not depend on the legacy
            // lookup path.
            if (persisted is not null
                && !string.Equals(persisted.DraftId, DraftId(session, pageId), StringComparison.Ordinal)
                && calibrationStates is not null
                && session.CanEditPage(pageId))
            {
                var activeDraftId = DraftId(session, pageId);
                await templates.SaveCalibrationDraftAsync(
                    CalibrationDraft.Create(
                        activeDraftId,
                        session.DraftRevisionId,
                        [pageId],
                        TemplateRevisionState.Calibrating,
                        persisted.Sequence),
                    cancellationToken).ConfigureAwait(false);
                await calibrationStates.SaveCalibrationStateAsync(
                    state.ToSnapshot(activeDraftId, pageId),
                    cancellationToken).ConfigureAwait(false);
            }

            _states.TryAdd(key, state);
        }
        if (revision.RuleWorkspace is { } rules && state.RuleVersion != rules.Version)
        {
            // Registration is owned by the persisted revision, not a stale calibration cache.
            state.RegisteredFrames.Clear();
            state.PersistedBindingIds.Clear();
            foreach (var binding in revision.FrameBindings.Where(binding => binding.PageId == pageId)) state.ApplyBinding(binding);
            state.RuleVersion = rules.Version;
        }
        return state.ToView(
            revision.RevisionId,
            pageId,
            !session.CanEditPage(pageId));
    }

    public async Task<TemplateCalibrationDraftView> BindAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string assetId,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        var frame = state.Frames.SingleOrDefault(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("TPL_FRAME_NOT_FOUND");
        if (!frame.IsPreviewSupported)
        {
            throw new InvalidOperationException("TPL_PSD_UNSUPPORTED_STRUCTURE");
        }

        var normalizedAssetId = assetId.Trim();
        if (normalizedAssetId.Length == 0 || normalizedAssetId.Length > 128)
        {
            throw new InvalidOperationException("TPL_CALIBRATION_ASSET_REF_INVALID");
        }

        if (!state.Assets.ContainsKey(normalizedAssetId))
        {
            state.Assets[normalizedAssetId] = new TemplateCalibrationAsset(
                normalizedAssetId,
                normalizedAssetId,
                normalizedAssetId,
                frame.Width,
                frame.Height);
        }
        state.SelectedAssetId = normalizedAssetId;
        state.SelectedFrameId = frameId;
        state.Push(new TemplateCalibrationOperation(
            $"operation_{Guid.NewGuid():N}",
            frameId,
            state.FrameAssets.GetValueOrDefault(frameId),
            normalizedAssetId,
            state.GetGeometry(frameId).Scale,
            state.GetGeometry(frameId).Scale,
            state.GetGeometry(frameId).OffsetX,
            state.GetGeometry(frameId).OffsetX,
            state.GetGeometry(frameId).OffsetY,
            state.GetGeometry(frameId).OffsetY));
        state.FrameAssets[frameId] = normalizedAssetId;
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
        state.SelectionRequirements.TryAdd(frameId, DefaultSelectionRequirement);
        var existingBinding = FindPersistedBinding(revision, state, frameId);
        if (existingBinding is null)
        {
            state.PersistedBindingIds[frameId] = NewBindingIdentity(revision, pageId, frameId, state.Frames.Single(frame => frame.FrameId == frameId));
            await templates.SaveDraftRevisionAsync(
                revision.AddFrameBinding(state.BuildFrameBinding(pageId, frameId)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            state.PersistedBindingIds[frameId] = existingBinding.FrameId;
            await templates.SaveDraftRevisionAsync(
                revision.UpdateFrameBinding(
                    existingBinding.FrameId,
                    selectionRequirement: state.BuildSelectionRequirement(
                        frameId,
                        existingBinding.SelectionRequirement.Criteria),
                    layoutIntent: state.BuildLayoutIntent(frameId)),
                cancellationToken).ConfigureAwait(false);
        }
        state.RegisteredFrames.Add(frameId);
        await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
        return state.ToView(view.RevisionId, pageId, isReadOnly: false);
    }

    private static string NormalizeStructureSignature(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? $"sha256:{normalized["sha256:".Length..].ToLowerInvariant()}"
            : $"sha256:{normalized.ToLowerInvariant()}";
    }

    public async Task<TemplateCalibrationDraftView> BindReadOnlyAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        if (assetSources is null)
        {
            throw new InvalidOperationException("TPL_CALIBRATION_ASSET_SOURCE_UNAVAILABLE");
        }

        var asset = await assetSources.InspectReadOnlyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        state.Assets[asset.AssetId] = asset;
        return await BindAssetAsync(
            session,
            pageId,
            frameId,
            asset.AssetId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateCalibrationDraftView> ClearAssetAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        var frame = state.Frames.SingleOrDefault(item => string.Equals(
            item.FrameId,
            frameId,
            StringComparison.Ordinal))
            ?? throw new InvalidOperationException("TPL_FRAME_NOT_FOUND");
        var previousAssetId = state.FrameAssets.GetValueOrDefault(frameId);
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
        if (previousAssetId is null)
        {
            state.SelectedFrameId = frameId;
            state.SelectedAssetId = null;
            state.RegisteredFrames.Remove(frameId);
            var existingBinding = FindPersistedBinding(revision, state, frameId);
            if (existingBinding is not null)
            {
                await templates.SaveDraftRevisionAsync(
                    revision.RemoveFrameBinding(existingBinding.FrameId),
                    cancellationToken).ConfigureAwait(false);
            }
            state.PersistedBindingIds.Remove(frameId);
            await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
            return state.ToView(view.RevisionId, pageId, isReadOnly: false);
        }

        var geometry = state.GetGeometry(frameId);
        state.Push(new TemplateCalibrationOperation(
            $"operation_{Guid.NewGuid():N}",
            frameId,
            previousAssetId,
            null,
            geometry.Scale,
            geometry.Scale,
            geometry.OffsetX,
            geometry.OffsetX,
            geometry.OffsetY,
            geometry.OffsetY));
        state.FrameAssets.Remove(frameId);
        state.RegisteredFrames.Remove(frameId);
        state.SelectedFrameId = frameId;
        state.SelectedAssetId = state.FrameAssets.Values.FirstOrDefault();

        if (revision.FrameBindings.Any(binding => string.Equals(binding.FrameId, frameId, StringComparison.Ordinal)))
        {
            await templates.SaveDraftRevisionAsync(
                revision.RemoveFrameBinding(frameId),
                cancellationToken).ConfigureAwait(false);
        }

        await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
        return state.ToView(view.RevisionId, pageId, isReadOnly: false);
    }

    public async Task<TemplateCalibrationDraftView> SetGeometryAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        double scale,
        double offsetX,
        double offsetY,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        if (!state.Frames.Any(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("TPL_FRAME_NOT_FOUND");
        }

        var previous = state.GetGeometry(frameId);
        var next = new Geometry(scale, offsetX, offsetY);
        next.Validate();
        state.SelectedFrameId = frameId;
        if (previous != next)
        {
            state.Push(new TemplateCalibrationOperation(
                $"operation_{Guid.NewGuid():N}",
                frameId,
                state.FrameAssets.GetValueOrDefault(frameId),
                state.FrameAssets.GetValueOrDefault(frameId),
                previous.Scale,
                next.Scale,
                previous.OffsetX,
                next.OffsetX,
                previous.OffsetY,
                next.OffsetY));
            state.FrameGeometry[frameId] = next;
        }
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
        var existingBinding = FindPersistedBinding(revision, state, frameId);
        if (existingBinding is not null)
        {
            await templates.SaveDraftRevisionAsync(
                revision.UpdateFrameBinding(
                    existingBinding.FrameId,
                    layoutIntent: state.BuildLayoutIntent(frameId)),
                cancellationToken).ConfigureAwait(false);
        }
        await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
        return state.ToView(view.RevisionId, pageId, isReadOnly: false);
    }

    public async Task<TemplateCalibrationDraftView> ApplyHistoryAsync(
        TemplateDraftSession session,
        string pageId,
        CalibrationUndoAction action,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        if (action == CalibrationUndoAction.Undo && state.Undo.Count > 0)
        {
            var operation = state.Undo.Pop();
            state.Redo.Push(operation);
            Apply(state, operation, undo: true);
            state.SelectedFrameId = operation.FrameId;
            state.SelectedAssetId = state.FrameAssets.GetValueOrDefault(operation.FrameId);
        }
        else if (action == CalibrationUndoAction.Redo && state.Redo.Count > 0)
        {
            var operation = state.Redo.Pop();
            state.Undo.Push(operation);
            Apply(state, operation, undo: false);
            state.SelectedFrameId = operation.FrameId;
            state.SelectedAssetId = state.FrameAssets.GetValueOrDefault(operation.FrameId);
        }

        if (state.SelectedFrameId is not null)
        {
            var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
            var frameId = state.SelectedFrameId;
            var existingBinding = FindPersistedBinding(revision, state, frameId);
            var hasBinding = existingBinding is not null;
            if (state.FrameAssets.ContainsKey(frameId))
            {
                state.RegisteredFrames.Add(frameId);
                await templates.SaveDraftRevisionAsync(
                    hasBinding
                        ? revision.UpdateFrameBinding(
                            existingBinding!.FrameId,
                            selectionRequirement: state.BuildSelectionRequirement(
                                frameId,
                                existingBinding.SelectionRequirement.Criteria),
                            layoutIntent: state.BuildLayoutIntent(frameId))
                        : revision.AddFrameBinding(state.BuildFrameBinding(pageId, frameId)),
                    cancellationToken).ConfigureAwait(false);
                state.PersistedBindingIds[frameId] = existingBinding?.FrameId ?? NewBindingIdentity(revision, pageId, frameId, state.Frames.Single(frame => frame.FrameId == frameId));
            }
            else
            {
                state.RegisteredFrames.Remove(frameId);
                if (hasBinding)
                {
                    await templates.SaveDraftRevisionAsync(
                        revision.RemoveFrameBinding(existingBinding!.FrameId),
                        cancellationToken).ConfigureAwait(false);
                }
                state.PersistedBindingIds.Remove(frameId);
            }
        }

        await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
        return state.ToView(view.RevisionId, pageId, isReadOnly: false);
    }

    public async Task<TemplateCalibrationDraftView> SetSelectionRequirementAsync(
        TemplateDraftSession session,
        string pageId,
        string frameId,
        string selectionRequirement,
        CancellationToken cancellationToken)
    {
        EnsurePageEditable(session, pageId);
        var view = await LoadAsync(session, pageId, cancellationToken).ConfigureAwait(false);
        var state = _states[Key(session, pageId)];
        var normalized = TemplateValueRules.RequiredText(
            selectionRequirement,
            2000,
            "TPL_SELECTION_REQUIREMENT_INVALID");
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_REVISION_NOT_FOUND");
        var binding = FindPersistedBinding(revision, state, frameId)
            ;
        var identified = state.Frames.SingleOrDefault(frame => frame.FrameId == frameId
            || frame.PersistedFrameId == frameId);
        if (binding is not null && revision.IsFrameExcluded(binding)
            || identified is not null && revision.RuleWorkspace?.IsCandidateExcluded(pageId,
                identified.TemplateSha256, identified.StructureSignature, identified.LayerId, identified.LayerPath) == true)
            throw new InvalidOperationException("TPL_RULE_REFERENCE_INACTIVE");
        if (binding is null)
        {
            var target = state.Frames.SingleOrDefault(frame => frame.FrameId == frameId)
                ?? throw new InvalidOperationException("TPL_FRAME_NOT_FOUND");
            if (!CanSetRule(target, state.CanvasWidth, state.CanvasHeight))
                throw new InvalidOperationException("TPL_PSD_UNSUPPORTED_STRUCTURE");
            // A template can be authored from PSD structure alone.  A rule-only
            // binding is deliberate and is resolved against a product's photos
            // later during batch analysis; it never fabricates an asset id.
            state.SelectionRequirements[frameId] = normalized;
            state.PersistedBindingIds[frameId] = NewBindingIdentity(revision, pageId, frameId, state.Frames.Single(frame => frame.FrameId == frameId));
            binding = state.BuildFrameBinding(pageId, frameId);
            await templates.SaveDraftRevisionAsync(
                revision.AddFrameBinding(binding),
                cancellationToken).ConfigureAwait(false);
            state.PersistedBindingIds[frameId] = NewBindingIdentity(revision, pageId, frameId, state.Frames.Single(frame => frame.FrameId == frameId));
            state.RegisteredFrames.Add(frameId);
        }
        else
        {
            await templates.SaveDraftRevisionAsync(
                revision.UpdateFrameBinding(
                    binding.FrameId,
                    selectionRequirement: SelectionRequirement.Create(
                        normalized,
                        binding.SelectionRequirement.Criteria),
                    layoutIntent: state.BuildLayoutIntent(frameId)),
                cancellationToken).ConfigureAwait(false);
            state.PersistedBindingIds[frameId] = binding.FrameId;
            state.RegisteredFrames.Add(frameId);
        }
        state.SelectionRequirements[frameId] = normalized;
        state.SelectedFrameId = frameId;
        await PersistDraftAsync(session, pageId, state, cancellationToken).ConfigureAwait(false);
        return state.ToView(view.RevisionId, pageId, isReadOnly: false);
    }

    private async Task PersistDraftAsync(
        TemplateDraftSession session,
        string pageId,
        CalibrationState state,
        CancellationToken cancellationToken)
    {
        var draftId = DraftId(session, pageId);
        var draft = CalibrationDraft.Create(
            draftId,
            session.DraftRevisionId,
            [pageId],
            TemplateRevisionState.Calibrating,
            state.Sequence);
        await templates.SaveCalibrationDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        if (calibrationStates is not null)
        {
            await calibrationStates.SaveCalibrationStateAsync(
                state.ToSnapshot(draftId, pageId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CalibrationStateSnapshot?> LoadPersistedStateAsync(
        TemplateDraftSession session,
        string pageId,
        CancellationToken cancellationToken)
    {
        if (calibrationStates is null)
        {
            return null;
        }

        var currentDraft = await calibrationStates.GetCalibrationStateAsync(
            DraftId(session, pageId),
            pageId,
            cancellationToken).ConfigureAwait(false);
        if (currentDraft is not null)
        {
            return currentDraft;
        }

        // Page-edit drafts inherit a committed page but historically kept the
        // calibration checkpoint under that committed revision id.  Resolve
        // the committed revision through the group rather than guessing a
        // revision number, then use it only as a read-compatible fallback.
        var group = await templates.GetGroupAsync(session.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var committedRevisionId = group?.CurrentRevisionId;
        if (string.IsNullOrWhiteSpace(committedRevisionId)
            || string.Equals(committedRevisionId, session.DraftRevisionId, StringComparison.Ordinal))
        {
            return null;
        }

        return await calibrationStates.GetCalibrationStateAsync(
            DraftId(committedRevisionId, pageId),
            pageId,
            cancellationToken).ConfigureAwait(false);
    }

    private static FrameBinding? FindPersistedBinding(
        TemplateRevision revision,
        CalibrationState state,
        string frameId)
    {
        var persistedFrameId = state.ResolvePersistedBindingId(frameId);
        return revision.FrameBindings.SingleOrDefault(binding =>
            string.Equals(binding.FrameId, persistedFrameId, StringComparison.Ordinal)
            && binding.PageId == state.Frames.SingleOrDefault(frame => frame.FrameId == frameId)?.PageId);
    }

    internal static string NewBindingIdentity(TemplateRevision revision, string pageId, string frameId,
        TemplateFrameProjection? target = null)
    {
        var legacy = revision.RuleWorkspace is null ? frameId
            : "frame_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(pageId + ":" + frameId)));
        if (target is not null && revision.RuleWorkspace is { } rules
            && !rules.Aliases.Any(alias => alias.PageId == pageId && alias.FrameId == legacy))
        {
            var reserved = ReferenceLearningService.StableFrameId(pageId, target.LayerId, target.LayerPath);
            if (rules.Aliases.Any(alias => alias.PageId == pageId && alias.FrameId == reserved)) return reserved;
        }
        return legacy;
    }

    internal static string CandidateBindingIdentity(TemplateRevision revision, TemplateFrameProjection target)
    {
        var legacy = NewBindingIdentity(revision, target.PageId, target.FrameId);
        return revision.RuleWorkspace?.Aliases.Any(alias => alias.PageId == target.PageId && alias.FrameId == legacy) == true
            ? legacy : ReferenceLearningService.StableFrameId(target.PageId, target.LayerId, target.LayerPath);
    }

    internal static bool CanSetRule(TemplateFrameProjection target, int width, int height) =>
        target.IsPreviewSupported && target.IsVisible && target.HasCompositeContribution != false
        && target.LayerPath.Count > 0 && target.LayerPath.All(segment => segment >= 0 && segment < int.MaxValue)
        && Math.Min(width, (long)target.Left + target.Width) > Math.Max(0, target.Left)
        && Math.Min(height, (long)target.Top + target.Height) > Math.Max(0, target.Top);

    internal static FrameBinding CreateRuleOnlyBinding(TemplateRevision revision, TemplateFrameProjection target,
        PsdCanvasInspection canvas, string description)
    {
        if (!CanSetRule(target, canvas.Width, canvas.Height) || target.HasCompositeContribution != true)
            throw new TemplateRuleViolationException("TPL_PSD_UNSUPPORTED_STRUCTURE");
        return FrameBinding.Create(CandidateBindingIdentity(revision, target), target.PageId,
            LayerPath.Create(target.LayerPath.Select(segment => checked(segment + 1))),
            ExpectedLayerEvidence.Create(target.LayerId, target.LayerName, target.IsVisible, target.IsClipping,
                target.HasMask, target.HasVectorMask, target.Opacity, target.BlendMode,
                LayerStructureSignature.Create(NormalizeStructureSignature(target.StructureSignature)), target.LayerKind), target.SupersededLayers,
            SelectionRequirement.Create(description), LayoutIntent.Create("subject_complete", 1, 0, 0,
                target.PreviewBounds is { } visible
                    ? TemplateRect.Create(Math.Max(0, visible.Left), Math.Max(0, visible.Top),
                        Math.Min(canvas.Width, visible.Left + visible.Width), Math.Min(canvas.Height, visible.Top + visible.Height))
                    : TemplateRect.Create(Math.Max(0, target.Left), Math.Max(0, target.Top),
                        Math.Min(canvas.Width, (double)target.Left + target.Width), Math.Min(canvas.Height, (double)target.Top + target.Height))));
    }

    private static void EnsurePageEditable(TemplateDraftSession session, string pageId)
    {
        if (!session.CanEditPage(pageId))
        {
            throw new InvalidOperationException("TPL_INCREMENT_ORIGINAL_PAGE_READ_ONLY");
        }
    }

    private string? GetTemplatePreviewSource(ImportedTemplateResource resource)
    {
        if (paths is null || resource.Inspection.Preview is null)
        {
            return null;
        }

        return paths.ResolveOwnedPath(TemplatePageImportService.GetPreviewRelativePath(
            resource.InternalRelativePath));
    }

    private static CalibrationState CreateState(
        string pageId,
        PsdInspectionDocument inspection,
        string? templatePreviewSource)
    {
        return new CalibrationState(InspectFrames(pageId, inspection), inspection.Canvas.Width,
            inspection.Canvas.Height, templatePreviewSource);
    }

    internal static IReadOnlyList<TemplateFrameProjection> InspectFrames(string pageId, PsdInspectionDocument inspection)
    {
        return inspection.Layers
            .Where(layer => layer.Visible && layer.EffectiveVisible
                && (layer.SmartObject is not null || layer.Kind == "pixel"
                    && layer.PixelEvidence.HasReplaceableRasterContent)
                && !IsCoveredPhotoEdgeResidual(inspection, layer))
            .Select(layer => new TemplateFrameProjection(
                $"frame_{layer.LayerId}",
                pageId,
                layer.Path,
                layer.LayerId,
                layer.Name,
                NormalizeStructureSignature(inspection.StructureSignature),
                layer.Bounds.Left,
                layer.Bounds.Top,
                Math.Max(1, layer.Bounds.Right - layer.Bounds.Left),
                Math.Max(1, layer.Bounds.Bottom - layer.Bounds.Top),
                layer.HasMask,
                layer.HasVectorMask,
                layer.Visible,
                layer.Clipping,
                checked((byte)Math.Clamp(layer.Opacity, byte.MinValue, byte.MaxValue)),
                layer.BlendMode,
                IsPreviewSupported: !layer.HasMask
                    && !layer.HasVectorMask
                    && (layer.SmartObject is null || IsAxisAlignedTransform(layer.SmartObject.TransformBox)),
                IsRegistered: false)
            {
                LayerKind = layer.SmartObject is null ? TemplateLayerKind.Pixel : TemplateLayerKind.SmartObject,
                SupersededLayers = CoveredPhotoStack(inspection, layer),
                TemplateSha256 = inspection.FileSha256,
                HasCompositeContribution = layer.PixelEvidence.Available
                    ? layer.PixelEvidence.HasMeaningfulCompositeContribution
                        && !IsCoveredPhotoEdgeResidual(inspection, layer)
                    : null,
                PreviewBounds = TemplatePlacementResolver.VisibleFrameBounds(layer) is { } bounds
                    ? new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                    : null
            })
            .ToArray();
    }

    // A fully covered embedded photograph can acquire a one-pixel perimeter in
    // an inspected, Photoshop-saved PSD. Its contribution bbox then spans the
    // whole window even though it is not another independently replaceable slot.
    // Only dismiss the lower of two adjacent, full-window photographs in the
    // same explicit clipping stack; small photographs in their own windows stay.
    internal static bool IsCoveredPhotoEdgeResidual(PsdInspectionDocument inspection, PsdLayerInspection old)
    {
        var isPhoto = old.Kind == "pixel" && old.PixelEvidence.HasReplaceableRasterContent
            || old.Kind == "smartobject" && old.SmartObject is
                { Kind: "data", EmbeddedContentAvailable: true, IsPsd: false } oldPhoto
                && oldPhoto.DetectedFileType is "jpg" or "jpeg" or "png";
        if (!isPhoto || !old.Visible || !old.EffectiveVisible || !old.Clipping
            || old.ClippingBase is not { Visible: true, EffectiveVisible: true } window
            || old.HasMask || old.HasVectorMask || old.Opacity != 255 || old.FillOpacity != 255
            || old.BlendMode is not ("norm" or "normal") || old.Path.Count == 0
            || !old.PixelEvidence.HasMeaningfulCompositeContribution) return false;

        var bounds = new PsdLayerBounds(Math.Max(0, window.Bounds.Left), Math.Max(0, window.Bounds.Top),
            Math.Min(inspection.Canvas.Width, window.Bounds.Right), Math.Min(inspection.Canvas.Height, window.Bounds.Bottom));
        var width = (long)bounds.Right - bounds.Left;
        var height = (long)bounds.Bottom - bounds.Top;
        if (width < 4 || height < 4 || old.PixelEvidence.CompositeContributionBounds != bounds
            || old.PixelEvidence.CompositeContributionPixelCount > 2 * (width + height)
            || old.Bounds.Left > bounds.Left || old.Bounds.Top > bounds.Top
            || old.Bounds.Right < bounds.Right || old.Bounds.Bottom < bounds.Bottom) return false;

        var upper = inspection.Layers.SingleOrDefault(layer => layer.Path.Count == old.Path.Count
            && layer.Path.Take(layer.Path.Count - 1).SequenceEqual(old.Path.Take(old.Path.Count - 1))
            && (long)layer.Path[^1] == (long)old.Path[^1] + 1);
        if (upper is null || !upper.Visible || !upper.EffectiveVisible || !upper.Clipping
            || upper.ClippingBase is null || upper.ClippingBase.LayerId != window.LayerId
            || !upper.ClippingBase.Path.SequenceEqual(window.Path)
            || upper.HasMask || upper.HasVectorMask || upper.Opacity != 255 || upper.FillOpacity != 255
            || upper.BlendMode is not ("norm" or "normal")
            || upper.Bounds.Left > bounds.Left || upper.Bounds.Top > bounds.Top
            || upper.Bounds.Right < bounds.Right || upper.Bounds.Bottom < bounds.Bottom
            || !upper.PixelEvidence.Available || upper.PixelEvidence.RenderedPixelCount <= 0
            || upper.PixelEvidence.CompositeContributionBounds != bounds
            || upper.PixelEvidence.CompositeContributionPixelCount < .9 * width * height) return false;
        return upper.Kind == "pixel" && upper.PixelEvidence.HasReplaceableRasterContent
            || upper.Kind == "smartobject" && upper.SmartObject is
                { Kind: "data", EmbeddedContentAvailable: true, IsPsd: false } photo
                && photo.DetectedFileType is "jpg" or "jpeg" or "png";
    }

    private static List<SupersededLayerEvidence> CoveredPhotoStack(PsdInspectionDocument inspection,
        PsdLayerInspection target)
    {
        var photograph = target.Kind == "pixel" && target.PixelEvidence.HasReplaceableRasterContent
            || target.SmartObject is { Kind: "data", EmbeddedContentAvailable: true, IsPsd: false } image
                && image.DetectedFileType is "jpg" or "jpeg" or "png" && image.EmbeddedContentSha256 is { Length: 64 }
                && target.PixelEvidence.HasMeaningfulCompositeContribution;
        if (!photograph
            || !target.Clipping || target.ClippingBase is not { Visible: true, EffectiveVisible: true } window
            || target.Opacity != 255 || target.FillOpacity != 255 || target.BlendMode is not ("norm" or "normal")
            || target.HasMask || target.HasVectorMask || target.Path.Count == 0) return [];
        var bounds = new PsdLayerBounds(Math.Max(0, window.Bounds.Left), Math.Max(0, window.Bounds.Top),
            Math.Min(inspection.Canvas.Width, window.Bounds.Right), Math.Min(inspection.Canvas.Height, window.Bounds.Bottom));
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || !Contains(target.Bounds, bounds)) return [];
        var siblings = inspection.Layers.Where(layer => layer.Path.Count == target.Path.Count
            && layer.Path.Take(layer.Path.Count - 1).SequenceEqual(target.Path.Take(target.Path.Count - 1)))
            .ToDictionary(layer => layer.Path[^1]);
        var result = new List<SupersededLayerEvidence>();
        // Only consecutive lower embedded photographs in this explicit window.
        // Stop at an adjustment, decoration, other frame or missing evidence;
        // never broaden this into every smart object or a rectangle heuristic.
        for (var index = target.Path[^1] - 1; index >= 0; index--)
        {
            if (!siblings.TryGetValue(index, out var old) || !old.Visible || !old.EffectiveVisible || !old.Clipping
                || old.ClippingBase is null || old.ClippingBase.LayerId != window.LayerId
                || !old.ClippingBase.Path.SequenceEqual(window.Path) || old.HasMask || old.HasVectorMask
                || old.Opacity != 255 || old.FillOpacity != 255 || old.BlendMode is not ("norm" or "normal")
                || !old.PixelEvidence.Available || old.PixelEvidence.RenderedPixelCount <= 0
                || old.PixelEvidence.CompositeContributionPixelCount is < 0 or > 1 || !Contains(old.Bounds, bounds)
                || old.SmartObject is not { Kind: "data", EmbeddedContentAvailable: true, IsPsd: false } oldImage
                || oldImage.DetectedFileType is not ("jpg" or "jpeg" or "png")
                || oldImage.EmbeddedContentSha256 is not { Length: 64 }) break;
            result.Add(SupersededLayerEvidence.Create(LayerPath.Create(old.Path.Select(part => checked(part + 1))),
                expectedClipping: true, old.LayerId));
        }
        return result;

        static bool Contains(PsdLayerBounds image, PsdLayerBounds window) => image.Left <= window.Left
            && image.Top <= window.Top && image.Right >= window.Right && image.Bottom >= window.Bottom;
    }

    public static IReadOnlyList<TemplateFrameProjection> OrderFramesVisually(IEnumerable<TemplateFrameProjection> frames)
    {
        var remaining = frames.OrderBy(frame => frame.PreviewBounds?.Top ?? frame.Top)
            .ThenBy(frame => frame.PreviewBounds?.Left ?? frame.Left).ThenBy(frame => frame.LayerId).ToList();
        var ordered = new List<TemplateFrameProjection>(remaining.Count);
        while (remaining.Count > 0)
        {
            var first = remaining[0];
            var top = first.PreviewBounds?.Top ?? first.Top;
            var height = first.PreviewBounds?.Height ?? first.Height;
            // Small vertical offsets within a row must not reverse left-to-right reading.
            var row = remaining.Where(frame => (frame.PreviewBounds?.Top ?? frame.Top) - top
                <= Math.Max(4, Math.Min(height, frame.PreviewBounds?.Height ?? frame.Height) * 0.15))
                .OrderBy(frame => frame.PreviewBounds?.Left ?? frame.Left)
                .ThenBy(frame => frame.PreviewBounds?.Top ?? frame.Top).ThenBy(frame => frame.LayerId).ToArray();
            ordered.AddRange(row);
            foreach (var frame in row) remaining.Remove(frame);
        }
        return ordered;
    }

    private static bool IsAxisAlignedTransform(IReadOnlyList<double> transformBox)
    {
        if (transformBox.Count == 0)
        {
            return true;
        }
        if (transformBox.Count != 8 || transformBox.Any(value => !double.IsFinite(value)))
        {
            return false;
        }

        const double tolerance = 0.5;
        return Math.Abs(transformBox[1] - transformBox[3]) <= tolerance
            && Math.Abs(transformBox[2] - transformBox[4]) <= tolerance
            && Math.Abs(transformBox[5] - transformBox[7]) <= tolerance
            && Math.Abs(transformBox[6] - transformBox[0]) <= tolerance;
    }

    private static void Apply(CalibrationState state, TemplateCalibrationOperation operation, bool undo)
    {
        var asset = undo ? operation.PreviousAssetId : operation.NextAssetId;
        if (asset is null)
        {
            state.FrameAssets.Remove(operation.FrameId);
        }
        else
        {
            state.FrameAssets[operation.FrameId] = asset;
        }

        state.FrameGeometry[operation.FrameId] = new Geometry(
            undo ? operation.PreviousScale : operation.NextScale,
            undo ? operation.PreviousOffsetX : operation.NextOffsetX,
            undo ? operation.PreviousOffsetY : operation.NextOffsetY);
    }

    private static string Key(TemplateDraftSession session, string pageId) =>
        $"{session.DraftRevisionId}:{pageId}";

    private static string DraftId(TemplateDraftSession session, string pageId) =>
        $"calibration_{session.DraftRevisionId}_{pageId}";

    private static string DraftId(string revisionId, string pageId) =>
        $"calibration_{revisionId}_{pageId}";

    private sealed class CalibrationState(
        IReadOnlyList<TemplateFrameProjection> frames,
        int canvasWidth,
        int canvasHeight,
        string? templatePreviewSource)
    {
        public IReadOnlyList<TemplateFrameProjection> Frames { get; } = frames;
        public int CanvasWidth { get; } = canvasWidth;
        public int CanvasHeight { get; } = canvasHeight;
        public string? TemplatePreviewSource { get; } = templatePreviewSource;
        public Dictionary<string, TemplateCalibrationAsset> Assets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> FrameAssets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Geometry> FrameGeometry { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SelectionRequirements { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PersistedBindingIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TemplateFramePreviewBounds> PreviewBounds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RegisteredFrames { get; } = new(StringComparer.Ordinal);
        public Stack<TemplateCalibrationOperation> Undo { get; } = new();
        public Stack<TemplateCalibrationOperation> Redo { get; } = new();
        public long Sequence { get; private set; }
        public long RuleVersion { get; set; } = -1;
        public string? SelectedAssetId { get; set; }
        public string? SelectedFrameId { get; set; }

        public Geometry GetGeometry(string frameId) => FrameGeometry.GetValueOrDefault(frameId, new Geometry(1, 0, 0));

        public string ResolvePersistedBindingId(string frameId) =>
            PersistedBindingIds.GetValueOrDefault(frameId, frameId);

        public LayoutIntent BuildLayoutIntent(string frameId)
        {
            var frame = Frames.Single(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal));
            var geometry = GetGeometry(frameId);
            return LayoutIntent.Create(
                "subject_complete",
                geometry.Scale,
                geometry.OffsetX,
                geometry.OffsetY,
                frame.PreviewBounds is { } visible
                    ? TemplateRect.Create(Math.Max(0, visible.Left), Math.Max(0, visible.Top),
                        Math.Min(CanvasWidth, visible.Left + visible.Width), Math.Min(CanvasHeight, visible.Top + visible.Height))
                    : TemplateRect.Create(
                        Math.Max(0, frame.Left), Math.Max(0, frame.Top),
                        Math.Min(CanvasWidth, frame.Left + frame.Width), Math.Min(CanvasHeight, frame.Top + frame.Height)));
        }

        public SelectionRequirement BuildSelectionRequirement(
            string frameId,
            IEnumerable<SelectionCriterion>? existingCriteria = null)
        {
            var description = SelectionRequirements.GetValueOrDefault(
                frameId,
                DefaultSelectionRequirement);
            description = NormalizeSelectionRequirement(description);
            var assetId = FrameAssets.GetValueOrDefault(frameId);
            var criteria = existingCriteria is null
                ? []
                : existingCriteria
                    .Where(item => !string.Equals(item.Key, "asset_id", StringComparison.Ordinal))
                    .ToArray();
            if (assetId is not null)
            {
                criteria = criteria
                    .Append(SelectionCriterion.Create("asset_id", assetId))
                    .ToArray();
            }
            return SelectionRequirement.Create(
                description,
                criteria);
        }

        public FrameBinding BuildFrameBinding(string pageId, string frameId)
        {
            var frame = Frames.Single(item => string.Equals(
                item.FrameId,
                frameId,
                StringComparison.Ordinal));
            return FrameBinding.Create(
                ResolvePersistedBindingId(frameId),
                pageId,
                LayerPath.Create(frame.LayerPath.Select(segment => checked(segment + 1))),
                ExpectedLayerEvidence.Create(
                    frame.LayerId,
                    frame.LayerName,
                    frame.IsVisible,
                    frame.IsClipping,
                    frame.HasMask,
                    frame.HasVectorMask,
                    frame.Opacity,
                    frame.BlendMode,
                    LayerStructureSignature.Create(NormalizeStructureSignature(frame.StructureSignature)), frame.LayerKind),
                frame.SupersededLayers,
                BuildSelectionRequirement(frameId),
                BuildLayoutIntent(frameId));
        }

        public CalibrationStateSnapshot ToSnapshot(string draftId, string pageId) => new(
            draftId,
            pageId,
            Sequence,
            SelectedAssetId,
            SelectedFrameId,
            Assets.Values.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToArray(),
            Frames.Select(frame => new CalibrationFrameState(
                frame.FrameId,
                FrameAssets.GetValueOrDefault(frame.FrameId),
                GetGeometry(frame.FrameId).Scale,
                GetGeometry(frame.FrameId).OffsetX,
                GetGeometry(frame.FrameId).OffsetY)).ToArray());

        public void Apply(CalibrationStateSnapshot? snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            foreach (var asset in snapshot.Assets)
            {
                Assets[asset.AssetId] = asset;
            }

            foreach (var frame in snapshot.Frames)
            {
                if (!Frames.Any(candidate => string.Equals(candidate.FrameId, frame.FrameId, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (frame.AssetId is not null)
                {
                    FrameAssets[frame.FrameId] = frame.AssetId;
                }

                var geometry = new Geometry(frame.Scale, frame.OffsetX, frame.OffsetY);
                geometry.Validate();
                FrameGeometry[frame.FrameId] = geometry;
            }

            SelectedAssetId = snapshot.SelectedAssetId;
            SelectedFrameId = snapshot.SelectedFrameId;
            Sequence = Math.Max(Sequence, snapshot.Sequence);
        }

        public void ApplyBinding(FrameBinding binding)
        {
            var frame = ResolveFrame(binding);
            if (frame is null)
            {
                return;
            }

            var frameId = frame.FrameId;
            PersistedBindingIds[frameId] = binding.FrameId;
            // A saved photo-layer rectangle must not replace the inspected visible frame on reopen.
            var bounds = frame.PreviewBounds is { } visible
                ? TemplateRect.Create(Math.Max(0, visible.Left), Math.Max(0, visible.Top),
                    Math.Min(CanvasWidth, visible.Left + visible.Width), Math.Min(CanvasHeight, visible.Top + visible.Height))
                : binding.LayoutIntent.SafeBounds;
            PreviewBounds[frameId] = new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            SelectionRequirements[frameId] = NormalizeSelectionRequirement(
                binding.SelectionRequirement.Description);
            RegisteredFrames.Add(frameId);
            FrameGeometry[frameId] = new Geometry(
                binding.LayoutIntent.Scale,
                binding.LayoutIntent.OffsetX,
                binding.LayoutIntent.OffsetY);
            var asset = binding.SelectionRequirement.Criteria.SingleOrDefault(item =>
                string.Equals(item.Key, "asset_id", StringComparison.Ordinal))?.Value;
            if (asset is not null)
            {
                EnsureAsset(asset, binding, frame);
                FrameAssets[frameId] = asset;
                SelectedAssetId ??= asset;
                SelectedFrameId ??= frameId;
            }
        }

        private TemplateFrameProjection? ResolveFrame(FrameBinding binding)
        {
            var exact = Frames.SingleOrDefault(frame => string.Equals(
                frame.FrameId,
                binding.FrameId,
                StringComparison.Ordinal));
            if (exact is not null)
            {
                return exact;
            }

            var byEvidence = Frames.Where(frame =>
                    frame.LayerId == binding.ExpectedLayer.LayerId
                    && string.Equals(
                        NormalizeStructureSignature(frame.StructureSignature),
                        NormalizeStructureSignature(binding.ExpectedLayer.StructureSignature.Value),
                        StringComparison.OrdinalIgnoreCase)
                    && frame.LayerPath.Select(segment => segment + 1).SequenceEqual(binding.LayerPath.Segments))
                .ToArray();
            if (byEvidence.Length == 1)
            {
                return byEvidence[0];
            }

            var byLayer = Frames.Where(frame => frame.LayerId == binding.ExpectedLayer.LayerId).ToArray();
            return byLayer.Length == 1 ? byLayer[0] : null;
        }

        private void EnsureAsset(
            string assetId,
            FrameBinding binding,
            TemplateFrameProjection frame)
        {
            if (Assets.ContainsKey(assetId))
            {
                return;
            }

            var displayName = binding.SelectionRequirement.Criteria.SingleOrDefault(item =>
                string.Equals(item.Key, "reference_file_name", StringComparison.Ordinal))?.Value;
            Assets[assetId] = new TemplateCalibrationAsset(
                assetId,
                string.IsNullOrWhiteSpace(displayName) ? assetId : displayName,
                string.Empty,
                frame.Width,
                frame.Height);
        }

        private static string NormalizeSelectionRequirement(string value)
        {
            var normalized = value.Trim();
            return normalized switch
            {
                "Select one user-provided asset for this registered frame."
                    or "Choose one source whose subject fits this registered frame."
                    or "请为这个图片框选择一张代表款商品图。"
                    or "请选择一张代表款商品图放入当前图片框。"
                    => DefaultSelectionRequirement,
                _ => normalized.Length == 0 ? DefaultSelectionRequirement : normalized
            };
        }

        public void Push(TemplateCalibrationOperation operation)
        {
            Undo.Push(operation);
            Redo.Clear();
            Sequence++;
        }

        public TemplateCalibrationDraftView ToView(string revisionId, string pageId, bool isReadOnly)
        {
            // Prefer a frame that already has a persisted rule when an edit
            // draft is first opened. A stale selection can otherwise point at
            // an unregistered smart object and make the saved rule appear to
            // have disappeared in the inspector.
            var selectedFrameId = SelectedFrameId;
            if (RegisteredFrames.Count > 0
                && (selectedFrameId is null || !RegisteredFrames.Contains(selectedFrameId)))
            {
                selectedFrameId = RegisteredFrames
                    .OrderBy(frameId => frameId, StringComparer.Ordinal)
                    .First();
            }

            var projectedFrames = Frames.Select(frame => frame with
            {
                PreviewBounds = PreviewBounds.GetValueOrDefault(frame.FrameId) ?? frame.PreviewBounds,
                BoundAssetId = FrameAssets.GetValueOrDefault(frame.FrameId),
                Scale = GetGeometry(frame.FrameId).Scale,
                OffsetX = GetGeometry(frame.FrameId).OffsetX,
                OffsetY = GetGeometry(frame.FrameId).OffsetY,
                SelectionRequirement = SelectionRequirements.GetValueOrDefault(
                    frame.FrameId,
                    string.Empty),
                IsRegistered = RegisteredFrames.Contains(frame.FrameId)
                    && FrameAssets.ContainsKey(frame.FrameId),
                HasPersistedBinding = RegisteredFrames.Contains(frame.FrameId),
                PersistedFrameId = RegisteredFrames.Contains(frame.FrameId) ? ResolvePersistedBindingId(frame.FrameId) : null,
                IsSelected = string.Equals(frame.FrameId, selectedFrameId, StringComparison.Ordinal)
            }).ToArray();
            var selectedFrame = projectedFrames.SingleOrDefault(frame => frame.IsSelected);
            return new TemplateCalibrationDraftView(
                revisionId,
                pageId,
                CanvasWidth,
                CanvasHeight,
                TemplatePreviewSource,
                isReadOnly,
                projectedFrames,
                Assets.Values.OrderBy(asset => asset.DisplayName, StringComparer.Ordinal).ToArray(),
                selectedFrame?.BoundAssetId,
                selectedFrameId,
                selectedFrame?.Scale ?? 1,
                selectedFrame?.OffsetX ?? 0,
                selectedFrame?.OffsetY ?? 0,
                selectedFrame?.SelectionRequirement ?? string.Empty,
                Frames.Count == 0
                    ? "当前 PSD 没有带完整结构证据的候选框，已阻断校准。"
                    : Frames.All(frame => !frame.IsPreviewSupported)
                        ? "候选框包含本地画布无法可靠表达的蒙版或变换，请运行兼容性检查。"
                    : FrameAssets.Count == 0
                        ? "请选择一个候选框并绑定代表款素材；其他智能对象不会自动注册。"
                        : $"已绑定 {FrameAssets.Count} 个图片框；校准草稿已保存。",
                Undo.Count > 0,
                Redo.Count > 0,
                Frames.Count == 0 || Frames.All(frame => !frame.IsPreviewSupported));
        }
    }

    private readonly record struct Geometry(double Scale, double OffsetX, double OffsetY)
    {
        public void Validate()
        {
            if (!double.IsFinite(Scale) || Scale <= 0 || Scale > 20
                || !double.IsFinite(OffsetX) || !double.IsFinite(OffsetY))
            {
                throw new InvalidOperationException("TPL_LAYOUT_INTENT_INVALID");
            }
        }
    }
}
