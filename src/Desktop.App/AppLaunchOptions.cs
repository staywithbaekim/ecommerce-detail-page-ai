namespace Desktop.App;

public sealed record AppLaunchOptions(bool ShowRecoveryFixture, string? DataRoot)
{
    public static AppLaunchOptions Default { get; } = new(false, null);

    public static AppLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var fixture = false;
        string? dataRoot = null;
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--fixture=recovery", StringComparison.OrdinalIgnoreCase))
            {
                fixture = true;
            }
            else if (argument.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase))
            {
                dataRoot = argument["--data-root=".Length..].Trim('"');
            }
        }

        return new AppLaunchOptions(fixture, dataRoot);
    }
}
