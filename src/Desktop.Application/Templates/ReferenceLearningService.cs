using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed class ReferenceLearningService(
    IPsdInspectionPort inspection,
    IReferencePhotoFingerprintPort photoFingerprints,
    IPathPolicy paths,
    IReferenceLearningSessionStore? sessions = null,
    TimeProvider? timeProvider = null) : IReferenceLearningService
{
    private readonly ConcurrentDictionary<string, ReferenceLearningDocument> active = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<ReferenceLearningResult> AnalyzeAsync(
        ReferenceLearningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsIdentifier(request.LearningId)
            || !IsIdentifier(request.PageId)
            || string.IsNullOrWhiteSpace(request.InternalPsdRelativePath)
            || string.IsNullOrWhiteSpace(request.ReferencePhotoDirectory)
            || !Path.IsPathFullyQualified(request.ReferencePhotoDirectory))
        {
            return Failure("TPL_LEARN_REQUEST_INVALID", "学习输入不完整。", "请选择带图 PSD 和对应源照片文件夹。");
        }

        string ownedPsdPath;
        try
        {
            ownedPsdPath = paths.ResolveOwnedPath(request.InternalPsdRelativePath);
        }
        catch (InvalidOperationException)
        {
            return Failure("TPL_PSD_PATH_NOT_OWNED", "学习只接受应用内部 PSD 副本。", "先导入 PSD，再重新开始学习。");
        }

        if (!File.Exists(ownedPsdPath))
        {
            return Failure("TPL_SOURCE_UNREADABLE", "应用内部 PSD 副本不存在。", "重新导入 PSD 后重试。", retryable: true);
        }

        PsdInspectionResult inspectionResult;
        try
        {
            inspectionResult = await inspection.InspectAsync(
                new PsdInspectionRequest(
                    request.LearningId,
                    request.InternalPsdRelativePath,
                    request.PreviewRelativePath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure("TPL_PSD_WORKER_FAILED", "PSD 检查组件异常退出。", "重试检查或改用 Photoshop 兼容性检查。", true);
        }

        if (!inspectionResult.IsSuccess)
        {
            var error = inspectionResult.Error!;
            return Failure(error.Code, error.Message, error.UserAction, error.Retryable);
        }

        ReferencePhotoInventory photos;
        try
        {
            photos = await photoFingerprints.ScanAsync(
                request.ReferencePhotoDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            return Failure("TPL_LEARN_REFERENCE_FOLDER_INVALID", "源照片文件夹不存在。", "选择制作参考 PSD 时使用的源照片文件夹。");
        }
        catch (InvalidOperationException error)
        {
            var code = error.Message.Split(':', 2)[0] switch
            {
                "TPL_LEARN_REFERENCE_PHOTOS_MISSING" => "TPL_LEARN_REFERENCE_PHOTOS_MISSING",
                "TPL_LEARN_REFERENCE_PHOTO_LIMIT" => "TPL_LEARN_REFERENCE_PHOTO_LIMIT",
                _ => "TPL_LEARN_REFERENCE_PHOTO_INVALID"
            };
            return Failure(code, "源照片集合无法安全读取。", "补齐可读取的 JPG/PNG 源照片后重试。");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                "TPL_LEARN_REFERENCE_FOLDER_UNREADABLE",
                "源照片文件夹没有读取权限。",
                "检查文件夹权限后重试。",
                retryable: true);
        }
        catch (IOException)
        {
            return Failure(
                "TPL_LEARN_REFERENCE_FOLDER_UNREADABLE",
                "源照片文件夹读取失败。",
                "关闭占用文件的程序后重试。",
                retryable: true);
        }

        var document = BuildDocument(request, inspectionResult.Inspection!, photos, clock.GetUtcNow());
        var saveFailure = await TrySaveAsync(document, cancellationToken).ConfigureAwait(false);
        if (saveFailure is not null)
        {
            return saveFailure;
        }
        active[document.LearningId] = document;

        return ReferenceLearningResult.Success(document);
    }

    public async Task<ReferenceLearningResult> ApplyReviewAsync(
        ReferenceLearningDocument document,
        IReadOnlyList<ReferenceFrameReview> reviews,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reviews);
        if (document.State is ReferenceLearningState.Cancelled or ReferenceLearningState.Committed)
        {
            return Failure("TPL_LEARN_SESSION_CLOSED", "学习会话已经结束。", "重新开始一次学习会话。");
        }
        if (!active.TryGetValue(document.LearningId, out var current)
            || current.UpdatedAt != document.UpdatedAt
            || !string.Equals(current.RulesDigest, document.RulesDigest, StringComparison.Ordinal))
        {
            return Failure("TPL_LEARN_SESSION_STALE", "学习会话不是最新版本。", "恢复最新会话后重新提交复核。");
        }

        var candidates = document.Candidates.ToDictionary(candidate => candidate.FrameId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var review in reviews)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(review.FrameId) || !candidates.TryGetValue(review.FrameId, out var candidate))
                {
                    return Failure("TPL_LEARN_REVIEW_FRAME_INVALID", "复核引用的图片框不存在或重复。", "刷新候选列表后重新提交复核。");
                }

                if (review.Decision == ReferenceReviewDecision.Accepted)
                {
                    if (!candidate.IsProduct)
                    {
                        return Failure(
                            "TPL_LEARN_FIXED_CONTENT",
                            "当前对象是模板固定信息，不是商品图片框。",
                            "请保留模板内容，或返回拖拽校准重新定义图片框。");
                    }

                    var assetId = review.ReferenceAssetId ?? candidate.ReferenceAssetId;
                    if (assetId is null
                        || document.ReferencePhotos.All(photo => !string.Equals(photo.AssetId, assetId, StringComparison.Ordinal)))
                    {
                        return Failure("TPL_LEARN_REVIEW_ASSET_INVALID", "复核选择的源照片不属于参考文件夹。", "只能选择本次参考照片文件夹中的源照片。");
                    }

                    var selectedPhoto = document.ReferencePhotos.Single(photo =>
                        string.Equals(photo.AssetId, assetId, StringComparison.Ordinal));
                    var semanticLabel = string.IsNullOrWhiteSpace(review.SemanticLabel)
                        ? candidate.SemanticLabel
                        : review.SemanticLabel.Trim();
                    if (string.IsNullOrWhiteSpace(semanticLabel))
                    {
                        return Failure(
                            "TPL_LEARN_REVIEW_SEMANTIC_REQUIRED",
                            "图片框用途尚未确认。",
                            "请用简短文字说明这个图片框需要的商品图，例如“完整正面图”。");
                    }
                    var layout = BuildReviewedLayout(candidate.LayoutIntent, review);
                    candidates[review.FrameId] = candidate with
                    {
                        MatchState = string.Equals(
                            candidate.EmbeddedContentSha256,
                            selectedPhoto.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                            ? ReferenceMatchState.Exact
                            : ReferenceMatchState.ManuallyConfirmed,
                        ReferenceAssetId = selectedPhoto.AssetId,
                        ReferencePhotoRelativePath = selectedPhoto.RelativePath,
                        Confidence = 1,
                        ReviewDecision = ReferenceReviewDecision.Accepted,
                        SemanticLabel = semanticLabel,
                        LayoutIntent = layout,
                        ReviewNote = NormalizeNote(review.Note)
                    };
                }
                else
                {
                    candidates[review.FrameId] = candidate.Review(
                        review.Decision,
                        review.SemanticLabel,
                        BuildReviewedLayout(candidate.LayoutIntent, review),
                        review.Note);
                }
            }
        }
        catch (TemplateRuleViolationException)
        {
            return Failure("TPL_LEARN_REVIEW_GEOMETRY_INVALID", "复核几何参数超出安全边界。", "调整缩放和偏移后重试。");
        }

        var ordered = document.Candidates
            .Select(candidate => candidates[candidate.FrameId])
            .ToArray();
        var allDecided = ordered.All(candidate => candidate.ReviewDecision != ReferenceReviewDecision.Pending);
        var accepted = ordered.Count(candidate => candidate.IsHighConfidence);
        var ready = accepted > 0 && allDecided && ordered
            .Where(candidate => candidate.ReviewDecision != ReferenceReviewDecision.Rejected)
            .All(candidate => candidate.IsHighConfidence);
        var requiresManualCalibration = ordered.Any(candidate =>
                candidate.ReviewDecision == ReferenceReviewDecision.ManualCalibration)
            || allDecided && accepted == 0;
        var state = ready
            ? ReferenceLearningState.ReadyToCommit
            : requiresManualCalibration
                ? ReferenceLearningState.RequiresManualCalibration
                : ReferenceLearningState.AwaitingReview;
        var reason = state == ReferenceLearningState.RequiresManualCalibration
            ? "存在未确认、低置信或需要人工校准的图片框。"
            : null;
        var updated = document.WithCandidates(ordered, state, reason, clock.GetUtcNow());
        var saveFailure = await TrySaveAsync(updated, cancellationToken).ConfigureAwait(false);
        if (saveFailure is not null)
        {
            return saveFailure;
        }
        active[updated.LearningId] = updated;

        return ReferenceLearningResult.Success(updated);
    }

    public async Task<ReferenceLearningResult> ResumeAsync(
        string learningId,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdentifier(learningId))
        {
            return Failure("TPL_LEARN_ID_INVALID", "学习会话编号无效。", "从模板学习入口重新选择会话。");
        }

        if (sessions is null)
        {
            return Failure("TPL_LEARN_SESSION_STORE_UNAVAILABLE", "学习会话存储不可用。", "重新开始一次学习会话。");
        }

        try
        {
            var document = await sessions.GetAsync(learningId, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return Failure("TPL_LEARN_SESSION_NOT_FOUND", "没有找到可恢复的学习会话。", "重新开始一次学习会话。");
            }

            active[document.LearningId] = document;
            return ReferenceLearningResult.Success(document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Failure(
                "TPL_LEARN_SESSION_UNREADABLE",
                "学习会话读取失败。",
                "检查本地存储后重试。",
                retryable: true);
        }
    }

    public async Task<ReferenceLearningResult> ResumeAsync(
        ReferenceLearningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resumed = await ResumeAsync(request.LearningId, cancellationToken).ConfigureAwait(false);
        if (!resumed.IsSuccess)
        {
            return resumed;
        }

        var document = resumed.Document!;
        if (!string.Equals(document.PageId, request.PageId, StringComparison.Ordinal)
            || !string.Equals(
                document.InternalPsdRelativePath,
                request.InternalPsdRelativePath.Replace('\\', '/'),
                StringComparison.Ordinal))
        {
            active.TryRemove(document.LearningId, out _);
            return Failure(
                "TPL_LEARN_RESUME_INPUT_MISMATCH",
                "恢复的学习会话不属于当前模板页面。",
                "返回模板页面后重新开始学习。");
        }

        string ownedPsdPath;
        try
        {
            ownedPsdPath = paths.ResolveOwnedPath(request.InternalPsdRelativePath);
        }
        catch (InvalidOperationException)
        {
            active.TryRemove(document.LearningId, out _);
            return Failure("TPL_PSD_PATH_NOT_OWNED", "学习只接受应用内部 PSD 副本。", "先导入 PSD，再重新开始学习。");
        }

        try
        {
            var file = new FileInfo(ownedPsdPath);
            if (!file.Exists || file.Length != document.PsdLength)
            {
                active.TryRemove(document.LearningId, out _);
                return SourceChangedFailure();
            }

            await using var stream = new FileStream(
                ownedPsdPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var psdHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(NormalizeDigest(psdHash), document.PsdSha256, StringComparison.Ordinal))
            {
                active.TryRemove(document.LearningId, out _);
                return SourceChangedFailure();
            }

            var photos = await photoFingerprints.ScanAsync(
                request.ReferencePhotoDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(photos.FolderDigest, document.ReferenceFolderDigest, StringComparison.Ordinal)
                || photos.Photos.Count != document.ReferencePhotos.Count)
            {
                active.TryRemove(document.LearningId, out _);
                return SourceChangedFailure();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or InvalidOperationException)
        {
            active.TryRemove(document.LearningId, out _);
            return Failure(
                "TPL_LEARN_RESUME_SOURCE_UNREADABLE",
                "无法重新核对学习输入。",
                "恢复参考 PSD 或源照片文件夹后重试。",
                retryable: true);
        }

        active[document.LearningId] = document;
        return resumed;
    }

    public async Task<ReferenceLearningResult> CancelAsync(
        ReferenceLearningDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (document.State == ReferenceLearningState.Committed)
        {
            return Failure("TPL_LEARN_SESSION_CLOSED", "已提交的学习规则不能取消。", "如需修改，请从已提交修订开始新的增量校准。");
        }
        if (!active.TryGetValue(document.LearningId, out var current)
            || current.UpdatedAt != document.UpdatedAt
            || !string.Equals(current.RulesDigest, document.RulesDigest, StringComparison.Ordinal))
        {
            return Failure("TPL_LEARN_SESSION_STALE", "学习会话不是最新版本。", "恢复最新会话后重试。");
        }

        var cancelled = document with
        {
            State = ReferenceLearningState.Cancelled,
            UpdatedAt = clock.GetUtcNow(),
            BlockReason = "用户取消学习。"
        };
        var saveFailure = await TrySaveAsync(cancelled, cancellationToken).ConfigureAwait(false);
        if (saveFailure is not null)
        {
            return saveFailure;
        }
        active[cancelled.LearningId] = cancelled;

        return ReferenceLearningResult.Success(cancelled);
    }

    private static ReferenceLearningDocument BuildDocument(
        ReferenceLearningRequest request,
        PsdInspectionDocument inspection,
        ReferencePhotoInventory photos,
        DateTimeOffset now)
    {
        var candidates = DetectStructuralSlots(inspection)
            .Select(slot => BuildCandidate(request.PageId, inspection, slot, photos))
            .ToArray();
        var blocked = candidates.Length == 0
            ? "PSD 没有可复用的可见智能对象图片框。"
            : candidates.Any(candidate => candidate.IsProduct
                && candidate.MatchState is (ReferenceMatchState.ExternalLink
                    or ReferenceMatchState.Unsupported))
                ? "存在外链或不支持的智能对象结构，需要人工校准。"
                : null;
        var state = blocked is null
            ? ReferenceLearningState.AwaitingReview
            : ReferenceLearningState.RequiresManualCalibration;
        var structureSignature = NormalizeDigest(inspection.StructureSignature);
        return new ReferenceLearningDocument(
            request.LearningId,
            request.PageId,
            request.InternalPsdRelativePath.Replace('\\', '/'),
            NormalizeDigest(inspection.FileSha256),
            inspection.SizeBytes,
            structureSignature,
            inspection.Canvas,
            photos.FolderDigest,
            photos.Photos,
            candidates,
            state,
            ReferenceLearningDigest.Compute(
                NormalizeDigest(inspection.FileSha256),
                structureSignature,
                photos.FolderDigest,
                photos.Photos,
                candidates),
            now,
            now,
            blocked);
    }

    private static ReferenceFrameCandidate BuildCandidate(
        string pageId,
        PsdInspectionDocument inspection,
        StructuralSlot slot,
        ReferencePhotoInventory photos)
    {
        var layer = slot.Target;
        var smartObject = layer.SmartObject!;
        var embeddedHash = smartObject.EmbeddedContentAvailable
            ? smartObject.EmbeddedContentSha256?.ToLowerInvariant()
            : null;
        var matches = embeddedHash is null
            ? []
            : photos.Photos.Where(photo => string.Equals(
                photo.Sha256,
                embeddedHash,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var filenameMatches = photos.Photos.Where(photo =>
            string.Equals(photo.FileName, smartObject.Filename, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        var filenameMatch = filenameMatches.Length == 1 ? filenameMatches[0] : null;
        var matchState = smartObject.Kind switch
        {
            "external" => ReferenceMatchState.ExternalLink,
            "alias" => ReferenceMatchState.Unsupported,
            _ when !smartObject.EmbeddedContentAvailable => ReferenceMatchState.Unsupported,
            _ when matches.Length == 1 => ReferenceMatchState.Exact,
            _ when matches.Length > 1 => ReferenceMatchState.Ambiguous,
            _ when filenameMatch is not null => ReferenceMatchState.Modified,
            _ => ReferenceMatchState.NoMatch
        };
        var selected = matches.Length == 1 ? matches[0] : null;
        // A completed reference PSD may intentionally contain small, product-
        // specific assets such as composition or care-label panels.  Size and
        // filename are not enough evidence to mark a structural candidate as
        // fixed; keep it reviewable and let the user explicitly retain or bind it.
        var structuralReason = slot.SupersededLayers.Count > 0
            ? $"替换栈栈顶；已折叠 {slot.SupersededLayers.Count} 个被覆盖旧图层。"
            : "单个可见智能对象，具有足够的画布面积。";
        var digest = NormalizeDigest(inspection.StructureSignature);
        var semanticLabel = InferSemanticLabel(layer, smartObject, selected);
        var confidence = matchState == ReferenceMatchState.Exact
            ? string.IsNullOrWhiteSpace(semanticLabel) ? 0.98 : 1
            : matchState == ReferenceMatchState.Ambiguous ? 0.5 : 0;
        return new ReferenceFrameCandidate(
            StableFrameId(pageId, layer.LayerId, layer.Path),
            pageId,
            layer.Path,
            layer.LayerId,
            layer.Name,
            digest,
            layer.Visible,
            layer.EffectiveVisible,
            layer.Clipping,
            layer.HasMask,
            layer.HasVectorMask,
            (byte)Math.Clamp(layer.Opacity, byte.MinValue, byte.MaxValue),
            layer.BlendMode,
            smartObject.TransformBox,
            matchState,
            selected?.AssetId,
            selected?.RelativePath,
            embeddedHash,
            semanticLabel,
            confidence,
            DeriveLayout(layer, inspection.Canvas.Width, inspection.Canvas.Height),
            ReferenceReviewDecision.Pending,
            null,
            slot.SupersededLayers.Select(item => new ReferenceSupersededLayerEvidence(
                item.Path,
                item.LayerId,
                item.Visible,
                item.Clipping)).ToArray(),
            layer.ClippingBase?.Path,
            true,
            structuralReason);
    }

    /// <summary>
    /// Infer only an unambiguous usage hint from local, human-authored asset
    /// metadata.  The hint never decides whether a layer is a slot: structural
    /// detection above has already made that decision.  Hash/geometry evidence
    /// still has to close before the hint can participate in batch confirmation.
    /// </summary>
    private static string InferSemanticLabel(
        PsdLayerInspection layer,
        PsdSmartObjectInspection smartObject,
        ReferencePhotoFingerprint? selected)
    {
        var cue = string.Join(
            " ",
            layer.Name,
            smartObject.Filename,
            selected?.FileName ?? "")
            .ToLowerInvariant();
        if (ContainsAny(cue, "care", "label", "wash", "composition", "成分", "水洗", "标签", "标"))
        {
            return "成分/水洗唛信息图";
        }
        if (ContainsAny(cue, "collar", "neck", "领口", "领"))
        {
            return "领口细节图";
        }
        if (ContainsAny(cue, "sleeve", "cuff", "袖口", "袖"))
        {
            return "袖口细节图";
        }
        if (ContainsAny(cue, "fabric", "textile", "material", "cloth", "面料", "面布"))
        {
            return "面料细节图";
        }
        if (ContainsAny(cue, "back", "rear", "背面", "后身"))
        {
            return "完整背面图";
        }
        if (ContainsAny(cue, "front", "main", "hero", "正面", "前身"))
        {
            return "完整正面图";
        }

        return "";
    }

    private static bool ContainsAny(string value, params string[] cues) =>
        cues.Any(value.Contains);

    private sealed record StructuralSlot(
        PsdLayerInspection Target,
        IReadOnlyList<PsdLayerInspection> SupersededLayers);

    /// <summary>
    /// Mirrors the PSD structural detector: only visible, meaningful-area
    /// smart objects are considered, consecutive overlapping siblings form a
    /// replacement stack, and the topmost layer is the sole candidate.
    /// </summary>
    private static StructuralSlot[] DetectStructuralSlots(
        PsdInspectionDocument inspection)
    {
        var indexed = inspection.Layers
            .Select((layer, order) => new IndexedLayer(layer, order))
            .ToArray();
        var byParent = indexed
            .GroupBy(item => ParentKey(item.Layer.Path), StringComparer.Ordinal)
            .ToArray();
        var selected = new List<(StructuralSlot Slot, int Order)>();
        foreach (var siblings in byParent)
        {
            var ordered = siblings
                .OrderBy(item => LastPathSegment(item.Layer.Path))
                .ToArray();
            var run = new List<IndexedLayer>();
            void Flush()
            {
                if (run.Count == 0)
                {
                    return;
                }

                var target = run[^1];
                selected.Add((
                    new StructuralSlot(
                        target.Layer,
                        run.Take(run.Count - 1).Select(item => item.Layer).ToArray()),
                    target.Order));
                run.Clear();
            }

            foreach (var item in ordered)
            {
                if (!IsEligible(item.Layer, inspection.Canvas))
                {
                    Flush();
                    continue;
                }

                if (run.Count == 0)
                {
                    run.Add(item);
                    continue;
                }

                var previous = run[^1];
                var consecutive = LastPathSegment(previous.Layer.Path) + 1
                    == LastPathSegment(item.Layer.Path);
                var overlapping = OverlapRatio(
                    previous.Layer.Bounds,
                    item.Layer.Bounds,
                    inspection.Canvas) >= 0.25;
                if (consecutive && overlapping)
                {
                    run.Add(item);
                }
                else
                {
                    Flush();
                    run.Add(item);
                }
            }
            Flush();
        }

        return selected
            .OrderBy(item => item.Order)
            .Select(item => item.Slot)
            .ToArray();
    }

    private sealed record IndexedLayer(PsdLayerInspection Layer, int Order);

    private static bool IsEligible(PsdLayerInspection layer, PsdCanvasInspection canvas)
    {
        if (layer.SmartObject is null
            || !layer.Visible
            || !layer.EffectiveVisible
            || layer.Opacity <= 0)
        {
            return false;
        }

        var width = Math.Max(0, Math.Min(layer.Bounds.Right, canvas.Width)
            - Math.Max(layer.Bounds.Left, 0));
        var height = Math.Max(0, Math.Min(layer.Bounds.Bottom, canvas.Height)
            - Math.Max(layer.Bounds.Top, 0));
        var ratio = (double)width * height / Math.Max(1L, (long)canvas.Width * canvas.Height);
        return ratio >= 0.01;
    }

    private static double OverlapRatio(
        PsdLayerBounds first,
        PsdLayerBounds second,
        PsdCanvasInspection canvas)
    {
        var firstArea = Area(first, canvas);
        var secondArea = Area(second, canvas);
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        var intersection = Math.Max(0, Math.Min(right, canvas.Width) - Math.Max(left, 0))
            * Math.Max(0, Math.Min(bottom, canvas.Height) - Math.Max(top, 0));
        var denominator = Math.Min(firstArea, secondArea);
        return denominator > 0 ? intersection / denominator : 0;
    }

    private static double Area(PsdLayerBounds bounds, PsdCanvasInspection canvas)
    {
        var width = Math.Max(0, Math.Min(bounds.Right, canvas.Width) - Math.Max(bounds.Left, 0));
        var height = Math.Max(0, Math.Min(bounds.Bottom, canvas.Height) - Math.Max(bounds.Top, 0));
        return (double)width * height;
    }

    private static string ParentKey(IReadOnlyList<int> path) =>
        path.Count <= 1 ? "" : string.Join('/', path.Take(path.Count - 1));

    private static int LastPathSegment(IReadOnlyList<int> path) =>
        path.Count == 0 ? -1 : path[^1];

    private static string StableFrameId(
        string pageId,
        int layerId,
        IReadOnlyList<int> layerPath)
    {
        var identity = string.Join(
            "\n",
            pageId,
            layerId.ToString(CultureInfo.InvariantCulture),
            string.Join('.', layerPath));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return $"learn_frame_{digest[..24]}";
    }

    private static LayoutIntent DeriveLayout(
        PsdLayerInspection layer,
        int canvasWidth,
        int canvasHeight)
    {
        var box = layer.SmartObject!.TransformBox;
        var left = box.Count == 8 ? box.Where((_, index) => index % 2 == 0).Min() : layer.Bounds.Left;
        var top = box.Count == 8 ? box.Where((_, index) => index % 2 == 1).Min() : layer.Bounds.Top;
        var right = box.Count == 8 ? box.Where((_, index) => index % 2 == 0).Max() : layer.Bounds.Right;
        var bottom = box.Count == 8 ? box.Where((_, index) => index % 2 == 1).Max() : layer.Bounds.Bottom;
        var frame = layer.ClippingBase?.Bounds ?? layer.Bounds;
        var safeLeft = Math.Clamp(frame.Left, 0, Math.Max(0, canvasWidth - 1));
        var safeTop = Math.Clamp(frame.Top, 0, Math.Max(0, canvasHeight - 1));
        var safeRight = Math.Clamp(frame.Right, safeLeft + 1, canvasWidth);
        var safeBottom = Math.Clamp(frame.Bottom, safeTop + 1, canvasHeight);
        var frameWidth = Math.Max(1, safeRight - safeLeft);
        var frameHeight = Math.Max(1, safeBottom - safeTop);
        var transformedWidth = Math.Max(1, right - left);
        var transformedHeight = Math.Max(1, bottom - top);
        var scale = Math.Clamp(
            Math.Max(transformedWidth / frameWidth, transformedHeight / frameHeight),
            0.01,
            20);
        return LayoutIntent.Create(
            "subject_cover",
            scale,
            left - safeLeft,
            top - safeTop,
            TemplateRect.Create(safeLeft, safeTop, safeRight, safeBottom));
    }

    private static LayoutIntent BuildReviewedLayout(LayoutIntent current, ReferenceFrameReview review)
    {
        if (review.Scale is null && review.OffsetX is null && review.OffsetY is null)
        {
            return current;
        }

        return LayoutIntent.Create(
            current.Intent,
            review.Scale ?? current.Scale,
            review.OffsetX ?? current.OffsetX,
            review.OffsetY ?? current.OffsetY,
            current.SafeBounds);
    }

    private async Task<ReferenceLearningResult?> TrySaveAsync(
        ReferenceLearningDocument document,
        CancellationToken cancellationToken)
    {
        if (sessions is null)
        {
            return null;
        }

        try
        {
            await sessions.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return Failure(
                "TPL_LEARN_SESSION_WRITE_FAILED",
                "学习复核草稿无法安全保存。",
                "检查本地存储后重试；旧模板修订未改变。",
                retryable: true);
        }
    }

    private static ReferenceLearningResult SourceChangedFailure() => Failure(
        "TPL_LEARN_SOURCE_CHANGED",
        "参考 PSD 或源照片集合已发生变化。",
        "确认当前文件后重新分析；不会沿用旧会话坐标或匹配结果。");

    private static string NormalizeDigest(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? $"sha256:{normalized[7..].ToLowerInvariant()}"
            : $"sha256:{normalized.ToLowerInvariant()}";
    }

    private static string? NormalizeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static ReferenceLearningResult Failure(
        string code,
        string message,
        string userAction,
        bool retryable = false) => ReferenceLearningResult.Failure(
        new ReferenceLearningError(code, message, userAction, retryable));
}
