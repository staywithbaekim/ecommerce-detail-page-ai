using System.Text.Json;
using Desktop.Application.Ai;
using Desktop.Application.Files;
using Desktop.Contracts.Ai;
using Desktop.Contracts.Validation;

namespace Desktop.Infrastructure.Ai;

public sealed class FileAiAnalysisResultStore : IAiAnalysisResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFileStore _files;
    private readonly IJsonContractValidator _validator;

    public FileAiAnalysisResultStore(IFileStore files, IJsonContractValidator validator)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task SaveAsync(string resultReference, AiAnalysisResultV1 result, CancellationToken cancellationToken)
    {
        ValidateReference(resultReference);
        ArgumentNullException.ThrowIfNull(result);
        var expectedRequestId = resultReference["results/".Length..];
        if (!string.Equals(expectedRequestId, result.RequestId, StringComparison.Ordinal))
            throw new InvalidDataException("AI_RESULT_REQUEST_REFERENCE_MISMATCH");
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var validation = _validator.Validate(ContractDocumentKind.AiAnalysis, json);
        if (!validation.IsValid) throw new InvalidDataException("AI_RESULT_CONTRACT_INVALID");
        await _files.WriteAtomicallyAsync(resultReference + ".json", JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiAnalysisResultV1?> LoadAsync(string resultReference, CancellationToken cancellationToken)
    {
        ValidateReference(resultReference);
        var bytes = await _files.ReadAsync(resultReference + ".json", cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            var json = JsonSerializer.Deserialize<AiAnalysisResultV1>(bytes, JsonOptions)
                ?? throw new JsonException();
            if (!_validator.Validate(ContractDocumentKind.AiAnalysis, JsonSerializer.Serialize(json, JsonOptions)).IsValid)
                throw new InvalidDataException("AI_RESULT_CONTRACT_INVALID");
            if (!string.Equals(resultReference, "results/" + json.RequestId, StringComparison.Ordinal))
                throw new InvalidDataException("AI_RESULT_REQUEST_REFERENCE_MISMATCH");
            return json;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException("AI_RESULT_RECORD_CORRUPT", exception);
        }
    }

    private static void ValidateReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("results/", StringComparison.Ordinal))
            throw new InvalidDataException("AI_RESULT_REFERENCE_INVALID");
        var id = value["results/".Length..];
        if (id.Length is < 1 or > 128 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidDataException("AI_RESULT_REFERENCE_INVALID");
    }
}
