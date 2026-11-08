using Kkindle.AccountServer;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed class AccountSyncStorageTests
{
    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("folder/../../outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("C:/outside.bin")]
    [InlineData("C:outside.bin")]
    [InlineData("folder\\..\\outside.bin")]
    public async Task StorageRejectsUnnormalizedPathsBeforeCreatingFiles(string key)
    {
        var directory = Path.Combine(Path.GetTempPath(), "kkindle-path-tests-" + Guid.NewGuid().ToString("N"));
        var storage = new AccountSyncStorage(directory);
        using var input = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<AccountRequestException>(() => storage.PutAsync(Guid.NewGuid(), key, input, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.False(Directory.Exists(directory));
    }
}
