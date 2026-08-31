using Desktop.Application.Rendering;

namespace Desktop.Infrastructure.Bridge;

/// <summary>
/// AI-01 deliberately stops before the formal Photoshop adapter. Production
/// composition therefore reports an explicit unavailable boundary instead of
/// submitting to the validation mock or pretending that an output exists.
/// RENDER-01 will replace this port after the approved bridge takeover is
/// implemented and verified.
/// </summary>
public sealed class UnavailablePhotoshopAvailabilityPort : IPhotoshopAvailabilityPort
{
    public Task<PhotoshopAvailability> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PhotoshopAvailability(
            IsAvailable: false,
            ReasonCode: "PHOTOSHOP_FORMAL_ADAPTER_NOT_AVAILABLE"));
    }
}
