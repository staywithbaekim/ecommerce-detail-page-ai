using System.Collections.Immutable;
using Desktop.Contracts.Ai;
using Desktop.Domain.Inputs;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

public sealed record AiRenderPreparationSnapshot(
    BatchDraft Batch,
    TemplateRevision TemplateRevision,
    ImmutableArray<AiAnalysisRequestV1> Requests,
    AiBatchAnalysisProjection Projection);
