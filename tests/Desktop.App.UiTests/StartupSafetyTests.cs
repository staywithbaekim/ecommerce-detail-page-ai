using System.Security.Cryptography;
using Desktop.App;
using Desktop.Application.Diagnostics;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.App.UiTests;

public sealed class StartupSafetyTests
{
    [Fact]
    public void ExplicitDataRootsCannotActivateDifferentDatabaseWindows()
    {
        using var roots = new TestRoots();
        var normal = global::Desktop.App.App.SingleInstanceIdentity(AppLaunchOptions.Default);
        var isolated = global::Desktop.App.App.SingleInstanceIdentity(new(false, roots.Isolated.RootDirectory));
        Assert.Equal("EcommerceDetailPage.DesktopV1", normal);
        Assert.Equal(normal, global::Desktop.App.App.SingleInstanceIdentity(new(false, DesktopDataOptions.ForCurrentUser().RootDirectory)));
        Assert.NotEqual(normal, isolated);
        Assert.Equal(isolated, global::Desktop.App.App.SingleInstanceIdentity(new(false, roots.Isolated.RootDirectory.ToUpperInvariant() + System.IO.Path.DirectorySeparatorChar)));
        Assert.NotEqual(isolated, global::Desktop.App.App.SingleInstanceIdentity(new(false, roots.Default.RootDirectory)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(roots.Path));
    }
    public static TheoryData<string[]> RejectedArguments { get; } = new()
    {
        new[] { "--data-root" },
        new[] { "--data-root=" },
        new[] { "--data-root=   " },
        new[] { "--data-root=\"\"" },
        new[] { "--data-root", "" },
        new[] { "--data-root", "--fixture=recovery" },
        new[] { "--data-root=relative" },
        new[] { "--data-root=C:relative" },
        new[] { "--data-root=\\rooted" },
        new[] { "--data-root=C:\\" },
        new[] { "--data-root=\\\\server\\share\\data" },
        new[] { "--data-root=\\\\?\\C:\\data" },
        new[] { "--data-root=C:\\data:stream" },
        new[] { "--data-root=C:\\bad*name" },
        new[] { "--data-root=C:\\bad\0name" },
        new[] { "--data-root=C:\\CON" },
        new[] { "--data-root=C:\\NUL.txt" },
        new[] { "--data-root=C:\\data." },
        new[] { "--data-root=C:\\data " },
        new[] { "--data-root=C:\\data\\..\\other" },
        new[] { "--data-root=\"C:\\data" },
        new[] { "--data-root=C:\\data\"" },
        new[] { "--data-rooot=C:\\data" },
        new[] { "--data-root", "C:\\data", "unexpected" },
        new[] { "--data-root=C:\\one", "--data-root=C:\\two" },
        new[] { "--data-root=C:\\one", "--data-root", "C:\\one" },
        new[] { "--unknown" },
        new[] { "" },
        new string[] { null! }
    };

    [Theory]
    [MemberData(nameof(RejectedArguments))]
    public async Task RejectedArgumentsCannotReachDefaultStoreOrDiagnostics(string[] arguments)
    {
        using var roots = new TestRoots();
        // An actual SQLite sentinel, not the user's default database. It deliberately
        // represents a pre-upgrade root; neither the sentinel nor its log may change.
        await roots.CreateDefaultSentinelAsync();
        var before = roots.Snapshot();
        DesktopDataOptions? selected = null;
        var defaultCalls = 0;
        var storeReached = false;
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            (_, selected) = global::Desktop.App.App.ResolveStartupOptions(arguments, () =>
            {
                defaultCalls++;
                return roots.Default;
            });
            storeReached = true;
            await InitializeAndRecordAsync(selected);
        });
        Assert.Null(selected);
        Assert.False(storeReached);
        Assert.Equal(0, defaultCalls);
        Assert.False(StartupDiagnostics.TryLogFailure(selected, "DESKTOP_STARTUP_FAILED", "launch_options", error));
        Assert.Equal(before, roots.Snapshot());
        await roots.AssertDefaultSentinelAsync();
    }

    [Fact]
    public void RejectionDoesNotCreateAnAbsentDefaultRoot()
    {
        using var roots = new TestRoots();
        var defaultCalls = 0;
        Assert.Throws<ArgumentException>(() => global::Desktop.App.App.ResolveStartupOptions(["--data-root="], () =>
        {
            defaultCalls++;
            Directory.CreateDirectory(roots.Default.RootDirectory);
            return roots.Default;
        }));
        Assert.False(StartupDiagnostics.TryLogFailure(null, "DESKTOP_STARTUP_FAILED", "launch_options", new ArgumentException()));
        Assert.Equal(0, defaultCalls);
        Assert.False(Directory.Exists(roots.Default.RootDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(roots.Path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CurrentBuildEnforcesIsolationArgumentSupport(bool separated, bool quoted)
    {
        using var roots = new TestRoots();
        await roots.CreateDefaultSentinelAsync();
        var before = roots.Snapshot();
        var value = quoted ? '"' + roots.Isolated.RootDirectory + '"' : roots.Isolated.RootDirectory;
        string[] arguments = separated ? ["--data-root", value] : ["--data-root=" + value];
        var defaultCalls = 0;
        DesktopDataOptions Default() { defaultCalls++; return roots.Default; }
#if DEBUG
        var (launch, selected) = global::Desktop.App.App.ResolveStartupOptions(arguments, Default);
        Assert.Equal(roots.Isolated.RootDirectory, launch.DataRoot);
        Assert.Equal(roots.Isolated, selected);
        Assert.Equal(before, roots.Snapshot()); // resolution is read-only, even for a valid root
        await InitializeAndRecordAsync(selected);
        Assert.True(StartupDiagnostics.TryLogFailure(selected, "ISOLATED_TEST", "store_initialization", new InvalidOperationException("sentinel")));
        Assert.True(File.Exists(selected.DatabasePath));
        Assert.True(File.Exists(System.IO.Path.Combine(selected.RootDirectory, StartupDiagnostics.DisplayLogPath)));
        await AssertInitializedAsync(selected);
        Assert.Equal(before, roots.SnapshotDefault());
#else
        var error = Assert.Throws<ArgumentException>(() => global::Desktop.App.App.ResolveStartupOptions(arguments, Default));
        Assert.Contains("DESKTOP_DATA_ROOT_UNSUPPORTED", error.Message, StringComparison.Ordinal);
        Assert.False(StartupDiagnostics.TryLogFailure(null, "DESKTOP_STARTUP_FAILED", "launch_options", error));
        Assert.Equal(before, roots.Snapshot());
        Assert.False(Directory.Exists(roots.Isolated.RootDirectory));
#endif
        Assert.Equal(0, defaultCalls);
        await roots.AssertDefaultSentinelAsync();
    }

    [Fact]
    public async Task NoArgumentsStillSelectsNormalProductRoot()
    {
        using var roots = new TestRoots();
        var calls = 0;
        var (launch, selected) = global::Desktop.App.App.ResolveStartupOptions([], () => { calls++; return roots.Default; });
        Assert.Equal(AppLaunchOptions.Default, launch);
        Assert.Equal(roots.Default, selected);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(roots.Default.RootDirectory));
        await InitializeAndRecordAsync(selected);
        await AssertInitializedAsync(selected);
        Assert.False(Directory.Exists(roots.Isolated.RootDirectory));
    }

    [Fact]
    public void ExistingFileCannotBeAnIsolationDirectory()
    {
        using var roots = new TestRoots();
        var path = System.IO.Path.Combine(roots.Path, "not-a-directory");
        File.WriteAllText(path, "sentinel");
        var before = roots.Snapshot();
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-root=" + path]));
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--data-root=" + System.IO.Path.Combine(path, "child")]));
        Assert.Equal(before, roots.Snapshot());
    }

    [Fact]
    public void RecoveryFixtureRemainsDebugOnly()
    {
#if DEBUG
        Assert.True(AppLaunchOptions.Parse(["--fixture=recovery"]).ShowRecoveryFixture);
#else
        Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--fixture=recovery"]));
#endif
    }

    private static async Task InitializeAndRecordAsync(DesktopDataOptions options)
    {
        await using var store = new SqliteDesktopStore(options);
        await store.InitializeAsync();
        await store.RecordStartAsync(new AppInstanceRecord("startup-sentinel", DateTimeOffset.UnixEpoch, null, false), default);
        await store.RecordSafeExitAsync("startup-sentinel", DateTimeOffset.UnixEpoch.AddSeconds(1), default);
    }

    private static async Task AssertInitializedAsync(DesktopDataOptions options)
    {
        await using var connection = Open(options);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT max(version) FROM schema_migrations";
        Assert.Equal(29L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM app_instance WHERE instance_id='startup-sentinel' AND safe_exit=1";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static SqliteConnection Open(DesktopDataOptions options) => new(new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath, Pooling = false
    }.ToString());

    private sealed class TestRoots : IDisposable
    {
        internal TestRoots()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktop-startup-safety", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Default = new(System.IO.Path.Combine(Path, "default-sentinel"));
            Isolated = new(System.IO.Path.Combine(Path, "中文 隔离目录", "data root"));
        }
        internal string Path { get; }
        internal DesktopDataOptions Default { get; }
        internal DesktopDataOptions Isolated { get; }

        internal async Task CreateDefaultSentinelAsync()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Default.DatabasePath)!);
            await using var connection = Open(Default);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, checksum TEXT);
                INSERT INTO schema_migrations VALUES(21,'sentinel-not-a-production-database');
                CREATE TABLE app_instance(instance_id TEXT PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
            var log = System.IO.Path.Combine(Default.RootDirectory, StartupDiagnostics.DisplayLogPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
            await File.WriteAllTextAsync(log, "diagnostic sentinel");
        }

        internal async Task AssertDefaultSentinelAsync()
        {
            await using var connection = Open(Default);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT max(version) FROM schema_migrations";
            Assert.Equal(21L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM app_instance";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        internal string[] Snapshot() => SnapshotFiles(Path);
        internal string[] SnapshotDefault() => SnapshotFiles(Default.RootDirectory);
        private string[] SnapshotFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => System.IO.Path.GetRelativePath(Path, file) + ":"
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) + ":" + File.GetLastWriteTimeUtc(file).Ticks).ToArray();
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
