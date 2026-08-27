using Desktop.Contracts.Bridge;
using Desktop.Contracts.Validation;

namespace Desktop.Application.Rendering;

public interface IRenderPlanValidator
{
    ContractValidationResult Validate(string renderPlanJson);
}

public sealed class RenderPlanValidator(IJsonContractValidator validator) : IRenderPlanValidator
{
    public ContractValidationResult Validate(string renderPlanJson) =>
        validator.Validate(ContractDocumentKind.RenderPlan, renderPlanJson);
}

public interface IBridgeClient
{
    Task<BridgeResult<BridgeSession>> HandshakeAsync(
        BridgeHandshakeRequest request,
        CancellationToken cancellationToken);

    Task<BridgeResult<BridgeExecution>> SubmitAsync(
        BridgeExecutionRequest request,
        CancellationToken cancellationToken);

    Task<BridgeResult<BridgeExecution>> QueryAsync(
        string executionId,
        string accessToken,
        CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(string accessToken, CancellationToken cancellationToken);
}
