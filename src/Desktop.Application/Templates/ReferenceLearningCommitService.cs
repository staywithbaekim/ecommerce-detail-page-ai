using Desktop.Domain.Templates;

namespace Desktop.Application.Templates;

/// <summary>
/// Applies a reviewed learning result to an existing template draft and then
/// delegates the actual revision commit to the normal workspace transaction.
/// The projected binding marks its asset criterion as reference-only.  Future
/// render plans must therefore provide explicit target-batch selections.
/// </summary>
public sealed class ReferenceLearningCommitService(
    ITemplateRepository templates,
    ITemplateWorkspaceService workspace,
    IReferenceLearningSessionStore? sessions = null,
    TimeProvider? timeProvider = null) : IReferenceLearningCommitService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<ReferenceLearningCommitResult> CommitAsync(
        ReferenceLearningDocument document,
        TemplateDraftSession draftSession,
        CancellationToken cancellationToken = default) => await CommitAsync(
        [document],
        draftSession,
        cancellationToken).ConfigureAwait(false);

    public async Task<ReferenceLearningCommitResult> CommitAsync(
        IReadOnlyList<ReferenceLearningDocument> documents,
        TemplateDraftSession draftSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(draftSession);
        if (documents.Count == 0
            || documents.Select(document => document.PageId).Distinct(StringComparer.Ordinal).Count() != documents.Count)
        {
            return Failure(
                "TPL_LEARN_DOCUMENT_SET_INVALID",
                "学习页面集合为空或包含重复页面。",
                "刷新模板页面后重新提交。");
        }

        FrameBinding[] bindings;
        try
        {
            bindings = documents
                .SelectMany(ReferenceLearningRuleProjector.ProjectBindings)
                .ToArray();
        }
        catch (TemplateRuleViolationException error)
        {
            return Failure(
                error.Code,
                "学习候选尚未全部完成复核。",
                "接受、拒绝或转人工校准每个图片框后重试。");
        }
        if (bindings.Select(binding => binding.FrameId).Distinct(StringComparer.Ordinal).Count()
            != bindings.Length)
        {
            return Failure(
                "TPL_LEARN_BINDING_SET_INVALID",
                "学习页面集合包含重复的图片框身份。",
                "重新分析冲突页面后再提交。",
                retryable: true);
        }

        var revision = await templates.GetRevisionAsync(
                draftSession.DraftRevisionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (revision is null
            || !string.Equals(revision.GroupId, draftSession.GroupId, StringComparison.Ordinal))
        {
            return Failure("TPL_REVISION_NOT_FOUND", "模板草稿不存在。", "恢复模板草稿后重试。");
        }

        if (documents.Any(document =>
                revision.Pages.All(page => !string.Equals(page.PageId, document.PageId, StringComparison.Ordinal)))
            || bindings.Any(binding => documents.All(document =>
                !string.Equals(document.PageId, binding.PageId, StringComparison.Ordinal))))
        {
            return Failure("TPL_LEARN_PAGE_INVALID", "学习页面与模板草稿不一致。", "重新选择同一模板页面后重试。");
        }

        if (revision.State == TemplateRevisionState.Committed)
        {
            if (bindings.Any(binding => revision.FrameBindings.All(existing =>
                !string.Equals(existing.FrameId, binding.FrameId, StringComparison.Ordinal)
                || !Equivalent(existing, binding))))
            {
                return Failure(
                    "TPL_LEARN_COMMITTED_BINDINGS_MISMATCH",
                    "已提交修订与本次学习规则不一致。",
                    "从当前模板建立新的修订后重试。");
            }

            try
            {
                var alreadyCommitted = await workspace.CommitDraftAsync(
                    draftSession,
                    cancellationToken).ConfigureAwait(false);
                return await MarkCommittedAsync(documents, alreadyCommitted, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is TemplateRuleViolationException or IOException or UnauthorizedAccessException)
            {
                return Failure(ErrorCode(error), "已提交修订无法再次修改；请从新修订开始。", "开始新的模板修订。");
            }
        }

        if (revision.State != TemplateRevisionState.Draft)
        {
            return Failure("TPL_REVISION_IMMUTABLE", "模板修订当前不可写。", "恢复或新建模板修订后重试。");
        }

        var updated = revision;
        foreach (var binding in bindings)
        {
            var existing = updated.FrameBindings.SingleOrDefault(item =>
                string.Equals(item.FrameId, binding.FrameId, StringComparison.Ordinal));
            if (existing is null)
            {
                updated = updated.AddFrameBinding(binding);
                continue;
            }

            if (!Equivalent(existing, binding))
            {
                return Failure(
                    "TPL_LEARN_TARGET_BINDING_CONFLICT",
                    "模板草稿已有不同的图片框绑定。",
                    "撤销冲突绑定或恢复一个新的模板修订。");
            }
        }

        if (!ReferenceEquals(updated, revision))
        {
            try
            {
                await templates.SaveDraftRevisionAsync(updated, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is TemplateRuleViolationException or IOException or UnauthorizedAccessException)
            {
                return Failure(ErrorCode(error), "模板草稿保存失败，旧修订未改变。", "检查本地存储后重试。", true);
            }
        }

        try
        {
            var committed = await workspace.CommitDraftAsync(
                    draftSession,
                    cancellationToken)
                .ConfigureAwait(false);
            return await MarkCommittedAsync(documents, committed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is TemplateRuleViolationException or IOException or UnauthorizedAccessException)
        {
            // The workspace commit uses the existing revision CAS.  A failed
            // commit therefore leaves the prior current revision untouched;
            // the draft remains available for recovery.
            return Failure(
                ErrorCode(error),
                "模板规则提交失败，旧修订未覆盖。请恢复草稿后重试。",
                "检查模板草稿和本地存储后重试。",
                true);
        }
    }

    private async Task<ReferenceLearningCommitResult> MarkCommittedAsync(
        IReadOnlyList<ReferenceLearningDocument> documents,
        TemplateGroupDetails template,
        CancellationToken cancellationToken)
    {
        var committed = documents.Select(document => document with
        {
            State = ReferenceLearningState.Committed,
            UpdatedAt = clock.GetUtcNow(),
            BlockReason = null
        })
            .ToArray();
        if (sessions is not null)
        {
            try
            {
                foreach (var document in committed)
                {
                    await sessions.SaveAsync(document, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                return Failure(
                    "TPL_LEARN_COMMIT_CHECKPOINT_FAILED",
                    "模板修订已提交，但学习会话检查点写入失败。",
                    "恢复该学习会话并重试；重复提交不会覆盖旧修订。",
                    retryable: true);
            }
        }

        return ReferenceLearningCommitResult.Success(committed[0], template);
    }

    private static string ErrorCode(Exception error) => error is TemplateRuleViolationException violation
        ? violation.Code
        : "TPL_LEARN_STORAGE_UNAVAILABLE";

    private static bool Equivalent(FrameBinding left, FrameBinding right) =>
        string.Equals(left.PageId, right.PageId, StringComparison.Ordinal)
        && left.LayerPath.Segments.SequenceEqual(right.LayerPath.Segments)
        && string.Equals(
            left.SelectionRequirement.Criteria.SingleOrDefault(item => item.Key == "asset_id")?.Value,
            right.SelectionRequirement.Criteria.SingleOrDefault(item => item.Key == "asset_id")?.Value,
            StringComparison.Ordinal)
        && left.SelectionRequirement.Criteria.Any(item =>
            item.Key == "reference_only" && item.Value == "true")
        && left.LayoutIntent == right.LayoutIntent;

    private static ReferenceLearningCommitResult Failure(
        string code,
        string message,
        string userAction,
        bool retryable = false) => ReferenceLearningCommitResult.Failure(
        new ReferenceLearningError(code, message, userAction, retryable));
}
