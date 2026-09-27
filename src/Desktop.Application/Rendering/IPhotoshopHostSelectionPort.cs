namespace Desktop.Application.Rendering;

public enum PhotoshopHostSelectionStatus { NotSelected, Valid, Missing, Changed, InvalidRecord, Unavailable }

/// <summary>Validated local selection, not an authenticated connection or production admission.</summary>
public sealed record PhotoshopHostSelectionSnapshot(
    PhotoshopHostSelectionStatus Status,
    string? ExecutablePath = null,
    string? NumericFileVersion = null,
    DateTimeOffset? SelectedAt = null,
    bool IsInUse = false);

/// <summary>Explicit local preference only. Neither reading nor saving starts Photoshop or a bridge session.</summary>
public interface IPhotoshopHostSelectionPort
{
    Task<PhotoshopHostSelectionSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<PhotoshopHostSelectionSnapshot> SelectAsync(string executablePath, CancellationToken cancellationToken);
}
