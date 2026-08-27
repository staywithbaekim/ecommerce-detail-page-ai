namespace Desktop.Contracts.Validation;

public interface IJsonContractValidator
{
    ContractValidationResult Validate(ContractDocumentKind kind, string json);
}
