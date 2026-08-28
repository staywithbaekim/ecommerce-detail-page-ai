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
    IPathPolicy? paths = null) : ITemplateCalibrationService
{
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
        var resource = await resources.GetByResourceIdAsync(page.TemplateResourceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("TPL_TEMPLATE_RESOURCE_NOT_FOUND");

        var key = Key(session, pageId);
        if (!_states.TryGetValue(key, out var state))
        {
            state = CreateState(
                pageId,
                resource.Inspection,
                GetTemplatePreviewSource(resource));
            if (calibrationStates is not null)
            {
                var persisted = await calibrationStates.GetCalibrationStateAsync(
                    DraftId(session, pageId),
                    pageId,
                    cancellationToken).ConfigureAwait(false);
                state.Apply(persisted);
            }
            foreach (var binding in revision.FrameBindings.Where(binding => string.Equals(
                         binding.PageId,
                         pageId,
                         StringComparison.Ordinal)))
            {
                state.ApplyBinding(binding);
            }

            _states.TryAdd(key, state);
        }
        return state.ToView(
            revision.RevisionId,
            pageId,
            session.IsIncremental && session.BasePageIds.Contains(pageId));
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
        state.SelectionRequirements[frameId] = "请为这个图片框选择一张代表款商品图。";
        if (!revision.FrameBindings.Any(binding => string.Equals(binding.FrameId, frameId, StringComparison.Ordinal)))
        {
            await templates.SaveDraftRevisionAsync(
                revision.AddFrameBinding(state.BuildFrameBinding(pageId, frameId)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await templates.SaveDraftRevisionAsync(
                revision.UpdateFrameBinding(
                    frameId,
                    selectionRequirement: state.BuildSelectionRequirement(frameId),
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
            if (revision.FrameBindings.Any(binding => string.Equals(
                    binding.FrameId,
                    frameId,
                    StringComparison.Ordinal)))
            {
                await templates.SaveDraftRevisionAsync(
                    revision.RemoveFrameBinding(frameId),
                    cancellationToken).ConfigureAwait(false);
            }
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
        if (revision.FrameBindings.Any(binding => string.Equals(binding.FrameId, frameId, StringComparison.Ordinal)))
        {
            await templates.SaveDraftRevisionAsync(
                revision.UpdateFrameBinding(frameId, layoutIntent: state.BuildLayoutIntent(frameId)),
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
            var hasBinding = revision.FrameBindings.Any(binding => string.Equals(
                binding.FrameId,
                frameId,
                StringComparison.Ordinal));
            if (state.FrameAssets.ContainsKey(frameId))
            {
                state.RegisteredFrames.Add(frameId);
                await templates.SaveDraftRevisionAsync(
                    hasBinding
                        ? revision.UpdateFrameBinding(
                            frameId,
                            selectionRequirement: state.BuildSelectionRequirement(frameId),
                            layoutIntent: state.BuildLayoutIntent(frameId))
                        : revision.AddFrameBinding(state.BuildFrameBinding(pageId, frameId)),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                state.RegisteredFrames.Remove(frameId);
                if (hasBinding)
                {
                    await templates.SaveDraftRevisionAsync(
                        revision.RemoveFrameBinding(frameId),
                        cancellationToken).ConfigureAwait(false);
                }
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
        var binding = revision.FrameBindings.SingleOrDefault(item => string.Equals(
            item.FrameId,
            frameId,
            StringComparison.Ordinal))
            ?? throw new InvalidOperationException("TPL_FRAME_NOT_FOUND");
        await templates.SaveDraftRevisionAsync(
            revision.UpdateFrameBinding(
                frameId,
                selectionRequirement: SelectionRequirement.Create(
                    normalized,
                    binding.SelectionRequirement.Criteria)),
            cancellationToken).ConfigureAwait(false);
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

    private static void EnsurePageEditable(TemplateDraftSession session, string pageId)
    {
        if (session.IsIncremental && session.BasePageIds.Contains(pageId))
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
        var frames = inspection.Layers
            .Where(layer => layer.SmartObject is not null && layer.Visible && layer.EffectiveVisible)
            .Select(layer => new TemplateFrameProjection(
                $"frame_{layer.LayerId}",
                pageId,
                layer.Path,
                layer.LayerId,
                layer.Name,
                inspection.StructureSignature,
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
                    && IsAxisAlignedTransform(layer.SmartObject!.TransformBox),
                IsRegistered: false))
            .ToArray();
        return new CalibrationState(
            frames,
            inspection.Canvas.Width,
            inspection.Canvas.Height,
            templatePreviewSource);
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
        public HashSet<string> RegisteredFrames { get; } = new(StringComparer.Ordinal);
        public Stack<TemplateCalibrationOperation> Undo { get; } = new();
        public Stack<TemplateCalibrationOperation> Redo { get; } = new();
        public long Sequence { get; private set; }
        public string? SelectedAssetId { get; set; }
        public string? SelectedFrameId { get; set; }

        public Geometry GetGeometry(string frameId) => FrameGeometry.GetValueOrDefault(frameId, new Geometry(1, 0, 0));

        public LayoutIntent BuildLayoutIntent(string frameId)
        {
            var frame = Frames.Single(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal));
            var geometry = GetGeometry(frameId);
            return LayoutIntent.Create(
                "subject_complete",
                geometry.Scale,
                geometry.OffsetX,
                geometry.OffsetY,
                TemplateRect.Create(0, 0, frame.Width, frame.Height));
        }

        public SelectionRequirement BuildSelectionRequirement(string frameId)
        {
            var description = SelectionRequirements.GetValueOrDefault(
                frameId,
                "请选择一张代表款商品图放入当前图片框。");
            description = NormalizeSelectionRequirement(description);
            var assetId = FrameAssets.GetValueOrDefault(frameId);
            return SelectionRequirement.Create(
                description,
                assetId is null ? [] : [SelectionCriterion.Create("asset_id", assetId)]);
        }

        public FrameBinding BuildFrameBinding(string pageId, string frameId)
        {
            var frame = Frames.Single(item => string.Equals(
                item.FrameId,
                frameId,
                StringComparison.Ordinal));
            return FrameBinding.Create(
                frameId,
                pageId,
                LayerPath.Create(frame.LayerPath),
                ExpectedLayerEvidence.Create(
                    frame.LayerId,
                    frame.LayerName,
                    frame.IsVisible,
                    frame.IsClipping,
                    frame.HasMask,
                    frame.HasVectorMask,
                    frame.Opacity,
                    frame.BlendMode,
                    LayerStructureSignature.Create(NormalizeStructureSignature(frame.StructureSignature))),
                [],
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
            if (!Frames.Any(frame => string.Equals(frame.FrameId, binding.FrameId, StringComparison.Ordinal)))
            {
                return;
            }

            SelectionRequirements[binding.FrameId] = NormalizeSelectionRequirement(
                binding.SelectionRequirement.Description);
            RegisteredFrames.Add(binding.FrameId);
            FrameGeometry[binding.FrameId] = new Geometry(
                binding.LayoutIntent.Scale,
                binding.LayoutIntent.OffsetX,
                binding.LayoutIntent.OffsetY);
            var asset = binding.SelectionRequirement.Criteria.SingleOrDefault(item =>
                string.Equals(item.Key, "asset_id", StringComparison.Ordinal))?.Value;
            if (asset is not null)
            {
                FrameAssets[binding.FrameId] = asset;
                SelectedAssetId ??= asset;
                SelectedFrameId ??= binding.FrameId;
            }
        }

        private static string NormalizeSelectionRequirement(string value)
        {
            var normalized = value.Trim();
            return normalized switch
            {
                "Select one user-provided asset for this registered frame."
                    or "Choose one source whose subject fits this registered frame."
                    => "请选择一张代表款商品图放入当前图片框。",
                _ => normalized.Length == 0 ? "请选择一张代表款商品图放入当前图片框。" : normalized
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
            var geometry = SelectedFrameId is null ? new Geometry(1, 0, 0) : GetGeometry(SelectedFrameId);
            var projectedFrames = Frames.Select(frame => frame with
            {
                IsRegistered = RegisteredFrames.Contains(frame.FrameId)
                    && FrameAssets.ContainsKey(frame.FrameId),
                HasPersistedBinding = RegisteredFrames.Contains(frame.FrameId),
                IsSelected = string.Equals(frame.FrameId, SelectedFrameId, StringComparison.Ordinal)
            }).ToArray();
            return new TemplateCalibrationDraftView(
                revisionId,
                pageId,
                CanvasWidth,
                CanvasHeight,
                TemplatePreviewSource,
                isReadOnly,
                projectedFrames,
                Assets.Values.OrderBy(asset => asset.DisplayName, StringComparer.Ordinal).ToArray(),
                SelectedAssetId,
                SelectedFrameId,
                geometry.Scale,
                geometry.OffsetX,
                geometry.OffsetY,
                SelectedFrameId is null
                    ? string.Empty
                    : SelectionRequirements.GetValueOrDefault(SelectedFrameId, string.Empty),
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
