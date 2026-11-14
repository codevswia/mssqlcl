using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MssqlCl.Models;

namespace MssqlCl.Discovery;

/// <summary>
/// Reads and decrypts ms-mssql.mssql passwords stored in VS Code / Antigravity IDE Electron SecretStorage.
/// </summary>
public sealed class IdeSecretReader
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] V10Prefix = "v10"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>
    /// Attempts to read the saved SqlLogin password for <paramref name="profile"/> from its IDE state.vscdb.
    /// </summary>
    public string? TryReadSqlPassword(ConnectionProfile profile)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(profile.SourceDataDir))
        {
            return null;
        }

        var localStatePath = Path.Combine(profile.SourceDataDir, "Local State");
        var stateDbPath = Path.Combine(profile.SourceDataDir, "User", "globalStorage", "state.vscdb");
        if (!File.Exists(localStatePath) || !File.Exists(stateDbPath))
        {
            return null;
        }

        try
        {
            var masterKey = ReadWindowsMasterKey(localStatePath);
            if (masterKey is null)
            {
                return null;
            }

            var candidateKeys = BuildCandidateSecretKeys(profile);
            if (candidateKeys.Count == 0)
            {
                return null;
            }

            var rawValue = QueryFirstMatchingSecret(stateDbPath, candidateKeys);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return null;
            }

            var bufferBytes = ParseElectronBufferJson(rawValue);
            return bufferBytes is null ? null : DecryptV10Buffer(masterKey, bufferBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mssqlcl] Warning: could not read IDE secret for '{profile.DisplayName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decrypts an Electron safeStorage v10 payload (3-byte "v10" + 12-byte nonce + ciphertext + 16-byte tag).
    /// </summary>
    public static string DecryptV10Buffer(byte[] masterKey, byte[] buffer)
    {
        if (buffer.Length <= V10Prefix.Length + NonceSize + TagSize ||
            !buffer.AsSpan(0, V10Prefix.Length).SequenceEqual(V10Prefix))
        {
            throw new CryptographicException("Unsupported Electron secret buffer format (expected v10 AES-256-GCM payload).");
        }

        var nonce = buffer.AsSpan(V10Prefix.Length, NonceSize);
        var cipherLen = buffer.Length - V10Prefix.Length - NonceSize - TagSize;
        var ciphertext = buffer.AsSpan(V10Prefix.Length + NonceSize, cipherLen);
        var tag = buffer.AsSpan(V10Prefix.Length + NonceSize + cipherLen, TagSize);

        var plaintext = new byte[cipherLen];
        using var aes = new AesGcm(masterKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Parses VS Code's serialized Node Buffer JSON (<c>{"type":"Buffer","data":[...]}</c>).
    /// </summary>
    public static byte[]? ParseElectronBufferJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var bytes = new byte[dataEl.GetArrayLength()];
        var idx = 0;
        foreach (var item in dataEl.EnumerateArray())
        {
            bytes[idx++] = item.GetByte();
        }
        return bytes;
    }

    private static byte[]? ReadWindowsMasterKey(string localStatePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
            !osCrypt.TryGetProperty("encrypted_key", out var keyEl) ||
            keyEl.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = Convert.FromBase64String(keyEl.GetString()!);
        if (raw.Length <= DpapiPrefix.Length || !raw.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
        {
            return null;
        }

        return ProtectedData.Unprotect(raw[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser);
    }

    private static IReadOnlyList<string> BuildCandidateSecretKeys(ConnectionProfile profile)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Id))
        {
            keys.Add(FormatSecretKey($"Microsoft.SqlTools|profile_id:{profile.Id}|itemtype:Profile"));
            var lower = profile.Id.ToLowerInvariant();
            if (lower != profile.Id)
            {
                keys.Add(FormatSecretKey($"Microsoft.SqlTools|profile_id:{lower}|itemtype:Profile"));
            }
            var upper = profile.Id.ToUpperInvariant();
            if (upper != profile.Id)
            {
                keys.Add(FormatSecretKey($"Microsoft.SqlTools|profile_id:{upper}|itemtype:Profile"));
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.Server) && !string.IsNullOrWhiteSpace(profile.User))
        {
            var db = string.IsNullOrWhiteSpace(profile.Database) ? "master" : profile.Database;
            keys.Add(FormatSecretKey($"Microsoft.SqlTools|itemtype:Profile|server:{profile.Server}|db:{db}|user:{profile.User}"));
            if (!string.IsNullOrEmpty(profile.Database))
            {
                keys.Add(FormatSecretKey($"Microsoft.SqlTools|itemtype:Profile|server:{profile.Server}|db:|user:{profile.User}"));
            }
        }

        return keys;
    }

    private static string FormatSecretKey(string innerKey) =>
        $"secret://{JsonSerializer.Serialize(new { extensionId = "ms-mssql.mssql", key = innerKey })}";

    private static string? QueryFirstMatchingSecret(string stateDbPath, IReadOnlyList<string> candidateKeys)
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"mssqlcl_state_{Guid.NewGuid():N}.vscdb");
        try
        {
            // Copy to temp so we never contend with a running IDE's SQLite lock.
            File.Copy(stateDbPath, tempDb, overwrite: true);
            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1";
            var param = cmd.Parameters.Add("$key", SqliteType.Text);

            foreach (var key in candidateKeys)
            {
                param.Value = key;
                var result = cmd.ExecuteScalar();
                if (result is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
                if (result is byte[] b && b.Length > 0)
                {
                    return Encoding.UTF8.GetString(b);
                }
            }

            return null;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(tempDb); } catch { /* Best-effort temp cleanup */ }
        }
    }
}
