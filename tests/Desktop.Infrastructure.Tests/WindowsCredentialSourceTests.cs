using Desktop.Application.Ai;
using Desktop.Infrastructure.Ai;
using Xunit.Sdk;

namespace Desktop.Infrastructure.Tests;

public sealed class WindowsCredentialSourceTests
{
    [Fact]
    public async Task CredentialManagerSupportsSaveReplaceStatusAndRemoveWithoutReturningFullKeyInStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Windows Credential Manager test requires Windows.");
        }

        var target = "AI-01-SET-TEST-" + Guid.NewGuid().ToString("N");
        var store = new WindowsCredentialSource(target);
        try
        {
            await store.RemoveAsync(CancellationToken.None);
            Assert.Equal(AiCredentialState.NotConfigured, (await store.GetStatusAsync(CancellationToken.None)).State);

            await store.SaveAsync("ark-test-secret-9876", CancellationToken.None);
            var first = await store.GetStatusAsync(CancellationToken.None);
            Assert.Equal(AiCredentialState.Configured, first.State);
            Assert.Equal("••••9876", first.MaskedSuffix);
            Assert.DoesNotContain("ark-test-secret", first.MaskedSuffix, StringComparison.Ordinal);
            Assert.Equal("ark-test-secret-9876", await store.GetCredentialAsync(CancellationToken.None));

            await store.SaveAsync("ark-replaced-4321", CancellationToken.None);
            var replaced = await store.GetStatusAsync(CancellationToken.None);
            Assert.Equal(AiCredentialState.Configured, replaced.State);
            Assert.Equal("••••4321", replaced.MaskedSuffix);
            Assert.Equal("ark-replaced-4321", await store.GetCredentialAsync(CancellationToken.None));

            await store.RemoveAsync(CancellationToken.None);
            var removed = await store.GetStatusAsync(CancellationToken.None);
            Assert.Equal(AiCredentialState.NotConfigured, removed.State);
            Assert.Null(await store.GetCredentialAsync(CancellationToken.None));
        }
        finally
        {
            try
            {
                await store.RemoveAsync(CancellationToken.None);
            }
            catch
            {
                // Cleanup must not hide the assertion failure.
            }
        }
    }
}
