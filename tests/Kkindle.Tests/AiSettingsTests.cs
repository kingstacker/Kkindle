using System.ComponentModel;
using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class AiSettingsTests
{
    [Theory]
    [InlineData("crypto")]
    [InlineData("win32")]
    [InlineData("format")]
    public async Task UnreadableSecretPreservesConnectionSettingsAndDoesNotOverwriteStoredData(string failure)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new AiSettingsStore(paths, new TestHelpers.PlaintextSecretProtector()).SaveAsync(new AiConnectionSettings
            {
                Provider = "custom", BaseUrl = "https://example.com/v1", Model = "test-model", ApiKey = "saved-key"
            });
            var settingsPath = Path.Combine(paths.Data, "ai-settings.json");
            var original = await File.ReadAllBytesAsync(settingsPath);

            var loaded = await new AiSettingsStore(paths, new UnreadableSecretProtector(failure)).LoadAsync();

            Assert.Equal("custom", loaded.Provider);
            Assert.Equal("https://example.com/v1", loaded.BaseUrl);
            Assert.Equal("test-model", loaded.Model);
            Assert.Empty(loaded.ApiKey);
            Assert.False(loaded.IsConfigured);
            Assert.Equal(original, await File.ReadAllBytesAsync(settingsPath));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private sealed class UnreadableSecretProtector(string failure) : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => throw (failure switch
        {
            "crypto" => new CryptographicException("Secret belongs to a different machine."),
            "win32" => new Win32Exception("The key store is unavailable."),
            _ => (Exception)new FormatException("Secret is malformed.")
        });
    }
}
