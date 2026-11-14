using System.Text;

namespace MssqlCl.Security;

public sealed record ReadOnlyCheckResult(bool Allowed, string? BlockedReason = null);

/// <summary>
/// Lexical T-SQL guard for --readonly mode that ignores comments, string literals, and quoted identifiers.
/// </summary>
public static class SqlReadOnlyGuard
{
    private static readonly HashSet<string> BlockedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT",
        "UPDATE",
        "DELETE",
        "MERGE",
        "DROP",
        "ALTER",
        "CREATE",
        "TRUNCATE",
        "EXEC",
        "EXECUTE",
        "SP_EXECUTESQL",
        "GRANT",
        "REVOKE",
        "DENY",
        "BACKUP",
        "RESTORE",
        "DBCC",
        "SHUTDOWN",
        "RECONFIGURE",
        "KILL",
        "BULK",
        "OPENROWSET",
        "OPENDATASOURCE",
        "WRITETEXT",
        "UPDATETEXT",
        "DISABLE",
        "ENABLE"
    };

    public static ReadOnlyCheckResult Validate(string sql, bool readOnlyMode)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new ReadOnlyCheckResult(false, "SQL query cannot be empty.");
        }

        if (!readOnlyMode)
        {
            return new ReadOnlyCheckResult(true);
        }

        var tokens = TokenizeUnquotedWords(sql);
        if (tokens.Count == 0)
        {
            return new ReadOnlyCheckResult(false, "SQL query contains no executable statements.");
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (BlockedKeywords.Contains(token))
            {
                return new ReadOnlyCheckResult(
                    false,
                    $"Blocked by --readonly mode: keyword '{token.ToUpperInvariant()}' is not permitted. Restart mssqlcl with --readwrite if write operations are intended.");
            }

            // Detect SELECT ... INTO <table_name> (creates a table) while ignoring variable targets.
            if (token.Equals("INTO", StringComparison.OrdinalIgnoreCase) &&
                i > 0 &&
                i + 1 < tokens.Count &&
                !tokens[i + 1].StartsWith('@') &&
                HasPrecedingSelectWithoutFrom(tokens, i))
            {
                return new ReadOnlyCheckResult(
                    false,
                    "Blocked by --readonly mode: 'SELECT ... INTO' creates a table and is not permitted.");
            }
        }

        return new ReadOnlyCheckResult(true);
    }

    private static bool HasPrecedingSelectWithoutFrom(List<string> tokens, int intoIndex)
    {
        for (var j = intoIndex - 1; j >= 0; j--)
        {
            if (tokens[j].Equals("FROM", StringComparison.OrdinalIgnoreCase) ||
                tokens[j] == ";")
            {
                return false;
            }
            if (tokens[j].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts unquoted identifier/keyword tokens outside T-SQL comments, string literals, and quoted identifiers.
    /// </summary>
    internal static List<string> TokenizeUnquotedWords(string sql)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            // Line comment: -- ... \n
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                FlushToken(current, tokens);
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            // Nested block comment: /* ... */
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                FlushToken(current, tokens);
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                continue;
            }

            // String literal: '...' (with '' escape)
            if (c == '\'')
            {
                FlushToken(current, tokens);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Bracketed identifier: [...] (with ]] escape)
            if (c == '[')
            {
                FlushToken(current, tokens);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == ']')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == ']')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Double-quoted identifier: "..." (with "" escape)
            if (c == '"')
            {
                FlushToken(current, tokens);
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == ';')
            {
                FlushToken(current, tokens);
                tokens.Add(";");
                i++;
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#')
            {
                current.Append(c);
            }
            else
            {
                FlushToken(current, tokens);
            }

            i++;
        }

        FlushToken(current, tokens);
        return tokens;
    }

    private static void FlushToken(StringBuilder current, List<string> tokens)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
            current.Clear();
        }
    }
}
