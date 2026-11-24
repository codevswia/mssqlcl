using ModelContextProtocol.Protocol;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Mcp;
using MssqlCl.Models;
using MssqlCl.Security;

namespace MssqlCl.Tests;

public sealed class QueryStoreToolsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mssqlcl_qs_{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(24, true)]
    [InlineData(8760, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(8761, false)]
    public void ResolveWindow_ValidatesHoursBounds(int? hours, bool expectedOk)
    {
        var now = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var (ok, start, end, err) = QueryStoreService.ResolveWindow(hours, null, null, now);

        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Null(err);
            Assert.Equal(now, end);
            Assert.Equal(now.AddHours(-(hours ?? QueryStoreService.DefaultHours)), start);
        }
        else
        {
            Assert.NotNull(err);
        }
    }

    [Fact]
    public void ResolveWindow_ValidatesExplicitUtcRange()
    {
        var (ok, start, end, err) = QueryStoreService.ResolveWindow(null, "2026-11-23T00:00:00Z", "2026-11-23T12:00:00Z");
        Assert.True(ok);
        Assert.Null(err);
        Assert.True(start < end);

        var reversed = QueryStoreService.ResolveWindow(null, "2026-11-23T12:00:00Z", "2026-11-23T00:00:00Z");
        Assert.False(reversed.Ok);

        var partial = QueryStoreService.ResolveWindow(null, "not-a-date", "2026-11-23T12:00:00Z");
        Assert.False(partial.Ok);
    }

    [Theory]
    [InlineData(null, true, "duration")]
    [InlineData("CPU", true, "cpu")]
    [InlineData("logical_reads", true, "logical_reads")]
    [InlineData("writes", true, "writes")]
    [InlineData("memory", true, "memory")]
    [InlineData("executions", true, "executions")]
    [InlineData("unknown_metric", false, "unknown_metric")]
    public void IsValidMetric_ValidatesSupportedMetrics(string? metric, bool expectedValid, string expectedNormalized)
    {
        var valid = QueryStoreService.IsValidMetric(metric, out var normalized);
        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedNormalized, normalized);
    }

    [Theory]
    [InlineData(null, true, 10)]
    [InlineData(1, true, 1)]
    [InlineData(50, true, 50)]
    [InlineData(0, false, 0)]
    [InlineData(51, false, 51)]
    public void IsValidTop_ValidatesBounds(int? top, bool expectedValid, int expectedResolved)
    {
        var valid = QueryStoreService.IsValidTop(top, out var resolved);
        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedResolved, resolved);
    }

    [Fact]
    public async Task ForcePlan_BlockedInReadOnlyMode()
    {
        await using var harness = CreateTools(readOnly: true);
        var result = await harness.Tools.QueryStoreForcePlanAsync("conn-missing", 1, 2, "force");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("--readwrite", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForcePlan_RejectsInvalidActionInReadWriteMode()
    {
        await using var harness = CreateTools(readOnly: false);
        var result = await harness.Tools.QueryStoreForcePlanAsync("conn-missing", 1, 2, "drop");

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("Invalid action", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TopQueries_RejectsInvalidMetricAndTop()
    {
        await using var harness = CreateTools(readOnly: true);

        var badMetric = await harness.Tools.QueryStoreTopQueriesAsync("conn-missing", metric: "bogus");
        Assert.True(badMetric.IsError);

        var badTop = await harness.Tools.QueryStoreTopQueriesAsync("conn-missing", top: 99);
        Assert.True(badTop.IsError);

        var missingConn = await harness.Tools.QueryStoreStatusAsync("conn-missing");
        Assert.True(missingConn.IsError);
    }

    private ToolsHarness CreateTools(bool readOnly)
    {
        var locator = new IdeSourceLocator(new Dictionary<string, string>(), _tempRoot);
        var vault = new LocalVaultStore(locator);
        var discovery = new ProfileDiscoveryService(locator, vault);
        var creds = new CredentialResolver(new IdeSecretReader(), vault);
        var sessions = new SessionManager(creds);
        var options = new McpOptions { ReadOnly = readOnly };
        var audit = new AuditLogger(locator, options);
        var tools = new MssqlTools(discovery, sessions, audit, options);
        return new ToolsHarness(tools, sessions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class ToolsHarness(MssqlTools tools, SessionManager sessions) : IAsyncDisposable
    {
        public MssqlTools Tools { get; } = tools;
        public ValueTask DisposeAsync() => sessions.DisposeAsync();
    }
}
