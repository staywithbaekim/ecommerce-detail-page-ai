using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Desktop.Application.Files;
using Desktop.Application.Psd;
using Desktop.Contracts.Ai;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed class ReferenceLearningService(
    IPsdInspectionPort inspection,
    IReferencePhotoFingerprintPort photoFingerprints,
    IPathPolicy paths,
    IReferenceLearningSessionStore? sessions = null,
    TimeProvider? timeProvider = null,
    IReferenceLearningAiService? semanticAi = null) : IReferenceLearningService
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
                    // The import flow already created the owned preview. During
                    // WPF review that PNG may be held by an ImageSource, and
                    // asking the worker to overwrite it would be reported by
                    // the worker as the misleading "damaged PSD" error.
                    PreviewRelativePath: null),
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
        var current = await ResolveCurrentAsync(document, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure("TPL_LEARN_SESSION_STALE", "学习会话不是最新版本。", "恢复最新会话后重新提交复核。");
        }
        document = current;

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
        if (semanticAi is not null)
        {
            var working = document.WithCandidates(ordered, ReferenceLearningState.AwaitingReview, null, clock.GetUtcNow());
            foreach (var review in reviews.Where(review => !string.IsNullOrWhiteSpace(review.SemanticLabel)))
            {
                var before = document.Candidates.Single(candidate => string.Equals(candidate.FrameId, review.FrameId, StringComparison.Ordinal));
                if (!string.Equals(before.SemanticLabel, review.SemanticLabel!.Trim(), StringComparison.Ordinal))
                {
                    working = await semanticAi.ApplyUserOverrideAsync(
                        working,
                        review.FrameId,
                        review.SemanticLabel!,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            ordered = working.Candidates.ToArray();
        }
        var allDecided = ordered.All(candidate => candidate.ReviewDecision != ReferenceReviewDecision.Pending);
        var ready = allDecided && ordered
            .Where(candidate => candidate.ReviewDecision != ReferenceReviewDecision.Rejected)
            .All(candidate => candidate.IsHighConfidence);
        var requiresManualCalibration = ordered.Any(candidate =>
            candidate.ReviewDecision == ReferenceReviewDecision.ManualCalibration);
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

        ReferencePhotoInventory photos;
        try
        {
            var file = new FileInfo(ownedPsdPath);
            if (!file.Exists)
            {
                active.TryRemove(document.LearningId, out _);
                return SourceMissingFailure();
            }

            if (file.Length != document.PsdLength)
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

            photos = await photoFingerprints.ScanAsync(
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

        // Older local sessions were created before semantic usage inference was
        // wired into the learning result.  Rehydrate those sessions with the
        // same deterministic evidence rules used for a fresh analysis so the
        // UI does not keep showing an empty "future selection rule" forever.
        var enriched = EnrichSemanticHints(document, request.PageName, photos);
        if (!ReferenceCandidatesEqual(document.Candidates, enriched.Candidates))
        {
            var saveFailure = await TrySaveAsync(enriched, cancellationToken).ConfigureAwait(false);
            if (saveFailure is not null)
            {
                return saveFailure;
            }

            document = enriched;
        }

        active[document.LearningId] = document;
        return ReferenceLearningResult.Success(document);
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
        var current = await ResolveCurrentAsync(document, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure("TPL_LEARN_SESSION_STALE", "学习会话不是最新版本。", "恢复最新会话后重试。");
        }
        document = current;

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
        var candidates = DetectStructuralSlots(inspection, photos)
            .Select(slot => BuildCandidate(
                request.PageId,
                request.PageName,
                inspection,
                slot,
                photos))
            .ToArray();
        var blocked = candidates.Length == 0
            ? inspection.Layers.Any(layer => IsStructuralLayer(layer, inspection.Canvas))
                ? "可见智能对象未同时闭合源照片内容和最终显示贡献，需要人工校准。"
                : "PSD 没有可复用且对最终页面有显示贡献的智能对象图片框。"
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
        string? pageName,
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
        // A completed reference PSD may intentionally contain small product-
        // specific assets such as composition or care-label panels. The exact
        // source-payload match and final composite contribution decide whether
        // it is a replaceable frame; visual text content and filename do not.
        var structuralReason = slot.SupersededLayers.Count > 0
            ? $"有效替换栈栈顶；已折叠 {slot.SupersededLayers.Count} 个被覆盖旧图层，并验证最终页面像素贡献。"
            : "源素材内容闭合的可见智能对象，并验证最终页面像素贡献。";
        var digest = NormalizeDigest(inspection.StructureSignature);
        var semantic = InferSemanticLabel(
            pageName,
            layer,
            smartObject,
            selected,
            DeriveLayout(layer, inspection.Canvas.Width, inspection.Canvas.Height),
            inspection.Canvas);
        var confidence = matchState == ReferenceMatchState.Exact
            ? semantic.Confidence
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
            semantic.Label,
            confidence,
            semantic.Layout,
            ReferenceReviewDecision.Pending,
            null,
            slot.SupersededLayers.Select(item => new ReferenceSupersededLayerEvidence(
                item.Path,
                item.LayerId,
                item.Visible,
                item.Clipping)).ToArray(),
            layer.ClippingBase?.Path,
            true,
            structuralReason,
            layer.PixelEvidence);
    }

    /// <summary>
    /// Infer only an unambiguous usage hint from local, human-authored asset
    /// metadata.  The hint never decides whether a layer is a slot: structural
    /// detection above has already made that decision.  Hash/geometry evidence
    /// still has to close before the hint can participate in batch confirmation.
    /// </summary>
    private sealed record SemanticInference(
        string Label,
        double Confidence,
        LayoutIntent Layout);

    private static SemanticInference InferSemanticLabel(
        string? pageName,
        PsdLayerInspection layer,
        PsdSmartObjectInspection smartObject,
        ReferencePhotoFingerprint? selected,
        LayoutIntent layout,
        PsdCanvasInspection canvas)
    {
        var cue = string.Join(
            " ",
            pageName ?? "",
            layer.Name,
            smartObject.Filename,
            selected?.FileName ?? "")
            .ToLowerInvariant();
        if (ContainsAny(cue, "care", "label", "wash", "composition", "成分", "水洗", "标签", "标"))
        {
            return new("成分/水洗唛信息图", 0.98, layout);
        }
        if (ContainsAny(cue, "collar", "neck", "领口", "领"))
        {
            return new("领口细节图", 1, layout);
        }
        if (ContainsAny(cue, "sleeve", "cuff", "袖口", "袖"))
        {
            return new("袖口细节图", 1, layout);
        }
        if (ContainsAny(cue, "fabric", "textile", "material", "cloth", "面料", "面布"))
        {
            return new("面料细节图", 1, layout);
        }
        if (ContainsAny(cue, "back", "rear", "背面", "后身"))
        {
            return new("完整背面图", 1, layout);
        }
        if (ContainsAny(cue, "front", "main", "hero", "正面", "前身"))
        {
            return new("完整正面图", 1, layout);
        }

        // A small raster that was actually matched into a visible smart
        // object is usually a composition/care graphic rather than a product
        // photo.  This remains reviewable (and is intentionally excluded from
        // one-click confirmation) because the target款 may have different
        // material percentages.
        if (selected is { Width: <= 500, Height: <= 500 })
        {
            return new("成分/水洗唛信息图", 0.96, layout);
        }

        var pageCue = (pageName ?? "").ToLowerInvariant();
        if (ContainsAny(pageCue, "面料", "材质", "布料", "fabric", "textile", "material"))
        {
            return new("面料细节图", 0.99, layout);
        }

        // A camera-generated filename and a generic page/layout position do
        // not establish a reusable category. Do not turn those weak cues into
        // the old "商品展示图（请确认）" sentence: the UI must show a
        // clearly unconfirmed placeholder until AI or the user supplies the
        // category.
        return new(ReferenceSelectionRequirementPolicy.UnconfirmedLabel, 0, layout);
    }

    private static ReferenceLearningDocument EnrichSemanticHints(
        ReferenceLearningDocument document,
        string? pageName,
        ReferencePhotoInventory photos)
    {
        var invalidAiSemanticFound = false;
        var candidates = document.Candidates
            .Select(candidate =>
            {
                var metadata = candidate.SemanticMetadata;
                var isCurrentAiSemantic = metadata?.Source == ReferenceSemanticSource.AiV2
                    && string.Equals(
                        metadata.PromptContractVersion,
                        ReferenceLearningAiProtocol.SemanticPromptVersion,
                        StringComparison.Ordinal)
                    && ReferenceSelectionRequirementPolicy.IsValid(candidate.SemanticLabel);
                var isUserOverride = metadata?.Source == ReferenceSemanticSource.UserOverride
                    && ReferenceSelectionRequirementPolicy.IsValid(candidate.SemanticLabel);
                if (isCurrentAiSemantic || isUserOverride)
                {
                    return candidate;
                }

                // A persisted v2/v3 narrative is not a harmless display hint:
                // it can reach the batch projector if it is left attached to
                // an old session. Clear it and make the next action explicit.
                if (metadata?.Source is ReferenceSemanticSource.AiV2 or ReferenceSemanticSource.AiUnavailable)
                {
                    invalidAiSemanticFound = true;
                    return candidate with
                    {
                        SemanticLabel = ReferenceSelectionRequirementPolicy.UnconfirmedLabel,
                        Confidence = 0,
                        ReviewDecision = ReferenceReviewDecision.Pending,
                        SemanticMetadata = new ReferenceSemanticMetadata(
                            ReferenceSemanticSource.AiUnavailable,
                            ReferenceLearningAiProtocol.RequestSchemaVersion,
                            ReferenceLearningAiProtocol.ResultSchemaVersion,
                            ReferenceLearningAiService.ModelConfigVersion,
                            ReferenceLearningAiProtocol.SemanticPromptVersion,
                            metadata.CacheKey,
                            [],
                            [])
                    };
                }

                if (!string.IsNullOrWhiteSpace(candidate.SemanticLabel)
                    && ReferenceSelectionRequirementPolicy.IsValid(candidate.SemanticLabel))
                {
                    return candidate;
                }

                var selected = candidate.ReferenceAssetId is null
                    ? null
                    : photos.Photos.FirstOrDefault(photo => string.Equals(
                        photo.AssetId,
                        candidate.ReferenceAssetId,
                        StringComparison.Ordinal));
                var smartObject = new PsdSmartObjectInspection(
                    candidate.LayerId.ToString(CultureInfo.InvariantCulture),
                    candidate.LayerName,
                    "data",
                    null,
                    "unknown",
                    selected?.Length ?? 0,
                    false,
                    candidate.TransformBox);
                var layer = new PsdLayerInspection(
                    candidate.LayerPath,
                    candidate.LayerId,
                    candidate.LayerName,
                    "smartobject",
                    false,
                    candidate.Visible,
                    candidate.EffectiveVisible,
                    candidate.Clipping,
                    candidate.Opacity,
                    candidate.Opacity,
                    candidate.BlendMode,
                    new PsdLayerBounds(
                        (int)Math.Round(candidate.LayoutIntent.SafeBounds.Left),
                        (int)Math.Round(candidate.LayoutIntent.SafeBounds.Top),
                        (int)Math.Round(candidate.LayoutIntent.SafeBounds.Right),
                        (int)Math.Round(candidate.LayoutIntent.SafeBounds.Bottom)),
                    candidate.UserMask,
                    candidate.VectorMask,
                    null,
                    smartObject);
                var inference = InferSemanticLabel(
                    pageName,
                    layer,
                    smartObject,
                    selected,
                    candidate.LayoutIntent,
                    document.Canvas);
                return candidate with
                {
                    SemanticLabel = inference.Label,
                    Confidence = document.Candidates.Any(item => item.FrameId == candidate.FrameId)
                        && candidate.MatchState == ReferenceMatchState.Exact
                        ? inference.Confidence
                        : candidate.Confidence
                };
            })
            .ToArray();

        return document.WithCandidates(
            candidates,
            document.State,
            invalidAiSemanticFound
                ? "旧版 AI 语义规则已失效，请重新分析或手工填写图片类别。"
                : document.BlockReason,
            DateTimeOffset.UtcNow);
    }

    private static bool ReferenceCandidatesEqual(
        IReadOnlyList<ReferenceFrameCandidate> left,
        IReadOnlyList<ReferenceFrameCandidate> right) => left.Count == right.Count
        && left.Zip(right).All(pair =>
            string.Equals(pair.First.FrameId, pair.Second.FrameId, StringComparison.Ordinal)
            && string.Equals(pair.First.SemanticLabel, pair.Second.SemanticLabel, StringComparison.Ordinal)
            && Math.Abs(pair.First.Confidence - pair.Second.Confidence) < 0.000001
            && pair.First.ReviewDecision == pair.Second.ReviewDecision
            && pair.First.SemanticMetadata?.Source == pair.Second.SemanticMetadata?.Source
            && string.Equals(
                pair.First.SemanticMetadata?.PromptContractVersion,
                pair.Second.SemanticMetadata?.PromptContractVersion,
                StringComparison.Ordinal));

    private static bool ContainsAny(string value, params string[] cues) =>
        cues.Any(value.Contains);

    private sealed record StructuralSlot(
        PsdLayerInspection Target,
        IReadOnlyList<PsdLayerInspection> SupersededLayers);

    /// <summary>
    /// Registers only source-matched smart objects with rendered pixels and a
    /// measurable contribution to the final page. Consecutive siblings form a
    /// replacement stack when they share a clipping window or broad placed
    /// overlap. A second contributing match outside an explicit shared clipping
    /// window starts another frame, so partially overlapping independent frames
    /// are not merged.
    /// </summary>
    private static StructuralSlot[] DetectStructuralSlots(
        PsdInspectionDocument inspection,
        ReferencePhotoInventory photos)
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

                var targetIndex = run.FindLastIndex(item =>
                    HasClosedReferenceContent(item.Layer, photos) &&
                    HasFinalCompositeContribution(item.Layer));
                if (targetIndex < 0)
                {
                    run.Clear();
                    return;
                }

                var target = run[targetIndex];
                selected.Add((
                    new StructuralSlot(
                        target.Layer,
                        run.Take(targetIndex).Select(item => item.Layer).ToArray()),
                    target.Order));
                run.Clear();
            }

            foreach (var item in ordered)
            {
                if (!IsStructuralLayer(item.Layer, inspection.Canvas))
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
                var sameWindow = IsSameReplacementWindow(
                    previous.Layer,
                    item.Layer,
                    inspection.Canvas);
                var startsAnotherEffectiveFrame =
                    HasClosedReferenceContent(item.Layer, photos) &&
                    HasFinalCompositeContribution(item.Layer) &&
                    run.Any(existing =>
                        HasClosedReferenceContent(existing.Layer, photos) &&
                        HasFinalCompositeContribution(existing.Layer));
                var sharesExplicitWindow = SharesClippingWindow(
                    previous.Layer,
                    item.Layer);
                if (consecutive && sameWindow &&
                    (!startsAnotherEffectiveFrame || sharesExplicitWindow))
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

    private static bool IsStructuralLayer(PsdLayerInspection layer, PsdCanvasInspection canvas)
    {
        if (layer.SmartObject is null
            || !layer.Visible
            || !layer.EffectiveVisible
            || layer.Opacity <= 0
            || !layer.PixelEvidence.Available
            || layer.PixelEvidence.RenderedPixelCount <= 0)
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

    private static bool HasClosedReferenceContent(
        PsdLayerInspection layer,
        ReferencePhotoInventory photos)
    {
        var smartObject = layer.SmartObject;
        return smartObject is
        {
            Kind: "data",
            EmbeddedContentAvailable: true,
            EmbeddedContentSha256: not null
        }
            && photos.Photos.Any(photo => string.Equals(
                photo.Sha256,
                smartObject.EmbeddedContentSha256,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasFinalCompositeContribution(PsdLayerInspection layer) =>
        layer.PixelEvidence.Available
        && layer.PixelEvidence.CompositeContributionPixelCount > 0
        && layer.PixelEvidence.CompositeContributionBounds is not null;

    private static bool IsSameReplacementWindow(
        PsdLayerInspection first,
        PsdLayerInspection second,
        PsdCanvasInspection canvas) => SharesClippingWindow(first, second)
        || OverlapRatio(first.Bounds, second.Bounds, canvas) >= 0.25;

    private static bool SharesClippingWindow(
        PsdLayerInspection first,
        PsdLayerInspection second) =>
        first.ClippingBase is not null
        && second.ClippingBase is not null
        && first.ClippingBase.Path.SequenceEqual(second.ClippingBase.Path);

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
        var intent = InferLayoutIntent(layer, safeLeft, safeTop, safeRight, safeBottom, canvasWidth, canvasHeight);
        return LayoutIntent.Create(
            intent,
            scale,
            left - safeLeft,
            top - safeTop,
            TemplateRect.Create(safeLeft, safeTop, safeRight, safeBottom));
    }

    /// <summary>
    /// Infer layout intent based on frame position, size, and layer context.
    /// This helps AI generate more specific semantic labels.
    /// </summary>
    private static string InferLayoutIntent(
        PsdLayerInspection layer,
        int frameLeft,
        int frameTop,
        int frameRight,
        int frameBottom,
        int canvasWidth,
        int canvasHeight)
    {
        var frameWidth = frameRight - frameLeft;
        var frameHeight = frameBottom - frameTop;
        var frameArea = (double)frameWidth * frameHeight;
        var canvasArea = (double)canvasWidth * canvasHeight;
        var areaRatio = frameArea / canvasArea;
        var aspectRatio = (double)frameWidth / frameHeight;

        // Calculate frame center
        var centerX = (frameLeft + frameRight) / 2.0;
        var centerY = (frameTop + frameBottom) / 2.0;
        var relativeCenterX = centerX / canvasWidth;
        var relativeCenterY = centerY / canvasHeight;

        // Check layer name hints
        var nameLower = layer.Name.ToLowerInvariant();
        var filenameLower = (layer.SmartObject?.Filename ?? "").ToLowerInvariant();
        var combinedHints = nameLower + " " + filenameLower;

        // Detail/crop detection: small frame or contains detail keywords
        if (areaRatio < 0.15 || ContainsAny(combinedHints, "detail", "细节", "局部", "特写", "close", "crop", "领", "袖", "collar", "sleeve", "fabric", "面料"))
        {
            return "detail_crop";
        }

        // Hero/main product: large centered frame
        if (areaRatio > 0.5 && relativeCenterX is > 0.3 and < 0.7 && relativeCenterY is > 0.3 and < 0.7)
        {
            return "hero_centered";
        }

        // Full product display: large frame covering most canvas
        if (areaRatio > 0.6)
        {
            return "full_product";
        }

        // Positioned frames in grid layouts
        if (areaRatio is > 0.15 and < 0.5)
        {
            // Top-left quadrant
            if (relativeCenterX < 0.5 && relativeCenterY < 0.5)
                return "grid_top_left";

            // Top-right quadrant
            if (relativeCenterX >= 0.5 && relativeCenterY < 0.5)
                return "grid_top_right";

            // Bottom-left quadrant
            if (relativeCenterX < 0.5 && relativeCenterY >= 0.5)
                return "grid_bottom_left";

            // Bottom-right quadrant
            if (relativeCenterX >= 0.5 && relativeCenterY >= 0.5)
                return "grid_bottom_right";
        }

        // Default fallback
        return "subject_cover";
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

    private async Task<ReferenceLearningDocument?> ResolveCurrentAsync(
        ReferenceLearningDocument supplied,
        CancellationToken cancellationToken)
    {
        if (active.TryGetValue(supplied.LearningId, out var current)
            && current.UpdatedAt == supplied.UpdatedAt
            && string.Equals(current.RulesDigest, supplied.RulesDigest, StringComparison.Ordinal))
            return current;
        if (sessions is null)
            return null;
        try
        {
            var persisted = await sessions.GetAsync(supplied.LearningId, cancellationToken).ConfigureAwait(false);
            if (persisted is null
                || persisted.UpdatedAt != supplied.UpdatedAt
                || !string.Equals(persisted.RulesDigest, supplied.RulesDigest, StringComparison.Ordinal))
                return null;
            active[supplied.LearningId] = persisted;
            return persisted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static ReferenceLearningResult SourceChangedFailure() => Failure(
        "TPL_LEARN_SOURCE_CHANGED",
        "参考 PSD 或源照片集合已发生变化。",
        "确认当前文件后重新分析；不会沿用旧会话坐标或匹配结果。");

    private static ReferenceLearningResult SourceMissingFailure() => Failure(
        "TPL_LEARN_SOURCE_MISSING",
        "当前学习会话引用的应用内部 PSD 副本已不存在。",
        "重新导入对应 PSD 后重新开始学习；不会修改用户原始 PSD。");

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
