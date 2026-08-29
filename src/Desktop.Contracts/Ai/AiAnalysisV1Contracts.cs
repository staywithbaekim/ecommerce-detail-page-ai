using System.Text.Json.Serialization;

namespace Desktop.Contracts.Ai;

public static class AiAnalysisProtocol
{
    public const string RequestSchemaVersion = "ai-analysis/v1";
    public const string ResultSchemaVersion = "ai-analysis-result/v1";
}

public static class AiAnalysisKind
{
    public const string ProductJointUnderstanding = "product_joint_understanding";
    public const string SelectedAssetGeometry = "selected_asset_geometry";
    public const string SizeTableExtraction = "size_table_extraction";

    public static bool IsSupported(string value) => value is
        ProductJointUnderstanding or SelectedAssetGeometry or SizeTableExtraction;
}

public sealed record AiAnalysisRequestV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("document_kind")] string DocumentKind,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("product_id")] string ProductId,
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("style_id")] string StyleId,
    [property: JsonPropertyName("template_snapshot_id")] string TemplateSnapshotId,
    [property: JsonPropertyName("template_revision_id")] string TemplateRevisionId,
    [property: JsonPropertyName("analysis_kind")] string AnalysisKind,
    [property: JsonPropertyName("model_config_version")] string ModelConfigVersion,
    [property: JsonPropertyName("prompt_contract_version")] string PromptContractVersion,
    [property: JsonPropertyName("response_schema_version")] string ResponseSchemaVersion,
    [property: JsonPropertyName("compression_profile_version")] string CompressionProfileVersion,
    [property: JsonPropertyName("consent_id")] string ConsentId,
    [property: JsonPropertyName("requirements_digest")] string RequirementsDigest,
    [property: JsonPropertyName("assets")] IReadOnlyList<AiAssetInputV1> Assets,
    [property: JsonPropertyName("requirements")] IReadOnlyList<AiRequirementInputV1> Requirements);

public sealed record AiAssetInputV1(
    [property: JsonPropertyName("asset_id")] string AssetId,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("pixel_width")] int PixelWidth,
    [property: JsonPropertyName("pixel_height")] int PixelHeight,
    [property: JsonPropertyName("analysis_copy_ref")] string AnalysisCopyReference);

public sealed record AiRequirementInputV1(
    [property: JsonPropertyName("requirement_id")] string RequirementId,
    [property: JsonPropertyName("selection_requirement")] string SelectionRequirement,
    [property: JsonPropertyName("layout_intent")] string LayoutIntent,
    [property: JsonPropertyName("required")] bool Required);

public sealed record AiAnalysisResultV1(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("document_kind")] string DocumentKind,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("provider_request_ref")] string? ProviderRequestReference,
    [property: JsonPropertyName("model_config_version")] string ModelConfigVersion,
    [property: JsonPropertyName("requirement_matches")] IReadOnlyList<AiRequirementMatchV1> RequirementMatches,
    [property: JsonPropertyName("variant_groups")] IReadOnlyList<AiVariantGroupV1> VariantGroups,
    [property: JsonPropertyName("subject_geometry")] IReadOnlyList<AiSubjectGeometryV1> SubjectGeometry,
    [property: JsonPropertyName("size_table_candidate")] AiSizeTableCandidateV1? SizeTableCandidate,
    [property: JsonPropertyName("warnings")] IReadOnlyList<AiWarningV1> Warnings,
    [property: JsonPropertyName("usage")] AiUsageSummaryV1 Usage);

public sealed record AiRequirementMatchV1(
    [property: JsonPropertyName("requirement_id")] string RequirementId,
    [property: JsonPropertyName("asset_id")] string AssetId,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("evidence_codes")] IReadOnlyList<string> EvidenceCodes);

public sealed record AiVariantGroupV1(
    [property: JsonPropertyName("variant_id")] string VariantId,
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("asset_ids")] IReadOnlyList<string> AssetIds,
    [property: JsonPropertyName("is_primary_suggestion")] bool IsPrimarySuggestion);

public sealed record AiSubjectGeometryV1(
    [property: JsonPropertyName("asset_id")] string AssetId,
    [property: JsonPropertyName("left")] decimal Left,
    [property: JsonPropertyName("top")] decimal Top,
    [property: JsonPropertyName("right")] decimal Right,
    [property: JsonPropertyName("bottom")] decimal Bottom,
    [property: JsonPropertyName("confidence")] decimal Confidence);

public sealed record AiSizeTableCandidateV1(
    [property: JsonPropertyName("source_asset_id")] string SourceAssetId,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record AiWarningV1(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("message_summary")] string MessageSummary);

public sealed record AiUsageSummaryV1(
    [property: JsonPropertyName("usage_event_id")] string UsageEventId,
    [property: JsonPropertyName("input_units")] int InputUnits,
    [property: JsonPropertyName("output_units")] int OutputUnits,
    [property: JsonPropertyName("request_count")] int RequestCount,
    [property: JsonPropertyName("provider_cost_amount")] decimal? ProviderCostAmount,
    [property: JsonPropertyName("provider_cost_currency")] string? ProviderCostCurrency);
