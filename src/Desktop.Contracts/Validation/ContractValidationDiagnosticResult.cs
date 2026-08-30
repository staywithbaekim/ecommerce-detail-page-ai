namespace Desktop.Contracts.Validation;

/// <summary>
/// Validation result plus the first failure location/keyword.  The path and
/// keyword are structural metadata only; no instance values are retained.
/// </summary>
public sealed record ContractValidationDiagnosticResult(
    ContractValidationResult Validation,
    string? FirstFailureInstancePath,
    string? FirstFailureKeyword);
