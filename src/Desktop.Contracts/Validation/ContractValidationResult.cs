namespace Desktop.Contracts.Validation;

public sealed record ContractValidationResult(bool IsValid, IReadOnlyList<ContractError> Errors)
{
    public static ContractValidationResult Success { get; } = new(true, []);

    public static ContractValidationResult Failure(ContractError error) => new(false, [error]);
}
