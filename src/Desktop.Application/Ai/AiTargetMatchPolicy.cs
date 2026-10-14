using Desktop.Contracts.Ai;
using Desktop.Domain.Templates;

namespace Desktop.Application.Ai;

public static class AiTargetMatchEvidence
{
    public const string Exact = "exact";
    public const string SafeSimilar = "safe_similar";
    public const string MaterialDifference = "material_difference";
    public const string Unsafe = "unsafe";

    public static bool IsFitClass(string value) => value is
        Exact or SafeSimilar or MaterialDifference or Unsafe;
}

internal enum AiTargetRequirementAssessment
{
    Ready = 0,
    ReviewRequired,
    BusinessError,
    InvalidResult
}

internal static class AiTargetMatchPolicy
{
    internal const decimal ExactGreenConfidence = 0.85m;
    internal const decimal SafeSimilarGreenConfidence = 0.90m;
    internal const decimal ReviewConfidence = 0.60m;

    public static AiTargetRequirementAssessment Assess(
        FrameBinding binding,
        IReadOnlyList<AiRequirementMatchV1> rankedMatches,
        bool manuallyLocked)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(rankedMatches);
        if (manuallyLocked)
        {
            return AiTargetRequirementAssessment.Ready;
        }

        if (!AiAnalysisInputProjector.IsLearnedReferenceBinding(binding))
        {
            var legacyTop = rankedMatches.OrderBy(match => match.Rank)
                .ThenBy(match => match.AssetId, StringComparer.Ordinal)
                .FirstOrDefault();
            return legacyTop is null
                || legacyTop.EvidenceCodes.Contains(AiTargetMatchEvidence.Unsafe, StringComparer.Ordinal)
                ? AiTargetRequirementAssessment.BusinessError
                : legacyTop.Confidence < 0.8m
                    ? AiTargetRequirementAssessment.ReviewRequired
                    : AiTargetRequirementAssessment.Ready;
        }

        if (rankedMatches.Count == 0)
        {
            return AiTargetRequirementAssessment.BusinessError;
        }

        foreach (var match in rankedMatches)
        {
            if (match.EvidenceCodes.Count(code => AiTargetMatchEvidence.IsFitClass(code)) != 1)
            {
                return AiTargetRequirementAssessment.InvalidResult;
            }
        }

        var ordered = rankedMatches.OrderBy(match => match.Rank)
            .ThenBy(match => match.AssetId, StringComparer.Ordinal)
            .ToArray();
        var top = ordered[0];
        var fitClass = top.EvidenceCodes.Single(AiTargetMatchEvidence.IsFitClass);
        if (fitClass == AiTargetMatchEvidence.Unsafe)
        {
            return AiTargetRequirementAssessment.BusinessError;
        }

        // An exact classification is already the model's semantic decision.
        // A second photo with the same score is not a semantic conflict: it
        // is a valid duplicate/alternative source photo and the Top-1 result
        // remains deterministic.  Do not turn that normal situation yellow
        // merely because the provider returned a tied score.  The same rule
        // applies to a high-confidence safe-similar result: a numerical gap
        // is not semantic evidence by itself.  Explicit conflict warnings
        // are evaluated at the page boundary by the workflow.
        if ((fitClass == AiTargetMatchEvidence.Exact
                && top.Confidence >= ExactGreenConfidence)
            || (fitClass == AiTargetMatchEvidence.SafeSimilar
                && top.Confidence >= SafeSimilarGreenConfidence))
        {
            return AiTargetRequirementAssessment.Ready;
        }

        if (fitClass is AiTargetMatchEvidence.Exact or AiTargetMatchEvidence.SafeSimilar)
        {
            return AiTargetRequirementAssessment.ReviewRequired;
        }

        return top.Confidence >= ReviewConfidence
            && fitClass == AiTargetMatchEvidence.MaterialDifference
            ? AiTargetRequirementAssessment.ReviewRequired
            : AiTargetRequirementAssessment.BusinessError;
    }
}
