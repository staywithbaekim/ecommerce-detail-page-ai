namespace Desktop.Infrastructure.Psd;

public sealed record PsdInspectionWorkerOptions(
    string ExecutablePath,
    TimeSpan Timeout,
    int MaxResponseBytes,
    IReadOnlyList<string>? Arguments = null)
{
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    public static PsdInspectionWorkerOptions ForInstalledApp() => new(
        Path.Combine(
            AppContext.BaseDirectory,
            "psd-inspection-worker",
            "psd-inspection-worker.exe"),
        TimeSpan.FromSeconds(30),
        DefaultMaxResponseBytes);
}
