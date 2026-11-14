using MssqlCl.Security;

namespace MssqlCl.Tests;

public sealed class SqlReadOnlyGuardTests
{
    [Theory]
    [InlineData("SELECT TOP 10 * FROM dbo.Users")]
    [InlineData("WITH cte AS (SELECT id, updated_at FROM dbo.Orders) SELECT * FROM cte")]
    [InlineData("SELECT 'DELETE FROM Users' AS label, [update_date] FROM dbo.Logs -- DROP TABLE Users")]
    [InlineData("/* INSERT INTO x VALUES (1) /* nested DELETE */ */ SELECT 1 AS ok")]
    [InlineData("SELECT \"ALTER\" FROM dbo.[CREATE]")]
    public void ReadOnly_AllowsSafeSelectQueries_EvenWithKeywordsInCommentsStringsOrIdentifiers(string sql)
    {
        var result = SqlReadOnlyGuard.Validate(sql, readOnlyMode: true);
        Assert.True(result.Allowed, result.BlockedReason);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Users(name) VALUES ('a')")]
    [InlineData("UPDATE dbo.Users SET name = 'b' WHERE id = 1")]
    [InlineData("DELETE FROM dbo.Users WHERE id = 1")]
    [InlineData("MERGE dbo.Target AS t USING dbo.Source AS s ON t.id = s.id WHEN MATCHED THEN DELETE;")]
    [InlineData("DROP TABLE dbo.Users")]
    [InlineData("ALTER TABLE dbo.Users ADD col INT")]
    [InlineData("CREATE TABLE dbo.Tmp (id INT)")]
    [InlineData("TRUNCATE TABLE dbo.Logs")]
    [InlineData("EXEC dbo.sp_SomeProc")]
    [InlineData("EXECUTE sp_executesql N'DELETE FROM dbo.Users'")]
    [InlineData("GRANT SELECT ON dbo.Users TO public")]
    [InlineData("SELECT * INTO dbo.BackupUsers FROM dbo.Users")]
    public void ReadOnly_BlocksWriteDdlAndExecStatements(string sql)
    {
        var result = SqlReadOnlyGuard.Validate(sql, readOnlyMode: true);
        Assert.False(result.Allowed);
        Assert.Contains("--readonly", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWrite_AllowsWriteStatements()
    {
        var result = SqlReadOnlyGuard.Validate("DELETE FROM dbo.Temp WHERE id = 1", readOnlyMode: false);
        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void RejectsEmptyOrCommentOnlyQueries(string sql)
    {
        var result = SqlReadOnlyGuard.Validate(sql, readOnlyMode: true);
        Assert.False(result.Allowed);
    }
}
