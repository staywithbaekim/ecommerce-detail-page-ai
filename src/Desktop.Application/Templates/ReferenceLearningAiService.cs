using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

public sealed record ReferenceLearningAiCacheEntry(
    string CacheKey,
    string Operation,
    string RequestSchemaVersion,
    string ResultSchemaVersion,
    string ModelConfigVersion,
    string PromptContractVersion,
    string CompressionProfileVersion,
    string ReferenceSetDigest,
    ReferenceLearningAiResultV2 Result,
    AiProviderUsage? Usage,
    DateTimeOffset CreatedAt,
    bool IsReusable = true);

public interface IReferenceLearningAiCacheStore
{
    Task<ReferenceLearningAiCacheEntry?> FindAsync(string cacheKey, CancellationToken cancellationToken);
    Task SaveAsync(ReferenceLearningAiCacheEntry entry, CancellationToken cancellationToken);
    Task<bool> InvalidateAsync(string cacheKey, CancellationToken cancellationToken);
    Task<ReferenceSemanticOverride?> FindOverrideAsync(string learningId, string frameId, CancellationToken cancellationToken);
    Task SaveOverrideAsync(ReferenceSemanticOverride value, CancellationToken cancellationToken);
}

public sealed record ReferenceSemanticOverride(
    string LearningId,
    string FrameId,
    string SelectionRequirement,
    DateTimeOffset UpdatedAt);

public sealed record ReferenceLearningAiFailure(
    string Code,
    string UserAction,
    bool Retryable,
    AiProviderOutcome Outcome);

public sealed record ReferenceSemanticGenerationCommand(
    ReferenceLearningDocument Document,
    string ReferencePhotoDirectory,
    string PageContext);

public sealed record ReferenceSemanticGenerationResult(
    ReferenceLearningDocument Document,
    string? CacheKey,
    bool CacheHit,
    ReferenceLearningAiFailure? Failure)
{
    public bool IsSuccess => Failure is null;
}

public sealed record ReferenceSemanticBatchGenerationResult(
    IReadOnlyList<ReferenceLearningDocument> Documents,
    string? CacheKey,
    bool CacheHit,
    ReferenceLearningAiFailure? Failure)
{
    public bool IsSuccess => Failure is null;
}

public interface IReferenceLearningAiService
{
    Task<ReferenceSemanticGenerationResult> GenerateSelectionRequirementsAsync(
        ReferenceSemanticGenerationCommand command,
        CancellationToken cancellationToken = default);

    Task<ReferenceSemanticBatchGenerationResult> GenerateSelectionRequirementsAsync(
        IReadOnlyList<ReferenceSemanticGenerationCommand> commands,
        CancellationToken cancellationToken = default);

    Task<ReferenceLearningDocument> ApplyUserOverrideAsync(
        ReferenceLearningDocument document,
        string frameId,
        string selectionRequirement,
        CancellationToken cancellationToken = default);
}

public sealed class ReferenceLearningAiService(
    IAiServicePort ai,
    IAiUploadAdmissionPort admission,
    IAiAnalysisCopyPreparer copyPreparer,
    IAiAnalysisCopyLeaseManager leases,
    IReferenceLearningAiCacheStore cache,
    IReferenceLearningSessionStore sessions,
    IAiUsageRecorder usageRecorder,
    IAiAnalysisStore ledger,
    TimeProvider? timeProvider = null) : IReferenceLearningAiService
{
    public const string ModelConfigVersion = "doubao-seed-2.1-pro-260628";
    // Reference semantic generation uses the production bounded WIC profile.
    // The v1 profile can preserve opaque PNGs and make a whole reference set
    // exceed the final 16 MiB JSON request-body gate before HTTP.  Keep this
    // version in the cache key and request contract so old v1 copies/results
    // are never silently reused.
    internal const string CompressionProfileVersion = AiAnalysisCopyProfiles.WicBoundedV4;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<ReferenceSemanticGenerationResult> GenerateSelectionRequirementsAsync(
        ReferenceSemanticGenerationCommand command,
        CancellationToken cancellationToken = default) =>
        GenerateSelectionRequirementsCoreAsync(command, persistSession: true, cancellationToken);

    public async Task<ReferenceSemanticBatchGenerationResult> GenerateSelectionRequirementsAsync(
        IReadOnlyList<ReferenceSemanticGenerationCommand> commands,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            return new([], null, false, new ReferenceLearningAiFailure(
                "TPL_LEARN_AI_DOCUMENTS_MISSING", "没有可分析的模板页面。", false, AiProviderOutcome.TerminalError));
        var first = commands[0];
        if (commands.Any(command => !string.Equals(command.ReferencePhotoDirectory, first.ReferencePhotoDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.Document.ReferenceFolderDigest, first.Document.ReferenceFolderDigest, StringComparison.Ordinal)
            || command.Document.ReferencePhotos.Count != first.Document.ReferencePhotos.Count))
        {
            return new(commands.Select(command => command.Document).ToArray(), null, false,
                new ReferenceLearningAiFailure("TPL_LEARN_REFERENCE_SET_MISMATCH", "全部页面必须使用同一参考款照片文件夹。", false, AiProviderOutcome.TerminalError));
        }
        var combinedCandidates = commands.SelectMany(command => command.Document.Candidates).ToArray();
        if (combinedCandidates.Length > 200
            || combinedCandidates.Select(candidate => candidate.FrameId).Distinct(StringComparer.Ordinal).Count() != combinedCandidates.Length)
        {
            return new(commands.Select(command => command.Document).ToArray(), null, false,
                new ReferenceLearningAiFailure("TPL_LEARN_AI_FRAME_SET_INVALID", "图片框集合超过单次安全上限或包含重复标识，请修复模板组。", false, AiProviderOutcome.TerminalError));
        }
        var synthetic = first.Document with
        {
            Candidates = combinedCandidates,
            RulesDigest = ReferenceLearningDigest.Compute(
                first.Document.PsdSha256,
                first.Document.StructureSignature,
                first.Document.ReferenceFolderDigest,
                first.Document.ReferencePhotos,
                combinedCandidates)
        };
        var context = string.Join("；", commands.Select(command => command.Document.PageId + "=" + SafeContext(command.PageContext)));
        var combined = await GenerateSelectionRequirementsCoreAsync(
            new ReferenceSemanticGenerationCommand(synthetic, first.ReferencePhotoDirectory, context),
            persistSession: false,
            cancellationToken).ConfigureAwait(false);
        var byFrame = combined.Document.Candidates.ToDictionary(candidate => candidate.FrameId, StringComparer.Ordinal);
        var documents = new List<ReferenceLearningDocument>(commands.Count);
        foreach (var command in commands)
        {
            var candidates = command.Document.Candidates.Select(candidate => byFrame[candidate.FrameId]).ToArray();
            var state = candidates.Any(candidate => candidate.SemanticMetadata?.Source == ReferenceSemanticSource.AiUnavailable)
                ? ReferenceLearningState.AwaitingReview
                : command.Document.State;
            var document = command.Document.WithCandidates(candidates, state, combined.Document.BlockReason, clock.GetUtcNow());
            // Each page owns its overrides. The combined request uses the first
            // learning id only for correlation, never as the owner of other pages.
            document = await ApplyPersistedOverridesAsync(document, cancellationToken).ConfigureAwait(false);
            await sessions.SaveAsync(document, cancellationToken).ConfigureAwait(false);
            documents.Add(document);
        }
        return new ReferenceSemanticBatchGenerationResult(documents, combined.CacheKey, combined.CacheHit, combined.Failure);
    }

    private async Task<ReferenceSemanticGenerationResult> GenerateSelectionRequirementsCoreAsync(
        ReferenceSemanticGenerationCommand command,
        bool persistSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Document);
        var document = command.Document;
        var frames = document.Candidates.Where(candidate => candidate.IsEvidenceClosed).ToArray();
        if (frames.Length == 0)
        {
            return Failure(document, "TPL_LEARN_AI_LOCAL_EVIDENCE_INCOMPLETE", "先修正图片框和参考照片对应关系。", false, AiProviderOutcome.TerminalError);
        }
        var referencedAssetIds = frames
            .Select(frame => frame.ReferenceAssetId!)
            .ToHashSet(StringComparer.Ordinal);
        var referencedPhotos = document.ReferencePhotos
            .Where(photo => referencedAssetIds.Contains(photo.AssetId))
            .ToArray();
        if (referencedPhotos.Length != referencedAssetIds.Count)
        {
            return Failure(document, "TPL_LEARN_AI_REFERENCE_SET_OPEN", "有图片框引用了参考文件夹之外的照片，请重新分析。", false, AiProviderOutcome.TerminalError);
        }
        if (!ReferenceBindingsAreClosed(frames, document.ReferencePhotos, referencedPhotos))
        {
            // Do not let a stale frame/asset pair reach either the copy
            // preparer or the provider.  A source folder is allowed to hold
            // extra photos, but every uploaded photo must be referenced by an
            // evidence-closed PSD frame and every exact frame must point back
            // to the same source hash/path that was embedded in the PSD.
            return Failure(document, "TPL_LEARN_AI_REFERENCE_BINDING_INVALID", "PSD 图片框与源照片的内容对应关系未闭合，未调用 AI。请重新导入并重新分析模板。", false, AiProviderOutcome.TerminalError);
        }
        if (referencedPhotos.Length is 0 or > AiAnalysisProductionPolicy.MaxReferenceAssetsPerRequest)
        {
            return Failure(document, "TPL_LEARN_AI_ASSET_LIMIT", "图片框实际使用的参考照片超过单次安全上限，请检查模板后重试。", false, AiProviderOutcome.TerminalError);
        }

        var prompts = frames.Select(frame =>
        {
            var frameContext = BuildFrameContext(frame);
            var layoutIntent = frame.LayoutIntent.Intent;
            return new ReferenceLearningFramePromptV2(
                frame.FrameId,
                frame.PageId,
                frame.ReferenceAssetId!,
                SafeContext(command.PageContext),
                frameContext,
                layoutIntent);
        }).ToArray();
        var cacheKey = ReferenceLearningAiCacheKeyFactory.Create(
            ReferenceLearningAiOperation.GenerateSelectionRequirements,
            referencedPhotos.Select(photo => (photo.AssetId, NormalizeHash(photo.Sha256))),
            document.ReferenceFolderDigest,
            prompts);
        var cached = await cache.FindAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        // Keep the established cache key readable; use a separate, provider-bound
        // request identity for durable billing/unknown ownership. No prompt or
        // local path is written to the ledger. Page identity survives relearning.
        var scope = "tpllearn_" + TemplateRuleCodec.Hash(JsonSerializer.Serialize(
            prompts.Select(frame => frame.PageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))[7..];
        var fingerprint = TemplateRuleCodec.Hash(JsonSerializer.Serialize(new
        {
            version = "reference-learning-request-identity/v1", provider = "volc-ark",
            model = ModelConfigVersion, configuration = ModelConfigVersion,
            task = ReferenceLearningAiOperation.GenerateSelectionRequirements,
            prompt = ReferenceLearningAiProtocol.SemanticPromptVersion,
            schema = ReferenceLearningAiProtocol.ResultSchemaVersion, input = cacheKey, scope
        }));
        var saved = await ledger.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        var cachedResult = cached is null
            ? null
            : NormalizeSemanticResult(prompts, cached.Result);
        if (IsCompatibleCache(cached, ReferenceLearningAiOperation.GenerateSelectionRequirements, document.ReferenceFolderDigest)
            && cachedResult is not null
            && ValidateSemanticResult(prompts, cachedResult)
            && (saved is null || saved.RequestId == cachedResult.RequestId
                && saved.Status is AiLedgerStatusPolicy.Analysing or AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.PlanClosed))
        {
            // Atomic result write may have completed just before a crash at the
            // ledger transition. Close that same request, never call again.
            if (saved?.Status == AiLedgerStatusPolicy.Analysing)
            {
                var usage = await ledger.LoadUsageAsync(saved.RequestId, cancellationToken).ConfigureAwait(false);
                if (cached!.Usage is { } measured && !usage.Any(item => item.InputUnits == measured.InputTokens
                    && item.OutputUnits == measured.OutputTokens && item.ModelConfigVersion == ModelConfigVersion))
                    return Failure(document, "TPL_LEARN_AI_EVIDENCE_INCOMPLETE", "用量与结果证据未闭合，请核查原请求；不会重新提交。", false, AiProviderOutcome.AiResultUnknown);
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.Validated,
                    $"ai-analysis/reference-learning-v2/{cacheKey[7..]}.json", clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
            // A v4 provider may still return a sentence that can be reduced to
            // a stable category. Persist that projection so the same cache
            // entry cannot reintroduce the narrative on the next open.
            if (!ReferenceSemanticResultsEqual(cached!.Result, cachedResult))
            {
                await cache.SaveAsync(cached! with { Result = cachedResult }, cancellationToken).ConfigureAwait(false);
            }
            var cachedDocument = await ApplyPersistedOverridesAsync(
                ApplySemantics(document, cachedResult, cacheKey, cached!.ModelConfigVersion, cached!.PromptContractVersion),
                cancellationToken).ConfigureAwait(false);
            return new ReferenceSemanticGenerationResult(
                cachedDocument,
                cacheKey,
                true,
                null);
        }

        if (saved is not null && saved.Status != AiLedgerStatusPolicy.Queued)
        {
            var unresolved = saved.Status is AiLedgerStatusPolicy.Uploading or AiLedgerStatusPolicy.Analysing or AiLedgerStatusPolicy.AiResultUnknown;
            return FailureWithUnavailableMetadata(document, cacheKey,
                unresolved ? "TPL_LEARN_AI_RESULT_UNKNOWN" : "TPL_LEARN_AI_REQUEST_ALREADY_FINISHED",
                "原请求已有记录；请核查结果或手工填写规则，不会自动重投。", false,
                unresolved ? AiProviderOutcome.AiResultUnknown : AiProviderOutcome.TerminalError);
        }

        var consent = await admission.TryAcquireAsync(AiConsentScopes.AnalysisCopy, cancellationToken).ConfigureAwait(false);
        if (consent is null || !consent.IsGranted)
        {
            return FailureWithUnavailableMetadata(document, cacheKey, "AI_UPLOAD_NOT_AUTHORIZED", "请先在设置中允许上传压缩分析图，或手工填写完整选图要求。", false, AiProviderOutcome.TerminalError);
        }

        var prepared = await PrepareReferenceAssetsAsync(command.ReferencePhotoDirectory, referencedPhotos, cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is not null)
        {
            return FailureWithUnavailableMetadata(document, cacheKey, prepared.Failure.Code, prepared.Failure.UserAction, prepared.Failure.Retryable, prepared.Failure.Outcome);
        }
        var releaseCopies = true;
        try
        {
            var request = CreateRequest(
                ReferenceLearningAiOperation.GenerateSelectionRequirements,
                document.LearningId,
                consent.ConsentId,
                document.ReferenceFolderDigest,
                prepared.Assets,
                prompts);
            var now = clock.GetUtcNow();
            saved ??= await ledger.RecordRequestAsync(new(request.RequestId, fingerprint, scope,
                "reference_learning", AiLedgerStatusPolicy.Queued, null, now, now), cancellationToken).ConfigureAwait(false);
            request = request with { RequestId = saved.RequestId };
            try
            {
                // Existing SQLite writer reserves budget/concurrency atomically
                // before crossing the provider boundary, including across restarts.
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Queued, AiLedgerStatusPolicy.Uploading, null, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                releaseCopies = false;
            }
            catch (InvalidOperationException error) when (error.Message is "AI_REQUEST_STATUS_CONFLICT"
                or "TPL_LEARN_AI_RESULT_UNKNOWN" or "AI_BUDGET_HARD_STOP" or "AI_CONCURRENCY_LIMIT")
            {
                return FailureWithUnavailableMetadata(document, cacheKey, error.Message,
                    "请求尚未解决或达到调用安全上限；未再次调用 AI，请核查原请求或手工填写。", false, AiProviderOutcome.AiResultUnknown);
            }
            await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Uploading, AiLedgerStatusPolicy.Analysing, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            ReferenceLearningAiExecutionResult execution;
            try { execution = await ai.AnalyzeReferenceLearningAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or HttpRequestException)
            {
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.AiResultUnknown, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                return FailureWithUnavailableMetadata(document, cacheKey, "TPL_LEARN_AI_RESULT_UNKNOWN",
                    "AI 请求结果未确定，保留原请求身份；请核查，不会自动重投。", false, AiProviderOutcome.AiResultUnknown);
            }
            // Failed/invalid results may still have incurred cost. Persist measured
            // usage before inspecting result semantics, even if the UI was cancelled.
            await RecordUsageAsync(request.RequestId, execution.Usage, CancellationToken.None).ConfigureAwait(false);
            if (!execution.IsSuccess)
            {
                var status = execution.Outcome == AiProviderOutcome.AiResultUnknown ? AiLedgerStatusPolicy.AiResultUnknown
                    : execution.Outcome == AiProviderOutcome.Cancelled ? AiLedgerStatusPolicy.Cancelled
                    : execution.Outcome == AiProviderOutcome.RetryableError ? AiLedgerStatusPolicy.RetryableError : AiLedgerStatusPolicy.TerminalError;
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, status, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                releaseCopies = status != AiLedgerStatusPolicy.AiResultUnknown;
                return FailureWithUnavailableMetadata(document, cacheKey, execution.Error?.Code ?? "TPL_LEARN_AI_UNAVAILABLE", execution.Error?.UserAction ?? "AI 语义不可用，请重试或手工填写完整要求。", execution.Error?.Retryable ?? false, execution.Outcome);
            }
            var normalizedResult = NormalizeSemanticResult(request.Frames, execution.Result!);
            if (normalizedResult is null || !ValidateSemanticResult(request, normalizedResult))
            {
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.TerminalError, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                releaseCopies = true;
                return FailureWithUnavailableMetadata(document, cacheKey, "TPL_LEARN_AI_RESULT_INVALID", "AI 返回的规则不是 2–40 字的可复用图片类别，已安全拒绝；请重试或手工填写。", false, AiProviderOutcome.TerminalError);
            }

            var entry = new ReferenceLearningAiCacheEntry(
                cacheKey,
                request.Operation,
                request.SchemaVersion,
                request.ResponseSchemaVersion,
                request.ModelConfigVersion,
                request.PromptContractVersion,
                request.CompressionProfileVersion,
                request.ReferenceSetDigest,
                normalizedResult,
                execution.Usage,
                clock.GetUtcNow());
            await cache.SaveAsync(entry, CancellationToken.None).ConfigureAwait(false);
            await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.Validated,
                $"ai-analysis/reference-learning-v2/{cacheKey[7..]}.json", clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            releaseCopies = true;
            var updated = await ApplyPersistedOverridesAsync(
                ApplySemantics(document, normalizedResult, cacheKey, request.ModelConfigVersion, request.PromptContractVersion),
                cancellationToken).ConfigureAwait(false);
            if (persistSession)
                await sessions.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return new ReferenceSemanticGenerationResult(updated, cacheKey, false, null);
        }
        finally
        {
            // Unknown or a failed durable write retains owned copies for recovery.
            // Never infer permission to clean them merely because the UI stopped.
            if (releaseCopies) await ReleasePreparedAsync(prepared.Assets, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<ReferenceLearningDocument> ApplyUserOverrideAsync(
        ReferenceLearningDocument document,
        string frameId,
        string selectionRequirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!ReferenceSelectionRequirementPolicy.IsValid(selectionRequirement))
            throw new InvalidOperationException("TPL_LEARN_SELECTION_REQUIREMENT_INVALID");
        var candidate = document.Candidates.SingleOrDefault(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("TPL_LEARN_FRAME_UNKNOWN");
        if (!string.IsNullOrWhiteSpace(candidate.SemanticMetadata?.CacheKey))
            await cache.InvalidateAsync(candidate.SemanticMetadata.CacheKey, cancellationToken).ConfigureAwait(false);
        var metadata = new ReferenceSemanticMetadata(
            ReferenceSemanticSource.UserOverride,
            ReferenceLearningAiProtocol.RequestSchemaVersion,
            ReferenceLearningAiProtocol.ResultSchemaVersion,
            ModelConfigVersion,
            "user-override/v1",
            "user-override",
            ["用户明确填写并确认"],
            [],
            true);
        await cache.SaveOverrideAsync(new ReferenceSemanticOverride(
            document.LearningId,
            frameId,
            selectionRequirement.Trim(),
            clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        var updatedCandidates = document.Candidates.Select(item => string.Equals(item.FrameId, frameId, StringComparison.Ordinal)
            ? item with { SemanticLabel = selectionRequirement.Trim(), Confidence = 1, SemanticMetadata = metadata }
            : item).ToArray();
        var updated = document.WithCandidates(updatedCandidates, ReferenceLearningState.AwaitingReview, null, clock.GetUtcNow());
        await sessions.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<ReferenceLearningDocument> ApplyPersistedOverridesAsync(
        ReferenceLearningDocument document,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ReferenceFrameCandidate>(document.Candidates.Count);
        foreach (var candidate in document.Candidates)
        {
            var value = await cache.FindOverrideAsync(document.LearningId, candidate.FrameId, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                candidates.Add(candidate);
                continue;
            }
            candidates.Add(candidate with
            {
                SemanticLabel = value.SelectionRequirement,
                Confidence = 1,
                ReviewDecision = string.Equals(candidate.SemanticLabel, value.SelectionRequirement, StringComparison.Ordinal)
                    ? candidate.ReviewDecision
                    : ReferenceReviewDecision.Pending,
                SemanticMetadata = new ReferenceSemanticMetadata(
                    ReferenceSemanticSource.UserOverride,
                    ReferenceLearningAiProtocol.RequestSchemaVersion,
                    ReferenceLearningAiProtocol.ResultSchemaVersion,
                    ModelConfigVersion,
                    "user-override/v1",
                    "user-override",
                    ["用户明确填写并确认"],
                    [],
                    true)
            });
        }
        return document.WithCandidates(candidates, ReferenceLearningState.AwaitingReview, null, clock.GetUtcNow());
    }

    private ReferenceLearningDocument ApplySemantics(
        ReferenceLearningDocument document,
        ReferenceLearningAiResultV2 result,
        string cacheKey,
        string modelVersion,
        string promptVersion)
    {
        var byFrame = result.FrameResults.ToDictionary(item => item.FrameId, StringComparer.Ordinal);
        var candidates = document.Candidates.Select(candidate =>
        {
            if (!byFrame.TryGetValue(candidate.FrameId, out var semantic)) return candidate;
            return candidate with
            {
                SemanticLabel = semantic.SelectionRequirement.Trim(),
                Confidence = (double)semantic.Confidence,
                ReviewDecision = string.Equals(candidate.SemanticLabel, semantic.SelectionRequirement.Trim(), StringComparison.Ordinal)
                    ? candidate.ReviewDecision
                    : ReferenceReviewDecision.Pending,
                SemanticMetadata = new ReferenceSemanticMetadata(
                    ReferenceSemanticSource.AiV2,
                    ReferenceLearningAiProtocol.RequestSchemaVersion,
                    ReferenceLearningAiProtocol.ResultSchemaVersion,
                    modelVersion,
                    promptVersion,
                    cacheKey,
                    semantic.PositiveEvidence,
                    semantic.ExclusionEvidence)
            };
        }).ToArray();
        return document.WithCandidates(candidates, ReferenceLearningState.AwaitingReview, null, clock.GetUtcNow());
    }

    private static bool ValidateSemanticResult(ReferenceLearningAiRequestV2 request, ReferenceLearningAiResultV2 result) =>
        string.Equals(result.SchemaVersion, ReferenceLearningAiProtocol.ResultSchemaVersion, StringComparison.Ordinal)
        && string.Equals(result.DocumentKind, "result", StringComparison.Ordinal)
        && string.Equals(result.Operation, request.Operation, StringComparison.Ordinal)
        && string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal)
        && string.Equals(result.ModelConfigVersion, request.ModelConfigVersion, StringComparison.Ordinal)
        && ValidateSemanticResult(request.Frames, result);

    private static bool ValidateSemanticResult(
        IReadOnlyList<ReferenceLearningFramePromptV2> frames,
        ReferenceLearningAiResultV2 result) =>
        string.Equals(result.SchemaVersion, ReferenceLearningAiProtocol.ResultSchemaVersion, StringComparison.Ordinal)
        && string.Equals(result.DocumentKind, "result", StringComparison.Ordinal)
        && string.Equals(result.Operation, ReferenceLearningAiOperation.GenerateSelectionRequirements, StringComparison.Ordinal)
        && string.Equals(result.ModelConfigVersion, ModelConfigVersion, StringComparison.Ordinal)
        && HasExactUniqueIds(frames.Select(item => item.FrameId), result.FrameResults.Select(item => item.FrameId))
        && result.FrameResults.All(item => item.PositiveEvidence.Count is > 0 and <= 8
            && item.ExclusionEvidence.Count <= 8
            && item.PositiveEvidence.All(IsValidEvidence)
            && item.ExclusionEvidence.All(IsValidEvidence)
            && ReferenceSelectionRequirementPolicy.IsValid(item.SelectionRequirement)
            && item.Confidence is >= 0 and <= 1)
        && SameAssetFramesUseSameRequirement(frames, result);

    private static ReferenceLearningAiResultV2? NormalizeSemanticResult(
        IReadOnlyList<ReferenceLearningFramePromptV2> frames,
        ReferenceLearningAiResultV2 result)
    {
        if (result.FrameResults.Count != frames.Count)
        {
            return null;
        }

        var normalized = new List<ReferenceLearningFrameResultV2>(result.FrameResults.Count);
        foreach (var frame in result.FrameResults)
        {
            if (!ReferenceSelectionRequirementPolicy.TryNormalize(
                    frame.SelectionRequirement,
                    out var selectionRequirement))
            {
                return null;
            }

            normalized.Add(frame with { SelectionRequirement = selectionRequirement });
        }

        return result with { FrameResults = normalized };
    }

    private static bool SameAssetFramesUseSameRequirement(
        IReadOnlyList<ReferenceLearningFramePromptV2> frames,
        ReferenceLearningAiResultV2 result)
    {
        var assetByFrame = frames.ToDictionary(frame => frame.FrameId, frame => frame.ReferenceAssetId, StringComparer.Ordinal);
        return result.FrameResults
            .GroupBy(frame => assetByFrame.GetValueOrDefault(frame.FrameId, string.Empty), StringComparer.Ordinal)
            .All(group => group.Select(frame => frame.SelectionRequirement).Distinct(StringComparer.Ordinal).Count() <= 1);
    }

    private static bool ReferenceBindingsAreClosed(
        IReadOnlyList<ReferenceFrameCandidate> frames,
        IReadOnlyList<ReferencePhotoFingerprint> allPhotos,
        ReferencePhotoFingerprint[] referencedPhotos)
    {
        var photosById = new Dictionary<string, ReferencePhotoFingerprint>(StringComparer.Ordinal);
        foreach (var photo in allPhotos)
        {
            if (string.IsNullOrWhiteSpace(photo.AssetId)
                || !photosById.TryAdd(photo.AssetId, photo))
            {
                return false;
            }
        }

        var referencedIds = referencedPhotos
            .Select(photo => photo.AssetId)
            .ToHashSet(StringComparer.Ordinal);
        if (referencedIds.Count != referencedPhotos.Length
            || !referencedIds.SetEquals(frames.Select(frame => frame.ReferenceAssetId!)))
        {
            return false;
        }

        foreach (var frame in frames)
        {
            if (string.IsNullOrWhiteSpace(frame.ReferenceAssetId)
                || string.IsNullOrWhiteSpace(frame.ReferencePhotoRelativePath)
                || !photosById.TryGetValue(frame.ReferenceAssetId, out var photo)
                || !string.Equals(frame.ReferencePhotoRelativePath, photo.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Exact PSD evidence must close through the embedded payload hash.
            // A manually confirmed frame may intentionally select a different
            // source photo, so it is checked by the explicit path/asset choice
            // above and does not require the old embedded hash to match.
            if (frame.MatchState == ReferenceMatchState.Exact
                && (string.IsNullOrWhiteSpace(frame.EmbeddedContentSha256)
                    || !string.Equals(frame.EmbeddedContentSha256, photo.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReferenceSemanticResultsEqual(
        ReferenceLearningAiResultV2 left,
        ReferenceLearningAiResultV2 right)
    {
        if (left.FrameResults.Count != right.FrameResults.Count)
        {
            return false;
        }

        for (var index = 0; index < left.FrameResults.Count; index++)
        {
            var first = left.FrameResults[index];
            var second = right.FrameResults[index];
            if (!string.Equals(first.FrameId, second.FrameId, StringComparison.Ordinal)
                || !string.Equals(first.SelectionRequirement, second.SelectionRequirement, StringComparison.Ordinal)
                || first.Confidence != second.Confidence
                || !first.PositiveEvidence.SequenceEqual(second.PositiveEvidence, StringComparer.Ordinal)
                || !first.ExclusionEvidence.SequenceEqual(second.ExclusionEvidence, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidEvidence(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 200;

    private static bool HasExactUniqueIds(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedIds = expected.ToArray();
        var actualIds = actual.ToArray();
        return expectedIds.Distinct(StringComparer.Ordinal).Count() == expectedIds.Length
            && actualIds.Distinct(StringComparer.Ordinal).Count() == actualIds.Length
            && expectedIds.ToHashSet(StringComparer.Ordinal).SetEquals(actualIds);
    }

    private static bool IsCompatibleCache(ReferenceLearningAiCacheEntry? entry, string operation, string referenceDigest) =>
        entry is { IsReusable: true }
        && entry.Operation == operation
        && entry.RequestSchemaVersion == ReferenceLearningAiProtocol.RequestSchemaVersion
        && entry.ResultSchemaVersion == ReferenceLearningAiProtocol.ResultSchemaVersion
        && entry.ModelConfigVersion == ModelConfigVersion
        && entry.PromptContractVersion == ReferenceLearningAiProtocol.SemanticPromptVersion
        && entry.CompressionProfileVersion == CompressionProfileVersion
        && entry.ReferenceSetDigest == referenceDigest;

    private static ReferenceLearningAiRequestV2 CreateRequest(
        string operation, string correlationId, string consentId, string referenceDigest,
        IReadOnlyList<AiAssetInputV1> assets, IReadOnlyList<ReferenceLearningFramePromptV2> frames) => new(
            ReferenceLearningAiProtocol.RequestSchemaVersion, "request", operation,
            "rl_" + Guid.NewGuid().ToString("N"), SafeId(correlationId), ModelConfigVersion,
            ReferenceLearningAiProtocol.SemanticPromptVersion,
            ReferenceLearningAiProtocol.ResultSchemaVersion, CompressionProfileVersion,
            SafeId(consentId), NormalizeHash(referenceDigest), assets, frames);

    private async Task<(IReadOnlyList<AiAssetInputV1> Assets, ReferenceLearningAiFailure? Failure)> PrepareReferenceAssetsAsync(
        string inputRoot, IReadOnlyList<ReferencePhotoFingerprint> photos, CancellationToken cancellationToken)
    {
        var assets = new List<AiAssetInputV1>();
        try
        {
            foreach (var photo in photos)
            {
                var format = ParseFormat(photo.MimeType);
                var source = SourceAssetMetadata.CreateReadable(photo.AssetId, "reference", photo.RelativePath, photo.FileName,
                    photo.Length, RawHash(photo.Sha256), photo.Width, photo.Height, format, photo.MimeType);
                assets.Add(await PrepareAsync(inputRoot, source, cancellationToken).ConfigureAwait(false));
            }
            return (assets, null);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await ReleasePreparedAsync(assets, CancellationToken.None).ConfigureAwait(false);
            return ([], new ReferenceLearningAiFailure("TPL_LEARN_ANALYSIS_COPY_FAILED", "无法创建压缩分析副本，请检查源照片后重试。", false, AiProviderOutcome.TerminalError));
        }
    }

    private async Task<AiAssetInputV1> PrepareAsync(string inputRoot, SourceAssetMetadata source, CancellationToken cancellationToken)
    {
        var reference = "rlcopy_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AssetId + "|" + source.Sha256))).ToLowerInvariant()[..32];
        var copy = await copyPreparer.PrepareAsync(new AiAnalysisCopyPreparationRequest(reference, inputRoot, source, CompressionProfileVersion), cancellationToken).ConfigureAwait(false);
        return new AiAssetInputV1(source.AssetId, NormalizeHash(source.Sha256), source.MimeType, source.PixelWidth, source.PixelHeight,
            copy.Reference, copy.Digest, copy.ByteLength, copy.MimeType, copy.CompressionProfileVersion, copy.LeaseId);
    }

    private async Task ReleasePreparedAsync(IEnumerable<AiAssetInputV1> assets, CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
            if (asset.AnalysisCopyLeaseId is not null)
                try { await leases.ReleaseAsync(asset.AnalysisCopyReference, asset.AnalysisCopyLeaseId, cancellationToken).ConfigureAwait(false); }
                catch { }
    }

    private async Task RecordUsageAsync(string requestId, AiProviderUsage? usage, CancellationToken cancellationToken)
    {
        if (usage is null) return;
        await ledger.RecordUsageAsync(new("usage_" + requestId, requestId, ModelConfigVersion,
            usage.InputTokens, usage.OutputTokens, usage.CacheHit, 0, null, null, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        if (usage.InputTokens > AiAnalysisProductionPolicy.MaxInputTokensPerBatch
            || usage.OutputTokens > AiAnalysisProductionPolicy.MaxOutputTokensPerBatch)
            throw new InvalidOperationException("AI_BUDGET_HARD_STOP");
        await usageRecorder.RecordAsync(new AiUsageEvent("usage_" + requestId, requestId, usage.InputTokens, usage.OutputTokens, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    private static SourceImageFormat ParseFormat(string mimeType) => mimeType switch
    {
        "image/jpeg" => SourceImageFormat.Jpeg,
        "image/png" => SourceImageFormat.Png,
        "image/tiff" => SourceImageFormat.Tiff,
        _ => throw new InvalidOperationException("TPL_LEARN_AI_SOURCE_FORMAT_UNSUPPORTED")
    };

    private static string SafeId(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')) return value;
        return "rl_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant()[..32];
    }

    private static string SafeContext(string? value) => string.IsNullOrWhiteSpace(value) ? "未提供页面文字上下文，仅依据参考组可见内容。" : value.Trim()[..Math.Min(value.Trim().Length, 2000)];

    private static string BuildFrameContext(ReferenceFrameCandidate _) =>
        "仅依据与该图片框通过内容哈希闭合的参考照片可见主体，生成未来可复用的图片类别；不要复述单张照片的场景或版式。";

    private static string NormalizeHash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) ? value.ToLowerInvariant() : "sha256:" + value.ToLowerInvariant();
    private static string RawHash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal) ? value[7..].ToLowerInvariant() : value.ToLowerInvariant();

    private static ReferenceSemanticGenerationResult Failure(ReferenceLearningDocument document, string code, string action, bool retryable, AiProviderOutcome outcome) =>
        new(document, null, false, new ReferenceLearningAiFailure(code, action, retryable, outcome));

    private ReferenceSemanticGenerationResult FailureWithUnavailableMetadata(ReferenceLearningDocument document, string cacheKey, string code, string action, bool retryable, AiProviderOutcome outcome)
    {
        var candidates = document.Candidates.Select(candidate => candidate with
        {
            SemanticLabel = IsUserOverrideSemantic(candidate)
                ? candidate.SemanticLabel
                : ReferenceSelectionRequirementPolicy.UnconfirmedLabel,
            Confidence = IsUserOverrideSemantic(candidate) ? candidate.Confidence : 0,
            ReviewDecision = IsUserOverrideSemantic(candidate)
                ? candidate.ReviewDecision
                : ReferenceReviewDecision.Pending,
            SemanticMetadata = IsUserOverrideSemantic(candidate)
                ? candidate.SemanticMetadata
                : new ReferenceSemanticMetadata(ReferenceSemanticSource.AiUnavailable,
                    ReferenceLearningAiProtocol.RequestSchemaVersion, ReferenceLearningAiProtocol.ResultSchemaVersion,
                    ModelConfigVersion, ReferenceLearningAiProtocol.SemanticPromptVersion, cacheKey, [], [])
        }).ToArray();
        var updated = document.WithCandidates(candidates, ReferenceLearningState.AwaitingReview, action, clock.GetUtcNow());
        return new ReferenceSemanticGenerationResult(updated, cacheKey, false, new ReferenceLearningAiFailure(code, action, retryable, outcome));
    }

    private static bool IsUserOverrideSemantic(ReferenceFrameCandidate candidate) =>
        candidate.SemanticMetadata?.Source == ReferenceSemanticSource.UserOverride
        && ReferenceSelectionRequirementPolicy.IsValid(candidate.SemanticLabel);

}

public static class ReferenceLearningAiCacheKeyFactory
{
    public static string Create(string operation, IEnumerable<(string AssetId, string ContentHash)> assets,
        string referenceSetDigest, IEnumerable<ReferenceLearningFramePromptV2> frames)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            operation,
            assets = assets.OrderBy(item => item.AssetId, StringComparer.Ordinal).Select(item => new { item.AssetId, item.ContentHash }),
            model_config_version = ReferenceLearningAiService.ModelConfigVersion,
            prompt_contract_version = ReferenceLearningAiProtocol.SemanticPromptVersion,
            response_schema_version = ReferenceLearningAiProtocol.ResultSchemaVersion,
            compression_profile_version = ReferenceLearningAiService.CompressionProfileVersion,
            reference_set_digest = referenceSetDigest,
            frames = frames.OrderBy(frame => frame.FrameId, StringComparer.Ordinal).Select(frame => new
            {
                frame.FrameId,
                frame.PageId,
                frame.ReferenceAssetId,
                frame.PageContext,
                frame.FrameContext,
                frame.LayoutIntent
            })
        });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
