using Desktop.Application.Ai;
using Desktop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Desktop.Infrastructure.Tests;

public sealed class AiConsentStoreTests
{
    [Fact]
    public async Task GrantAndRevokePersistAcrossStoreReopenAndKeepHistory()
    {
        using var directory = new TestDirectory();
        var options = new DesktopDataOptions(directory.Path);
        AiUploadConsentRecord granted;
        await using (var first = new SqliteDesktopStore(options))
        {
            await first.InitializeAsync();
            granted = await first.GrantAsync(
                AiConsentScopes.AnalysisCopy,
                AiConsentPolicy.Version,
                CancellationToken.None);
            var revoked = await first.RevokeAsync(
                AiConsentScopes.AnalysisCopy,
                CancellationToken.None);

            Assert.Equal(granted.ConsentId, revoked.ConsentId);
            Assert.Equal(AiUploadConsentState.Revoked, revoked.State);
        }

        await using var reopened = new SqliteDesktopStore(options);
        await reopened.InitializeAsync();
        var current = await reopened.GetConsentAsync(
            AiConsentScopes.AnalysisCopy,
            CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal(granted.ConsentId, current!.ConsentId);
        Assert.Equal(AiUploadConsentState.Revoked, current.State);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ai_upload_consent_events WHERE scope = $scope;";
        count.Parameters.AddWithValue("$scope", AiConsentScopes.AnalysisCopy);
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MissingConsentIsDeniedByDefault()
    {
        using var directory = new TestDirectory();
        await using var store = new SqliteDesktopStore(new DesktopDataOptions(directory.Path));
        await store.InitializeAsync();

        Assert.Null(await store.GetConsentAsync(AiConsentScopes.AnalysisCopy, CancellationToken.None));
    }
}
