using System.Data;
using System.Data.Common;
using MssqlCl.Database;

namespace MssqlCl.Tests;

public sealed class CsvFormatterTests
{
    [Fact]
    public async Task FormatReaderAsync_FormatsRfc4180CsvAndHandlesNulls()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("note", typeof(string));

        table.Rows.Add(1, "Alice", "hello, world");
        table.Rows.Add(2, "Bob \"B\"", DBNull.Value);
        table.Rows.Add(3, "Line1\nLine2", "ok");

        await using var reader = table.CreateDataReader();
        var csv = await CsvFormatter.FormatReaderAsync(reader, maxRows: 100, CancellationToken.None);

        var normalized = csv.Replace("\r\n", "\n");
        Assert.Contains("id,name,note\n", normalized);
        Assert.Contains("1,Alice,\"hello, world\"\n", normalized);
        Assert.Contains("2,\"Bob \"\"B\"\"\",\n", normalized);
        Assert.Contains("3,\"Line1\nLine2\",ok", normalized);
    }

    [Fact]
    public async Task FormatReaderAsync_TruncatesAtMaxRows()
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(int));
        for (var i = 1; i <= 5; i++)
        {
            table.Rows.Add(i);
        }

        await using var reader = table.CreateDataReader();
        var csv = await CsvFormatter.FormatReaderAsync(reader, maxRows: 2, CancellationToken.None);

        Assert.Contains("truncated at 2 rows", csv);
        Assert.DoesNotContain("\n3\n", csv.Replace("\r\n", "\n"));
    }
}
