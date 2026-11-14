using System.Data.Common;
using System.Globalization;
using System.Text;

namespace MssqlCl.Database;

/// <summary>
/// Formats DbDataReader result sets into compact RFC 4180 CSV (modeled after SQLcl's SET SQLFORMAT CSV).
/// </summary>
public static class CsvFormatter
{
    public static async Task<string> FormatReaderAsync(DbDataReader reader, int maxRows, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var resultSetIndex = 0;

        do
        {
            if (reader.FieldCount == 0)
            {
                resultSetIndex++;
                continue;
            }

            if (resultSetIndex > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"# ResultSet {resultSetIndex + 1}");
            }

            for (var col = 0; col < reader.FieldCount; col++)
            {
                if (col > 0)
                {
                    sb.Append(',');
                }
                AppendCsvField(sb, reader.GetName(col));
            }
            sb.AppendLine();

            var rowCount = 0;
            var truncated = false;

            while (await reader.ReadAsync(ct))
            {
                if (rowCount >= maxRows)
                {
                    truncated = true;
                    break;
                }

                for (var col = 0; col < reader.FieldCount; col++)
                {
                    if (col > 0)
                    {
                        sb.Append(',');
                    }

                    if (await reader.IsDBNullAsync(col, ct))
                    {
                        continue;
                    }

                    var val = reader.GetValue(col);
                    AppendCsvField(sb, FormatScalar(val));
                }
                sb.AppendLine();
                rowCount++;
            }

            if (truncated)
            {
                sb.AppendLine($"# [truncated at {maxRows} rows; refine your query with TOP/WHERE or increase --max-rows]");
            }
            else if (rowCount == 0)
            {
                sb.AppendLine("# (0 rows)");
            }

            resultSetIndex++;
        }
        while (await reader.NextResultAsync(ct));

        if (resultSetIndex == 0 || (sb.Length == 0 && reader.RecordsAffected >= 0))
        {
            sb.AppendLine($"OK (rowsAffected={reader.RecordsAffected})");
        }

        return sb.ToString().TrimEnd();
    }

    internal static string FormatScalar(object value) => value switch
    {
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    internal static void AppendCsvField(StringBuilder sb, string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            sb.Append(field);
            return;
        }

        sb.Append('"');
        foreach (var ch in field)
        {
            if (ch == '"')
            {
                sb.Append("\"\"");
            }
            else
            {
                sb.Append(ch);
            }
        }
        sb.Append('"');
    }
}
