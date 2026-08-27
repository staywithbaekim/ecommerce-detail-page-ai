namespace Desktop.Contracts.Validation;

public sealed record ContractError(
    string Code,
    bool Retryable,
    string Scope,
    string UserAction);
