namespace Desktop.Infrastructure.Persistence;

public sealed record DesktopDataOptions(string RootDirectory)
{
    public static DesktopDataOptions ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new DesktopDataOptions(Path.Combine(localAppData, "EcommerceDetailPage", "DesktopV1"));
    }

    public string DatabasePath => Path.Combine(RootDirectory, "state", "desktop-v1.db");
}
