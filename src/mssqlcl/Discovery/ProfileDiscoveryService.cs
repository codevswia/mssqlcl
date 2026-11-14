using System.Text.Json;
using MssqlCl.Models;

namespace MssqlCl.Discovery;

/// <summary>
/// Discovers SQL Server connection profiles from Antigravity IDE, VS Code, and local mssqlcl storage.
/// </summary>
public sealed class ProfileDiscoveryService
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IdeSourceLocator _locator;
    private readonly LocalVaultStore _localVault;

    public ProfileDiscoveryService(IdeSourceLocator locator, LocalVaultStore localVault)
    {
        _locator = locator;
        _localVault = localVault;
    }

    /// <summary>
    /// Loads all profiles according to the configured source priority.
    /// </summary>
    public IReadOnlyList<ConnectionProfile> ListProfiles(McpOptions options, string? filter = null)
    {
        var result = new List<ConnectionProfile>();

        foreach (var source in options.GetSourcePriority())
        {
            if (source.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(_localVault.LoadConnections());
                continue;
            }

            var dataDir = _locator.GetIdeDirectory(source);
            if (string.IsNullOrEmpty(dataDir))
            {
                continue;
            }

            var settingsPath = Path.Combine(dataDir, "User", "settings.json");
            if (!File.Exists(settingsPath))
            {
                continue;
            }

            result.AddRange(ParseSettingsFile(settingsPath, source, dataDir));
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            return result;
        }

        return result
            .Where(p =>
                p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Server.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Database.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (p.GroupPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                p.Source.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Finds a matching profile by profileId, "source:name", profileName, or server/database.
    /// </summary>
    public ConnectionProfile? FindProfile(
        McpOptions options,
        string? profileId = null,
        string? serverOrProfileName = null,
        string? database = null)
    {
        var profiles = ListProfiles(options);

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var byId = profiles.FirstOrDefault(p =>
                p.Id.Equals(profileId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }

            // Allow passing a profileName inside profileId as a convenience.
            var byNameViaId = MatchByNameOrQualified(profiles, profileId.Trim());
            if (byNameViaId is not null)
            {
                return byNameViaId;
            }
        }

        if (!string.IsNullOrWhiteSpace(serverOrProfileName))
        {
            var query = serverOrProfileName.Trim();
            var byName = MatchByNameOrQualified(profiles, query);
            if (byName is not null)
            {
                return byName;
            }

            // Match by server (+ database when specified).
            var serverMatches = profiles
                .Where(p => p.Server.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!string.IsNullOrWhiteSpace(database))
            {
                var exactDb = serverMatches.FirstOrDefault(p =>
                    p.Database.Equals(database.Trim(), StringComparison.OrdinalIgnoreCase));
                if (exactDb is not null)
                {
                    return exactDb;
                }
            }

            if (serverMatches.Count > 0)
            {
                return serverMatches[0];
            }
        }

        return null;
    }

    private static ConnectionProfile? MatchByNameOrQualified(IReadOnlyList<ConnectionProfile> profiles, string query)
    {
        var colonIdx = query.IndexOf(':');
        if (colonIdx > 0)
        {
            var sourcePrefix = query[..colonIdx];
            var rest = query[(colonIdx + 1)..];
            if (sourcePrefix is "antigravity" or "vscode" or "local")
            {
                return profiles.FirstOrDefault(p =>
                    p.Source.Equals(sourcePrefix, StringComparison.OrdinalIgnoreCase) &&
                    (p.DisplayName.Equals(rest, StringComparison.OrdinalIgnoreCase) ||
                     p.Id.Equals(rest, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return profiles.FirstOrDefault(p =>
            p.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses mssql.connections and mssql.connectionGroups from a VS Code / Antigravity settings.json (JSONC).
    /// </summary>
    public static IReadOnlyList<ConnectionProfile> ParseSettingsJson(string jsoncContent, string source, string? sourceDataDir)
    {
        using var doc = JsonDocument.Parse(jsoncContent, JsoncOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var groups = ParseGroups(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("mssql.connections", out var connArray) ||
            connArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var profiles = new List<ConnectionProfile>();
        foreach (var item in connArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var groupId = GetString(item, "groupId");
            var profile = new ConnectionProfile
            {
                Id = GetString(item, "id") ?? string.Empty,
                ProfileName = GetString(item, "profileName") ?? string.Empty,
                Server = GetString(item, "server") ?? string.Empty,
                Database = GetString(item, "database") ?? string.Empty,
                User = GetString(item, "user") ?? string.Empty,
                Password = GetString(item, "password"),
                AuthenticationType = GetString(item, "authenticationType") ?? "SqlLogin",
                Encrypt = ParseEncrypt(item),
                TrustServerCertificate = GetBool(item, "trustServerCertificate"),
                ConnectTimeout = GetInt(item, "connectTimeout", 30),
                CommandTimeout = GetInt(item, "commandTimeout", 30),
                ApplicationIntent = GetString(item, "applicationIntent") ?? "ReadWrite",
                GroupId = groupId,
                GroupPath = ResolveGroupPath(groupId, groups),
                TenantId = GetString(item, "tenantId"),
                Email = GetString(item, "email"),
                AccountId = GetString(item, "accountId"),
                SavePassword = GetBool(item, "savePassword"),
                Source = source,
                SourceDataDir = sourceDataDir
            };

            if (!string.IsNullOrWhiteSpace(profile.Server))
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    private static IReadOnlyList<ConnectionProfile> ParseSettingsFile(string settingsPath, string source, string dataDir)
    {
        try
        {
            var raw = File.ReadAllText(settingsPath);
            return ParseSettingsJson(raw, source, dataDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mssqlcl] Warning: failed to parse {settingsPath}: {ex.Message}");
            return [];
        }
    }

    private static Dictionary<string, ConnectionGroup> ParseGroups(JsonElement root)
    {
        var map = new Dictionary<string, ConnectionGroup>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("mssql.connectionGroups", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(item, "id");
            var name = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[id] = new ConnectionGroup
            {
                Id = id,
                Name = name,
                ParentId = GetString(item, "parentId")
            };
        }

        return map;
    }

    private static string? ResolveGroupPath(string? groupId, IReadOnlyDictionary<string, ConnectionGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(groupId) || groupId.Equals("ROOT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentId = groupId;

        while (!string.IsNullOrWhiteSpace(currentId) &&
               !currentId.Equals("ROOT", StringComparison.OrdinalIgnoreCase) &&
               visited.Add(currentId) &&
               groups.TryGetValue(currentId, out var group))
        {
            parts.Push(group.Name);
            currentId = group.ParentId;
        }

        return parts.Count > 0 ? string.Join("/", parts) : null;
    }

    private static string ParseEncrypt(JsonElement obj)
    {
        if (!obj.TryGetProperty("encrypt", out var el))
        {
            return "Optional";
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => "Mandatory",
            JsonValueKind.False => "Optional",
            JsonValueKind.String => el.GetString() ?? "Optional",
            _ => "Optional"
        };
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool GetBool(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False && el.GetBoolean();

    private static int GetInt(JsonElement obj, string prop, int fallback) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val) ? val : fallback;
}
