using Desktop.Application.Psd;

namespace Desktop.Infrastructure.Bridge;

public sealed partial class BridgeOutputReservation
{
    private RenderMediaEvidence? _verifiedMedia;
    internal async Task<RenderMediaEvidence> ValidateMediaAsync(string templateRelativePath,
        IPsdInspectionPort inspector, CancellationToken cancellationToken)
    {
        EnsureMatches(PlanDigest, Binding);
        if (_transferFaulted || !_uploads.ContainsKey("jpg")
            || IncludePsd && (!_uploads.TryGetValue(BridgeOutputReservation.InspectionPsdKind, out var psd) || !psd.Sealed || psd.Sha256 is null)
            || !IncludePsd && _uploads.Count != 1 || IncludePsd && _uploads.Count != 2
            || _uploads.Values.Any(upload => !upload.Sealed || upload.Sha256 is null)) throw Invalid();
        var receipts = _uploads.Select(pair => new RenderMediaFile(pair.Key, pair.Value.Length, pair.Value.Sha256!)).ToArray();
        var evidence = await new RenderMediaValidator(_paths, inspector).ValidateAsync(_planJson, templateRelativePath,
            Path.Combine(_stagingRelativePath, "jpg.part"), IncludePsd ? Path.Combine(_stagingRelativePath, BridgeOutputReservation.InspectionPsdKind + ".part") : null,
            receipts, cancellationToken).ConfigureAwait(false);
        EnsureMatches(PlanDigest, Binding);
        _verifiedMedia = evidence with { Files = Array.AsReadOnly(evidence.Files.ToArray()) };
        return evidence;
    }
}
