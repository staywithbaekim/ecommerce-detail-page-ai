using System.Security.Cryptography;
using Desktop.Infrastructure.Ai;
using Desktop.Infrastructure.Bridge;

namespace Desktop.Infrastructure.Tests;

public sealed class BridgePairingStoreTests
{
    [Fact]
    public async Task WindowsProtectedStoreRoundTripsAndBacksUpBeforeRotation()
    {
        var target = "RENDER-01-PAIR-TEST-" + Guid.NewGuid().ToString("N");
        var store = new WindowsBridgePairingStore(target);
        var old = RandomNumberGenerator.GetBytes(32);
        var next = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.Null(store.Read());
            store.Save(old);
            Assert.True(SameAndClear(old, store.Read()));
            store.Save(next);
            Assert.True(SameAndClear(next, store.Read()));
            Assert.True(SameAndClear(old, new WindowsBridgePairingStore(target + ".previous").Read()));
            Assert.Throws<ArgumentException>(() => store.Save(new byte[31]));
            Assert.True(SameAndClear(next, store.Read()));
            // A malformed stored credential is never accepted as pairing material.
            await new WindowsCredentialSource(target).SaveAsync("synthetic-invalid-value", default);
            var error = Assert.Throws<InvalidOperationException>(() => store.Read());
            Assert.Equal("BRIDGE_PAIRING_STORAGE_UNAVAILABLE", error.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(old);
            CryptographicOperations.ZeroMemory(next);
            await new WindowsCredentialSource(target).RemoveAsync(default);
            await new WindowsCredentialSource(target + ".previous").RemoveAsync(default);
        }
    }

    private static bool SameAndClear(byte[] expected, byte[]? actual)
    {
        try { return actual is not null && CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally { if (actual is not null) CryptographicOperations.ZeroMemory(actual); }
    }
}
