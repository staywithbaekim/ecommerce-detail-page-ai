using System.Collections.Immutable;
using Desktop.Application.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Templates;

public static class TemplateRuleAiProtocol
{
    public const string RequestVersion = "template-rule-ai-request/v1";
    public const string ResultVersion = "template-rule-ai-result/v1";
    public const string PromptVersion = "template-rule-prompt/v3";
    public const string ModelVersion = ReferenceLearningAiService.ModelConfigVersion;
}
public sealed record TemplateRuleTargetReference(string PageId, string? FrameId);
public sealed record TemplateRuleInterpretationCommand(string CommandId, long ExpectedDraftVersion, string Text,
    ImmutableArray<TemplateRuleTargetReference> References);
public sealed record TemplateRuleAiPage(string PageId, string Name, bool IsSizeTable);
public sealed record TemplateRuleAiRequest(string SchemaVersion, string RequestId, string PromptVersion, string ModelVersion,
    long DraftVersion, string RuleDigest, string Text, ImmutableArray<TemplateRuleAiPage> Pages,
    ImmutableArray<TemplateRuleFrameOverview> Frames, ImmutableArray<TemplateReferenceAliasView> Aliases,
    TemplateRuleSetView? Rules, ImmutableArray<TemplateRuleTargetReference> AllowedTargets);
public sealed record TemplateRuleAiResponse(string SchemaVersion, ImmutableArray<TemplateFrameRuleEdit> Frames,
    ImmutableArray<TemplateBranchRuleEdit> Branches, string Summary, string? Clarification);
public sealed record TemplateRuleAiExecution(AiProviderOutcome Outcome, TemplateRuleAiResponse? Response,
    AiProviderUsage? Usage, ContractError? Error);
public sealed record TemplateRuleInterpretationResult(string RequestId, string? AppliedCommandId, string Message,
    TemplateRuleDraftView Draft, bool RequiresClarification);
public interface ITemplateRuleInterpreter
{
    Task<TemplateRuleInterpretationResult> InterpretAsync(TemplateDraftSession session,
        TemplateRuleInterpretationCommand command, CancellationToken cancellationToken);
}
