using ModelContextProtocol.Protocol;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Mcp;
using MssqlCl.Models;
using MssqlCl.Security;

namespace MssqlCl.Tests;

public sealed class McpToolsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mssqlcl_mcp_{Guid.NewGuid():N}");

    [Fact]
    public async Task RunQuery_BlocksDmlInReadOnlyMode_BeforeRequiringLiveSqlServer()
    {
        var locator = new IdeSourceLocator(new Dictionary<string, string>(), _tempRoot);
        var vault = new LocalVaultStore(locator);
        var discovery = new ProfileDiscoveryService(locator, vault);
        var creds = new CredentialResolver(new IdeSecretReader(), vault);
        await using var sessions = new SessionManager(creds);
        var options = new McpOptions { ReadOnly = true };
        var audit = new AuditLogger(locator, options);

        var tools = new MssqlTools(discovery, sessions, audit, options);
        var result = await tools.RunQueryAsync("conn-missing", "DELETE FROM dbo.Users WHERE id = 1");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("--readonly", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListServers_ReturnsDiscoveredProfiles()
    {
        var locator = new IdeSourceLocator(new Dictionary<string, string>(), _tempRoot);
        var vault = new LocalVaultStore(locator);
        vault.SaveProfile(new ConnectionProfile
        {
            Id = "id-100",
            ProfileName = "DemoSql",
            Server = "localhost",
            Database = "DemoDb",
            User = "reader",
            AuthenticationType = "Integrated"
        });

        var discovery = new ProfileDiscoveryService(locator, vault);
        var creds = new CredentialResolver(new IdeSecretReader(), vault);
        using var _ = new MemoryStream();
        var sessions = new SessionManager(creds);
        var options = new McpOptions { SourceFilter = "local" };
        var tools = new MssqlTools(discovery, sessions, new AuditLogger(locator, options), options);

        var res = tools.ListServers();
        Assert.False(res.IsError);
        var text = Assert.IsType<TextContentBlock>(res.Content[0]).Text;
        Assert.Contains("DemoSql", text);
        Assert.Contains("id-100", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
