using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>Text-only rule interpretation, using the existing consent, request ledger, writer and provider.</summary>
public sealed partial class TemplateRuleInterpretationService(ITemplateWorkspaceService workspace, IAiServicePort provider,
    IAiUploadAdmissionPort admission, IAiAnalysisStore ledger, IFileStore files, TimeProvider clock,
    ITemplateRepository templates) : ITemplateRuleInterpreter
{
    private sealed record StoredResponse(string Fingerprint, string ResponseDigest, TemplateRuleAiResponse Response);
    private sealed record RequestIdentity(string SchemaVersion, AiTaskProviderIdentity Provider,
        string TaskType, string PromptVersion, string ResultSchema, string InputDigest, string Fingerprint);

    public async Task<TemplateRuleInterpretationResult> InterpretAsync(TemplateDraftSession session,
        TemplateRuleInterpretationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identity = provider.TemplateRulesIdentity;
        identity.Validate();
        TemplateValueRules.Identifier(command.CommandId, "TPL_RULE_COMMAND_INVALID");
        var text = TemplateValueRules.RequiredText(command.Text, 8000, "TPL_RULE_MESSAGE_INVALID");
        if (command.References.IsDefault || command.References.Length > 1024 || UnsafeText().IsMatch(text))
            throw new TemplateRuleViolationException("TPL_RULE_MESSAGE_CONTAINS_PRIVATE_DATA");
        var editor = workspace as ITemplateRuleEditingService ?? throw new TemplateRuleViolationException("TPL_RULE_STORE_UNAVAILABLE");
        var draft = await editor.GetRulesAsync(session, cancellationToken).ConfigureAwait(false);
        if (draft.Version != command.ExpectedDraftVersion) throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
        // Capture the actual immutable draft, not a UI reconstruction or inferred frames.
        var revision = await templates.GetRevisionAsync(session.DraftRevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateRuleViolationException("TPL_REVISION_NOT_FOUND");
        if (revision.GroupId != session.GroupId || revision.RuleWorkspace?.Version != draft.Version
            || TemplateRuleCodec.BusinessDigest(revision) != draft.RuleDigest)
            throw new TemplateRuleViolationException("TPL_RULE_VERSION_CONFLICT");
        var source = await workspace.GetDraftAsync(session, cancellationToken).ConfigureAwait(false);
        var pages = source.Pages.Where(page => page.IsAffected).Select(page => new TemplateRuleAiPage(page.Page.PageId,
            SafeMetadata(page.Page.Name), page.Page.ContentType == TemplatePageContentTypeOption.DynamicSizeTable)).ToImmutableArray();
        var pageIds = pages.Select(page => page.PageId).ToHashSet(StringComparer.Ordinal);
        var proposals = draft.Candidates.Where(candidate => pageIds.Contains(candidate.PageId) && candidate.CanInterpret).ToArray();
        var frames = draft.Frames.Where(frame => pageIds.Contains(frame.PageId))
            .Concat(proposals.Select(candidate => new TemplateRuleFrameOverview(candidate.PageId, candidate.FrameId, ""))).ToImmutableArray();
        if (frames.Any(frame => UnsafeText().IsMatch(frame.Description)))
            throw new TemplateRuleViolationException("TPL_RULE_MESSAGE_CONTAINS_PRIVATE_DATA");
        var references = command.References.ToList();
        // Resolve typed @ references against stable aliases, never array positions or fuzzy names.
        foreach (Match match in AliasText().Matches(text))
        {
            var page = draft.Aliases.SingleOrDefault(alias => alias.FrameId is null && alias.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == match.Groups[1].Value)
                ?? throw new TemplateRuleViolationException("TPL_RULE_REFERENCE_INACTIVE");
            var frame = match.Groups[2].Success ? draft.Aliases.SingleOrDefault(alias => alias.PageId == page.PageId && alias.FrameId is not null
                && alias.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == match.Groups[2].Value)
                ?? throw new TemplateRuleViolationException("TPL_RULE_REFERENCE_INACTIVE") : null;
            references.Add(new(page.PageId, frame?.FrameId));
        }
        if (references.Any(reference => !pageIds.Contains(reference.PageId)
            || reference.FrameId is not null && !frames.Any(frame => frame.PageId == reference.PageId && frame.FrameId == reference.FrameId)))
            throw new TemplateRuleViolationException("TPL_RULE_REFERENCE_INACTIVE");
        if (references.Count == 0 && !text.Contains("全部", StringComparison.Ordinal) && !text.Contains("所有", StringComparison.Ordinal)
            && !text.Contains("模板组", StringComparison.Ordinal))
            return new("", null, "请用 @ 指明模板或框；涉及整组时可说“模板组”。", draft, true);
        var allowed = references.Count == 0 ? pages.Select(page => new TemplateRuleTargetReference(page.PageId, null)).ToImmutableArray()
            : references.Distinct().ToImmutableArray();
        var unsetPages = pages.Where(page => !page.IsSizeTable && allowed.Any(target => target.PageId == page.PageId)
            && !frames.Any(frame => frame.PageId == page.PageId)).ToArray();
        if (unsetPages.Length > 0)
        {
            var labels = unsetPages.Select(page => "@" + draft.Aliases.Single(alias => alias.PageId == page.PageId && alias.FrameId is null).Number);
            return new("", null, string.Join("、", labels)
                + " 尚无结构与像素证据闭合的可用图片框。请重新解析或检查模板结构；无需导入参考款。本次未发送 AI 请求。", draft, true);
        }
        var ambiguous = pages.Where(page => allowed.Any(target => target.PageId == page.PageId && target.FrameId is null)
            && proposals.Count(candidate => candidate.PageId == page.PageId) > 1
            && !allowed.Any(target => target.PageId == page.PageId && target.FrameId is not null)).ToArray();
        if (ambiguous.Length > 0)
            return new("", null, "这张 PSD 有多个尚无规则的图片框，请在正文用 @ 引用具体框，避免把背景或装饰当成商品图。本次未发送 AI 请求。", draft, true);
        var requestId = "tplrule_" + TemplateRuleCodec.Hash(session.DraftRevisionId + ":" + command.CommandId)[7..];
        var request = new TemplateRuleAiRequest(TemplateRuleAiProtocol.RequestVersion, requestId, TemplateRuleAiProtocol.PromptVersion,
            identity.ConfigurationVersion, draft.Version, draft.RuleDigest, text, pages, frames,
            [.. draft.Aliases.Where(alias => pageIds.Contains(alias.PageId) && (alias.FrameId is null || frames.Any(frame => frame.FrameId == alias.FrameId)))],
            draft.Rules, allowed);
        var requestJson = TemplateRuleCodec.Serialize(request);
        if (Encoding.UTF8.GetByteCount(requestJson) > 128 * 1024) throw new TemplateRuleViolationException("TPL_RULE_AI_CONTEXT_LIMIT");
        // Content identity deliberately excludes a click ID: a second click after an unknown
        // result must find the same ledger entry, not cause another paid submission.
        var inputDigest = TemplateRuleCodec.Hash(proposals.Length == 0
            ? TemplateRuleCodec.Serialize(request with { RequestId = "" })
            : TemplateRuleCodec.Serialize(new { Request = request with { RequestId = "" },
                Candidates = proposals.Select(candidate => new { candidate.PageId, candidate.FrameId, candidate.Target }) }));
        var fingerprint = TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(new
        { Version = "template-rule-request-identity/v2", identity.Provider, identity.Model, identity.ConfigurationVersion,
            TaskType = TemplateRuleAiProtocol.RequestVersion, TemplateRuleAiProtocol.ResultVersion, inputDigest }));
        var saved = await ledger.FindRequestByFingerprintAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        if (saved is null && identity == AiTaskProviderIdentity.LegacyTemplateRules)
        {
            // Preserve the original provider's v1 cache/unknown ownership. The same
            // intent must not become a new paid operation solely due to this upgrade.
            saved = await ledger.FindRequestByFingerprintAsync(inputDigest, cancellationToken).ConfigureAwait(false);
            if (saved is not null) fingerprint = inputDigest;
        }
        StoredResponse result;
        if (saved is null || saved.Status == AiLedgerStatusPolicy.Queued)
        {
            // No provider call happens before durable authorization and a CAS claim.
            var consent = await admission.TryAcquireAsync(identity.ConsentScope, cancellationToken).ConfigureAwait(false);
            if (consent?.IsGranted != true) throw new TemplateRuleViolationException("AI_UPLOAD_NOT_AUTHORIZED");
            var now = clock.GetUtcNow();
            saved ??= await ledger.RecordRequestAsync(new(requestId, fingerprint,
                "tplrules_" + session.DraftRevisionId, "template_rules", AiLedgerStatusPolicy.Queued, null, now, now), cancellationToken).ConfigureAwait(false);
            request = request with { RequestId = saved.RequestId };
            // Audit only bounded public configuration and digests; never persist the
            // prompt, credential, endpoint or complete transport request here. Failure
            // to persist identity precedes admission and cannot incur provider cost.
            await files.WriteAtomicallyAsync($"ai/template-rule-results/{saved.RequestId}.provider.json",
                Encoding.UTF8.GetBytes(TemplateRuleCodec.Serialize(new RequestIdentity(
                    "template-rule-request-identity/v2", identity, TemplateRuleAiProtocol.RequestVersion,
                    request.PromptVersion, TemplateRuleAiProtocol.ResultVersion, inputDigest, fingerprint))),
                cancellationToken).ConfigureAwait(false);
            await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Queued, AiLedgerStatusPolicy.Uploading, null, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Uploading, AiLedgerStatusPolicy.Analysing, null, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            TemplateRuleAiExecution execution;
            try { execution = await provider.InterpretTemplateRulesAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
            {
                // An adapter exception is not evidence that a paid request was never accepted.
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.AiResultUnknown, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                throw new TemplateRuleViolationException("TPL_RULE_AI_RESULT_UNKNOWN");
            }
            if (execution.Usage is { } usage)
                await ledger.RecordUsageAsync(new("usage_" + saved.RequestId, saved.RequestId, identity.ConfigurationVersion,
                    usage.InputTokens, usage.OutputTokens, usage.CacheHit, 0, null, null, clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            if (execution.Outcome != AiProviderOutcome.Completed || execution.Response is null)
            {
                var status = execution.Outcome == AiProviderOutcome.AiResultUnknown ? AiLedgerStatusPolicy.AiResultUnknown
                    : execution.Outcome == AiProviderOutcome.Cancelled ? AiLedgerStatusPolicy.Cancelled
                    : execution.Outcome == AiProviderOutcome.RetryableError ? AiLedgerStatusPolicy.RetryableError : AiLedgerStatusPolicy.TerminalError;
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, status, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                throw new TemplateRuleViolationException(execution.Error?.Code ?? "TPL_RULE_AI_UNAVAILABLE");
            }
            try { ValidateResponse(request, execution.Response); ValidateExecutableBranches(
                StageCandidateBindings(revision, proposals, source, execution.Response), execution.Response); }
            catch (TemplateRuleViolationException)
            {
                await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.TerminalError, null, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            result = new(fingerprint, TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(execution.Response)), execution.Response);
            var path = $"ai/template-rule-results/{saved.RequestId}.json";
            await files.WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes(TemplateRuleCodec.Serialize(result)), CancellationToken.None).ConfigureAwait(false);
            saved = await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Analysing, AiLedgerStatusPolicy.Validated, path, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        else if (saved.Status is AiLedgerStatusPolicy.Validated or AiLedgerStatusPolicy.PlanClosed)
        {
            var bytes = await files.ReadAsync(saved.ResultReference!, cancellationToken).ConfigureAwait(false)
                ?? throw new TemplateRuleViolationException("TPL_RULE_AI_RESULT_MISSING");
            result = TemplateRuleCodec.Deserialize<StoredResponse>(Encoding.UTF8.GetString(bytes));
            if (result.Fingerprint != fingerprint || result.ResponseDigest != TemplateRuleCodec.Hash(TemplateRuleCodec.Serialize(result.Response)))
                throw new TemplateRuleViolationException("TPL_RULE_AI_RESULT_CORRUPT");
        }
        else throw new TemplateRuleViolationException(saved.Status is AiLedgerStatusPolicy.Uploading or AiLedgerStatusPolicy.Analysing or AiLedgerStatusPolicy.AiResultUnknown
            ? "TPL_RULE_AI_RESULT_UNKNOWN" : "TPL_RULE_AI_REQUEST_ALREADY_FINISHED");

        ValidateResponse(request, result.Response);
        var staged = StageCandidateBindings(revision, proposals, source, result.Response);
        ValidateExecutableBranches(staged, result.Response);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(result.Response.Clarification))
            return new(saved.RequestId, null, result.Response.Clarification, draft, true);
        var patchId = "apply_" + saved.RequestId;
        var additions = staged.FrameBindings.Where(frame => !revision.FrameBindings.Any(original => original.FrameId == frame.FrameId))
            .Select(frame => new TemplateRuleTargetEdit(frame.FrameId, TemplatePackageService.ToPackageBinding(frame))).ToArray();
        var updated = await editor.ApplyRulePatchAsync(session, new(patchId, command.ExpectedDraftVersion,
            new([.. result.Response.Frames.Where(edit => !additions.Any(target => target.FrameId == edit.FrameId))],
                result.Response.Branches, Targets: additions, Source: new("ai", text, saved.RequestId,
                fingerprint, identity.ConfigurationVersion, TemplateRuleAiProtocol.PromptVersion))), cancellationToken).ConfigureAwait(false);
        if (saved.Status == AiLedgerStatusPolicy.Validated) await ledger.UpdateRequestStatusAsync(saved.RequestId, AiLedgerStatusPolicy.Validated, AiLedgerStatusPolicy.PlanClosed,
            saved.ResultReference, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        return new(saved.RequestId, patchId, result.Response.Summary, updated, false);
    }

    private static TemplateRevision StageCandidateBindings(TemplateRevision revision, TemplateRuleCandidateView[] candidates,
        TemplateDraftView source, TemplateRuleAiResponse response)
    {
        var staged = revision;
        foreach (var edit in response.Frames)
        {
            if (candidates.SingleOrDefault(candidate => candidate.FrameId == edit.FrameId) is not { } candidate) continue;
            var canvas = source.Pages.Single(page => page.Page.PageId == candidate.PageId).Resource.Inspection.Canvas;
            var binding = TemplateCalibrationService.CreateRuleOnlyBinding(revision, candidate.Target, canvas, edit.Description);
            if (binding.FrameId != edit.FrameId) throw new TemplateRuleViolationException("TPL_RULE_TARGET_REFERENCE_INVALID");
            staged = staged.AddFrameBinding(binding);
        }
        return staged;
    }

    public static void ValidateResponse(TemplateRuleAiRequest request, TemplateRuleAiResponse response)
    {
        if (response.SchemaVersion != TemplateRuleAiProtocol.ResultVersion || response.Frames.IsDefault || response.Branches.IsDefault
            || response.Frames.Length > 1024 || response.Branches.Length > 128 || string.IsNullOrWhiteSpace(response.Summary) || response.Summary.Length > 1000
            || response.Clarification?.Length > 2000 || !string.IsNullOrEmpty(response.Clarification) && (!response.Frames.IsEmpty || !response.Branches.IsEmpty)
            || response.Frames.IsEmpty && response.Branches.IsEmpty && string.IsNullOrWhiteSpace(response.Clarification))
            throw new TemplateRuleViolationException("TPL_RULE_AI_RESPONSE_INVALID");
        bool Allowed(string page, string? frame) => request.AllowedTargets.Any(target => target.PageId == page && (target.FrameId is null || target.FrameId == frame));
        foreach (var edit in response.Frames)
        {
            if (edit is null || request.Frames.SingleOrDefault(frame => frame.FrameId == edit.FrameId) is not { } target || !Allowed(target.PageId, target.FrameId))
                throw new TemplateRuleViolationException("TPL_RULE_AI_SCOPE_VIOLATION");
            _ = SelectionRequirement.Create(edit.Description);
        }
        foreach (var edit in response.Branches)
        {
            if (edit is null) throw new TemplateRuleViolationException("TPL_RULE_AI_RESPONSE_INVALID");
            var previous = request.Rules?.Branches.SingleOrDefault(branch => branch.BranchId == edit.BranchId);
            var branch = edit.Branch is null ? null : TemplateRuleViewMapper.ToDomain(edit.Branch);
            if (branch is not null)
            {
                TemplateRuleCodec.ValidateRuleShape(new(1, [branch]));
                if (branch.BranchId != edit.BranchId || branch.Recipes.Any(recipe => !request.Pages.Any(page => page.PageId == recipe.PageId)
                    || recipe.FrameBindings.Any(binding => !request.Frames.Any(frame => frame.FrameId == binding.FrameId && frame.PageId == recipe.PageId))))
                    throw new TemplateRuleViolationException("TPL_RULE_AI_SCOPE_VIOLATION");
            }
            if ((edit.Branch?.Recipes ?? []).Concat(previous?.Recipes ?? []).Any(recipe => !Allowed(recipe.PageId, null)))
                throw new TemplateRuleViolationException("TPL_RULE_AI_SCOPE_VIOLATION");
        }
        if (response.Frames.Select(edit => edit.FrameId).Distinct(StringComparer.Ordinal).Count() != response.Frames.Length
            || response.Branches.Select(edit => edit.BranchId).Distinct(StringComparer.Ordinal).Count() != response.Branches.Length)
            throw new TemplateRuleViolationException("TPL_RULE_AI_RESPONSE_INVALID");
        _ = TemplateRuleCodec.Serialize(response);
    }

    private static void ValidateExecutableBranches(TemplateRevision revision, TemplateRuleAiResponse response)
    {
        if (response.Branches.IsEmpty) return;
        var branches = (revision.RuleWorkspace?.Rules?.Branches ?? []).ToList();
        foreach (var edit in response.Branches)
        {
            var index = branches.FindIndex(branch => branch.BranchId == edit.BranchId);
            if (edit.Branch is null)
            {
                if (index < 0) throw new TemplateRuleViolationException("TPL_RULE_BRANCH_REFERENCE_INVALID");
                branches.RemoveAt(index);
            }
            else if (index < 0) branches.Add(TemplateRuleViewMapper.ToDomain(edit.Branch));
            else branches[index] = TemplateRuleViewMapper.ToDomain(edit.Branch);
        }
        // Reuse the production evaluator against persisted structure. Manual editing
        // still permits diagnostic drafts; an AI result must not be labelled validated
        // and written to the draft when its merged branch patch cannot execute.
        var diagnostics = TemplateRuleEvaluator.Validate(new(1, [.. branches]), revision);
        if (!diagnostics.IsEmpty) throw new TemplateRuleViolationException(diagnostics[0].Code);
        foreach (var branch in branches.Where(branch => branch.RequireCompleteColorCoverage))
        {
            // Reject provable duplicate coverage without fabricating target-product
            // color facts. Runtime Evaluate remains authoritative for actual counts.
            var all = branch.Recipes.Count(recipe => recipe.Repeat == TemplateRuleRepeat.ColorChunks
                && recipe.ColorRange == TemplateRuleColorRange.All);
            var secondary = branch.Recipes.Count(recipe => recipe.Repeat == TemplateRuleRepeat.ColorChunks
                && recipe.ColorRange == TemplateRuleColorRange.Secondary);
            var primary = branch.Recipes.Sum(recipe => recipe.ColorSlots.Count(slot => slot.Source == TemplateRuleColorSource.Primary));
            var singleSecondary = branch.Recipes.Sum(recipe => recipe.ColorSlots.Count(slot => slot.Source == TemplateRuleColorSource.SingleSecondary));
            bool CanHaveAtLeast(int count)
            {
                long minimum = Math.Max(count, branch.Condition.Minimum);
                if (branch.Condition.Parity != TemplateRuleColorParity.Any
                    && minimum % 2 != (branch.Condition.Parity == TemplateRuleColorParity.Odd ? 1 : 0)) minimum++;
                return minimum <= (branch.Condition.Maximum ?? int.MaxValue);
            }
            if (CanHaveAtLeast(1) && (all > 1 || all > 0 && primary > 0 || primary > 1)
                || CanHaveAtLeast(2) && (all > 0 && (secondary > 0 || singleSecondary > 0)
                    || secondary > 1 || singleSecondary > 1 || secondary > 0 && singleSecondary > 0))
                throw new TemplateRuleViolationException("TPL_RULE_COLOR_COVERAGE_INVALID");
        }
    }
    private static string SafeMetadata(string value) => UnsafeText().IsMatch(value) ? "模板" : value;
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\|https?://|sk-[A-Za-z0-9]{12,}|(?i:api[_ -]?key|authorization)\s*[:=])", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeText();
    [GeneratedRegex(@"@(\d+)(?:\s*(?:的)?\s*框\s*(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex AliasText();
}
