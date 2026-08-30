namespace Desktop.Contracts.Validation;

/// <summary>
/// Optional extension for validators that can expose a bounded, sanitized
/// description of the first contract failure.  Callers must continue to use
/// <see cref="IJsonContractValidator.Validate"/> for normal behavior.
/// </summary>
public interface IJsonContractDiagnosticValidator : IJsonContractValidator
{
    ContractValidationDiagnosticResult ValidateWithDiagnostic(
        ContractDocumentKind kind,
        string json);
}
