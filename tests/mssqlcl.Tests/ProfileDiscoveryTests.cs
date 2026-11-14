using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Tests;

public sealed class ProfileDiscoveryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mssqlcl_test_{Guid.NewGuid():N}");

    [Fact]
    public void ParseSettingsJson_HandlesJsoncCommentsTrailingCommasAndGroups()
    {
        const string jsonc = """
            {
              // User settings comment
              "editor.fontSize": 14,
              "mssql.connectionGroups": [
                { "name": "ROOT", "id": "ROOT" },
                { "name": "DEV", "id": "grp-dev", "parentId": "ROOT" },
                { "name": "AppDb", "id": "grp-app", "parentId": "grp-dev" },
              ],
              "mssql.connections": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "profileName": "LocalDev",
                  "server": "localhost\\SQLEXPRESS",
                  "database": "AppDb",
                  "user": "sa",
                  "authenticationType": "SqlLogin",
                  "encrypt": "Optional",
                  "trustServerCertificate": true,
                  "groupId": "grp-app",
                },
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "profileName": "CloudEntra",
                  "server": "example.database.windows.net",
                  "database": "Analytics",
                  "user": "user@example.com",
                  "email": "user@example.com",
                  "tenantId": "00000000-0000-0000-0000-000000000001",
                  "authenticationType": "AzureMFA",
                  "encrypt": true,
                }
              ],
            }
            """;

        var profiles = ProfileDiscoveryService.ParseSettingsJson(jsonc, "vscode", @"C:\fake\Code");

        Assert.Equal(2, profiles.Count);
        Assert.Equal("LocalDev", profiles[0].DisplayName);
        Assert.Equal("DEV/AppDb", profiles[0].GroupPath);
        Assert.Equal("Optional", profiles[0].Encrypt);
        Assert.True(profiles[0].TrustServerCertificate);

        Assert.Equal("CloudEntra", profiles[1].DisplayName);
        Assert.Equal("Mandatory", profiles[1].Encrypt);
        Assert.Equal("AzureMFA", profiles[1].AuthenticationType);
    }

    [Fact]
    public void FindProfile_ResolvesQualifiedSourceAndPriority()
    {
        var agDir = Path.Combine(_tempRoot, "Antigravity IDE");
        var vsDir = Path.Combine(_tempRoot, "Code");
        var localDir = Path.Combine(_tempRoot, ".mssqlcl");
        Directory.CreateDirectory(Path.Combine(agDir, "User"));
        Directory.CreateDirectory(Path.Combine(vsDir, "User"));

        File.WriteAllText(Path.Combine(agDir, "User", "settings.json"), """
            { "mssql.connections": [ { "id": "aaaa", "profileName": "Shared", "server": "ag-srv", "database": "db1", "authenticationType": "Integrated" } ] }
            """);
        File.WriteAllText(Path.Combine(vsDir, "User", "settings.json"), """
            { "mssql.connections": [ { "id": "bbbb", "profileName": "Shared", "server": "vs-srv", "database": "db2", "authenticationType": "Integrated" } ] }
            """);

        var locator = new IdeSourceLocator(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["antigravity"] = agDir,
                ["vscode"] = vsDir
            },
            localDir);
        var discovery = new ProfileDiscoveryService(locator, new LocalVaultStore(locator));
        var options = new McpOptions();

        var defaultMatch = discovery.FindProfile(options, serverOrProfileName: "Shared");
        Assert.NotNull(defaultMatch);
        Assert.Equal("antigravity", defaultMatch.Source);
        Assert.Equal("ag-srv", defaultMatch.Server);

        var vsMatch = discovery.FindProfile(options, serverOrProfileName: "vscode:Shared");
        Assert.NotNull(vsMatch);
        Assert.Equal("vscode", vsMatch.Source);
        Assert.Equal("vs-srv", vsMatch.Server);

        var byId = discovery.FindProfile(options, profileId: "bbbb");
        Assert.NotNull(byId);
        Assert.Equal("vs-srv", byId.Server);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
