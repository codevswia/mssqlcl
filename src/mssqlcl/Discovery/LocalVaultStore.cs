using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MssqlCl.Models;

namespace MssqlCl.Discovery;

/// <summary>
/// Manages local mssqlcl connection profiles and Windows DPAPI-encrypted fallback secrets under ~/.mssqlcl.
/// </summary>
public sealed class LocalVaultStore
{
    private static readonly byte[] VaultEntropy = "mssqlcl-dpapi-vault-v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storeDir;
    private string ConnectionsFilePath => Path.Combine(_storeDir, "connections.json");
    private string VaultFilePath => Path.Combine(_storeDir, "vault.json");

    public LocalVaultStore(IdeSourceLocator locator)
    {
        _storeDir = locator.LocalStoreDir;
    }

    public IReadOnlyList<ConnectionProfile> LoadConnections()
    {
        if (!File.Exists(ConnectionsFilePath))
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(ConnectionsFilePath)) ?? [];
            foreach (var item in items)
            {
                item.Source = "local";
                item.SourceDataDir = null;
            }
            return items;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mssqlcl] Warning: failed to read {ConnectionsFilePath}: {ex.Message}");
            return [];
        }
    }

    public void SaveProfile(ConnectionProfile profile)
    {
        Directory.CreateDirectory(_storeDir);
        var list = LoadConnections().ToList();
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString();
        }
        profile.Password = null;
        profile.Source = "local";

        var existingIdx = list.FindIndex(p =>
            p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(profile.DisplayName, StringComparison.OrdinalIgnoreCase));

        if (existingIdx >= 0)
        {
            list[existingIdx] = profile;
        }
        else
        {
            list.Add(profile);
        }

        File.WriteAllText(ConnectionsFilePath, JsonSerializer.Serialize(list, JsonOptions));
    }

    public bool DeleteProfile(string idOrName)
    {
        if (!File.Exists(ConnectionsFilePath))
        {
            return false;
        }

        var list = LoadConnections().ToList();
        var removed = list.RemoveAll(p =>
            p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            File.WriteAllText(ConnectionsFilePath, JsonSerializer.Serialize(list, JsonOptions));
            DeleteSecret(idOrName);
        }
        return removed > 0;
    }

    public void SaveSecret(string key, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI vault is only supported on Windows.");
        }

        Directory.CreateDirectory(_storeDir);
        var map = LoadVaultMap();
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), VaultEntropy, DataProtectionScope.CurrentUser);
        map[key] = Convert.ToBase64String(protectedBytes);
        File.WriteAllText(VaultFilePath, JsonSerializer.Serialize(map, JsonOptions));
    }

    public string? TryGetSecret(params string[] candidateKeys)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(VaultFilePath))
        {
            return null;
        }

        var map = LoadVaultMap();
        foreach (var key in candidateKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !map.TryGetValue(key, out var b64))
            {
                continue;
            }

            try
            {
                var raw = Convert.FromBase64String(b64);
                var plain = ProtectedData.Unprotect(raw, VaultEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public void DeleteSecret(string key)
    {
        if (!File.Exists(VaultFilePath))
        {
            return;
        }

        var map = LoadVaultMap();
        if (map.Remove(key))
        {
            File.WriteAllText(VaultFilePath, JsonSerializer.Serialize(map, JsonOptions));
        }
    }

    private Dictionary<string, string> LoadVaultMap()
    {
        if (!File.Exists(VaultFilePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VaultFilePath));
            return loaded is not null
                ? new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
