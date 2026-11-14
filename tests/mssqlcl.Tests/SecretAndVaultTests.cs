using System.Security.Cryptography;
using System.Text;
using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Tests;

public sealed class SecretAndVaultTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mssqlcl_vault_{Guid.NewGuid():N}");

    [Fact]
    public void DecryptV10Buffer_RoundTripsAes256GcmPayload()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes("S3cr3t!Pass#2026");
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(masterKey, 16))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        var v10 = new byte[3 + nonce.Length + cipher.Length + tag.Length];
        Encoding.ASCII.GetBytes("v10").CopyTo(v10, 0);
        nonce.CopyTo(v10, 3);
        cipher.CopyTo(v10, 15);
        tag.CopyTo(v10, 15 + cipher.Length);

        var json = $$"""{"type":"Buffer","data":[{{string.Join(",", v10)}}]}""";
        var parsed = IdeSecretReader.ParseElectronBufferJson(json);

        Assert.NotNull(parsed);
        var decrypted = IdeSecretReader.DecryptV10Buffer(masterKey, parsed);
        Assert.Equal("S3cr3t!Pass#2026", decrypted);
    }

    [Fact]
    public void LocalVaultStore_SavesAndResolvesDpapiSecretOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var locator = new IdeSourceLocator(new Dictionary<string, string>(), _tempRoot);
        var vault = new LocalVaultStore(locator);
        var profile = new ConnectionProfile
        {
            Id = "test-id-1",
            ProfileName = "TestLocal",
            Server = "localhost",
            Database = "master",
            User = "sa",
            AuthenticationType = "SqlLogin",
            SavePassword = true
        };

        vault.SaveProfile(profile);
        vault.SaveSecret(profile.Id, "LocalVaultPass!");

        var resolver = new CredentialResolver(new IdeSecretReader(), vault);
        var resolution = resolver.Resolve(profile);

        Assert.True(resolution.Success);
        Assert.Equal("LocalVaultPass!", resolution.Password);
        Assert.Equal("LocalDpapiVault", resolution.Source);
        Assert.True(vault.DeleteProfile("TestLocal"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
