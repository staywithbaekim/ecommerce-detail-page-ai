using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Desktop.Contracts.Validation;

public sealed class JsonSchemaContractValidator : IJsonContractValidator
{
    private static readonly Lazy<Dictionary<ContractDocumentKind, JsonSchema>> SchemaCache = new(CreateSchemas);
    private readonly Dictionary<ContractDocumentKind, JsonSchema> _schemas;

    public JsonSchemaContractValidator()
    {
        _schemas = SchemaCache.Value;
    }

    public ContractValidationResult Validate(ContractDocumentKind kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure("CONTRACT_EMPTY", "请选择有效的合同数据后重试。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var instance = document.RootElement;

            var result = _schemas[kind].Evaluate(instance, new EvaluationOptions
            {
                OutputFormat = OutputFormat.Flag,
                RequireFormatValidation = true
            });

            return result.IsValid
                ? ContractValidationResult.Success
                : Failure("CONTRACT_SCHEMA_REJECTED", "合同结构不符合当前协议，请修正后重试。");
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException)
        {
            return Failure("CONTRACT_INVALID_JSON", "合同数据无法解析，请修正后重试。");
        }
    }

    private static JsonSchema LoadSchema(string fileName)
    {
        var assembly = typeof(JsonSchemaContractValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("嵌入式合同 Schema 不可用。");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static Dictionary<ContractDocumentKind, JsonSchema> CreateSchemas() => new()
    {
        [ContractDocumentKind.RenderPlan] = LoadSchema("render-plan-v1.schema.json"),
        [ContractDocumentKind.BridgeEnvelope] = LoadSchema("bridge-v1.schema.json"),
        [ContractDocumentKind.TemplateRevisionManifest] = LoadSchema("template-revision-v1.schema.json"),
        [ContractDocumentKind.TemplatePackageManifest] = LoadSchema("template-package-v1.schema.json"),
        [ContractDocumentKind.AssetManifest] = LoadSchema("asset-manifest-v1.schema.json"),
        [ContractDocumentKind.AiAnalysis] = LoadSchema("ai-analysis-v1.schema.json")
    };

    private static ContractValidationResult Failure(string code, string userAction) =>
        ContractValidationResult.Failure(new ContractError(
            code,
            Retryable: false,
            Scope: "contract",
            UserAction: userAction));
}
