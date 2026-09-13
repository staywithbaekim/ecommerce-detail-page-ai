using System.Text.Json.Serialization;

namespace Desktop.Contracts.Ai;

public static class ReferenceLearningAiProtocol
{
    public const string RequestSchemaVersion = "reference-learning-ai/v2";
    public const string ResultSchemaVersion = "reference-learning-ai-result/v2";
    // v5 uploads only PSD-referenced assets, binds each asset label directly
    // to its adjacent image, and keeps layer/layout metadata out of the
    // reusable visual-category phrase. Keep this version in the request,
    // result metadata, and cache fingerprint so older misbound or narrative
    // results can never satisfy the corrected prompt contract.
    public const string SemanticPromptVersion = "reference-learning-semantic-prompt/v5";
}

public static class ReferenceLearningAiOperation
{
    public const string GenerateSelectionRequirements = "generate_selection_requirements";
    public static bool IsSupported(string value) =>
        value == GenerateSelectionRequirements;
}

public sealed record ReferenceLearningAiRequestV2(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("document_kind")] string DocumentKind,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("model_config_version")] string ModelConfigVersion,
    [property: JsonPropertyName("prompt_contract_version")] string PromptContractVersion,
    [property: JsonPropertyName("response_schema_version")] string ResponseSchemaVersion,
    [property: JsonPropertyName("compression_profile_version")] string CompressionProfileVersion,
    [property: JsonPropertyName("consent_id")] string ConsentId,
    [property: JsonPropertyName("reference_set_digest")] string ReferenceSetDigest,
    [property: JsonPropertyName("assets")] IReadOnlyList<AiAssetInputV1> Assets,
    [property: JsonPropertyName("frames")] IReadOnlyList<ReferenceLearningFramePromptV2> Frames);

public sealed record ReferenceLearningFramePromptV2(
    [property: JsonPropertyName("frame_id")] string FrameId,
    [property: JsonPropertyName("page_id")] string PageId,
    [property: JsonPropertyName("reference_asset_id")] string ReferenceAssetId,
    [property: JsonPropertyName("page_context")] string PageContext,
    [property: JsonPropertyName("frame_context")] string FrameContext,
    [property: JsonPropertyName("layout_intent")] string LayoutIntent);

public sealed record ReferenceLearningAiResultV2(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("document_kind")] string DocumentKind,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("model_config_version")] string ModelConfigVersion,
    [property: JsonPropertyName("frame_results")] IReadOnlyList<ReferenceLearningFrameResultV2> FrameResults,
    [property: JsonPropertyName("warnings")] IReadOnlyList<AiWarningV1> Warnings,
    [property: JsonPropertyName("usage")] AiUsageSummaryV1 Usage);

public sealed record ReferenceLearningFrameResultV2(
    [property: JsonPropertyName("frame_id")] string FrameId,
    [property: JsonPropertyName("selection_requirement")] string SelectionRequirement,
    [property: JsonPropertyName("positive_evidence")] IReadOnlyList<string> PositiveEvidence,
    [property: JsonPropertyName("exclusion_evidence")] IReadOnlyList<string> ExclusionEvidence,
    [property: JsonPropertyName("confidence")] decimal Confidence);
