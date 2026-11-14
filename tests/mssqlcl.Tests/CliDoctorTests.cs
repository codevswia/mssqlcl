using MssqlCl.Cli;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Tests;

public sealed class CliDoctorTests : IDisposable
{
    private readonly string _tempRoot;

    public CliDoctorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mssqlcl-doctor-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_ReportsEnvironmentAndReturnsZero()
    {
        var ideDir = Path.Combine(_tempRoot, "vscode");
        Directory.CreateDirectory(Path.Combine(ideDir, "User", "globalStorage"));
        File.WriteAllText(Path.Combine(ideDir, "User", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(ideDir, "Local State"), "{}");

        var localDir = Path.Combine(_tempRoot, ".mssqlcl");
        Directory.CreateDirectory(localDir);

        var locator = new IdeSourceLocator(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["vscode"] = ideDir,
                ["antigravity"] = Path.Combine(_tempRoot, "missing-antigravity")
            },
            localDir);

        var vault = new LocalVaultStore(locator);
        var discovery = new ProfileDiscoveryService(locator, vault);
        var creds = new CredentialResolver(new IdeSecretReader(), vault);
        await using var sessions = new SessionManager(creds);
        var cli = new CliHandler(discovery, creds, vault, sessions, locator);

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exitCode = await cli.ExecuteAsync(["doctor"], new McpOptions(), CancellationToken.None);
            var output = sw.ToString();

            Assert.Equal(0, exitCode);
            Assert.Contains("mssqlcl doctor", output);
            Assert.Contains("connections.json", output);
            Assert.Contains("vscode", output);
            Assert.Contains("[FOUND]", output);
            Assert.Contains("[NOT FOUND]", output);
            Assert.Contains("[OK] Environment looks healthy.", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
