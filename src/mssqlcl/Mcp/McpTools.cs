using System.ComponentModel;
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MssqlCl.Database;
using MssqlCl.Discovery;
using MssqlCl.Models;
using MssqlCl.Security;

namespace MssqlCl.Mcp;

/// <summary>
/// Exposes the 11 SQL Server database MCP tools.
/// </summary>
[McpServerToolType]
public sealed class MssqlTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ProfileDiscoveryService _discovery;
    private readonly SessionManager _sessions;
    private readonly AuditLogger _audit;
    private readonly McpOptions _options;

    public MssqlTools(
        ProfileDiscoveryService discovery,
        SessionManager sessions,
        AuditLogger audit,
        McpOptions options)
    {
        _discovery = discovery;
        _sessions = sessions;
        _audit = audit;
        _options = options;
    }

    [McpServerTool(Name = "mssql_list_servers", ReadOnly = true, Idempotent = true)]
    [Description("Lists all saved SQL Server connection profiles discovered from Antigravity IDE, VS Code, and local ~/.mssqlcl storage. Call this first to obtain a profileId or profileName before calling mssql_connect.")]
    public CallToolResult ListServers(
        [Description("Optional substring filter matched against profileName, server, database, groupPath, or source.")] string? filter = null)
    {
        var profiles = _discovery.ListProfiles(_options, filter);
        var items = profiles.Select(p => new
        {
            profileId = p.Id,
            profileName = p.DisplayName,
            server = p.Server,
            database = string.IsNullOrWhiteSpace(p.Database) ? "master" : p.Database,
            user = string.IsNullOrWhiteSpace(p.Email) ? p.User : p.Email,
            authenticationType = p.AuthenticationType,
            encrypt = p.Encrypt,
            groupPath = p.GroupPath,
            source = p.Source
        });

        _audit.Log("mssql_list_servers", "ok", detail: $"count={profiles.Count}");
        return Ok(JsonSerializer.Serialize(items, JsonOptions));
    }

    [McpServerTool(Name = "mssql_connect", ReadOnly = true)]
    [Description("Opens a SQL Server session using a saved profileId, profileName (optionally qualified as 'antigravity:Name' or 'vscode:Name'), or serverName + database. Returns a connectionId and database environment context.")]
    public async Task<CallToolResult> ConnectAsync(
        [Description("Profile UUID from mssql_list_servers (or profile name).")] string? profileId = null,
        [Description("Server host or profile name if profileId is omitted.")] string? serverName = null,
        [Description("Optional database name override.")] string? database = null,
        CancellationToken ct = default)
    {
        var profile = _discovery.FindProfile(_options, profileId, serverName, database);
        if (profile is null)
        {
            _audit.Log("mssql_connect", "not_found", detail: $"profileId={profileId}, server={serverName}");
            return Error(
                $"No matching saved connection profile was found for profileId='{profileId}', serverName='{serverName}'. " +
                "Call mssql_list_servers to inspect available profiles, or create one in VS Code / Antigravity IDE / 'mssqlcl conn save'.");
        }

        try
        {
            var (session, context) = await _sessions.ConnectAsync(profile, database, ct);
            _audit.Log("mssql_connect", "ok", session.ConnectionId, profile.DisplayName, profile.Server, session.CurrentDatabase, $"auth={session.CredentialSource}");

            var payload = new
            {
                connectionId = session.ConnectionId,
                profileName = profile.DisplayName,
                source = profile.Source,
                server = profile.Server,
                database = session.CurrentDatabase,
                authenticationType = profile.AuthenticationType,
                credentialSource = session.CredentialSource,
                mode = _options.ReadOnly ? "readonly" : "readwrite",
                context
            };
            return Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            _audit.Log("mssql_connect", "error", profileName: profile.DisplayName, server: profile.Server, database: database ?? profile.Database, detail: ex.Message);
            return Error($"Failed to connect to '{profile.DisplayName}' ({profile.Server}): {ex.Message}");
        }
    }

    [McpServerTool(Name = "mssql_disconnect", ReadOnly = true)]
    [Description("Closes an active SQL Server connection identified by connectionId.")]
    public async Task<CallToolResult> DisconnectAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId)
    {
        var closed = await _sessions.DisconnectAsync(connectionId);
        _audit.Log("mssql_disconnect", closed ? "ok" : "not_found", connectionId);
        return closed
            ? Ok($"Disconnected '{connectionId}'.")
            : Error($"No active connection found for connectionId '{connectionId}'.");
    }

    [McpServerTool(Name = "mssql_get_connection_details", ReadOnly = true, Idempotent = true)]
    [Description("Returns metadata and active state for a connectionId (or all open connections if omitted).")]
    public CallToolResult GetConnectionDetails(
        [Description("Optional connectionId; if omitted, lists all active sessions.")] string? connectionId = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            var all = _sessions.ListSessions().Select(FormatSessionSummary);
            return Ok(JsonSerializer.Serialize(all, JsonOptions));
        }

        try
        {
            var session = _sessions.GetRequiredSession(connectionId);
            return Ok(JsonSerializer.Serialize(FormatSessionSummary(session), JsonOptions));
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "mssql_change_database", ReadOnly = true)]
    [Description("Switches the active database catalog on an open connectionId.")]
    public async Task<CallToolResult> ChangeDatabaseAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Target database name.")] string database,
        CancellationToken ct = default)
    {
        try
        {
            var session = _sessions.GetRequiredSession(connectionId);
            await _sessions.ChangeDatabaseAsync(connectionId, database, ct);
            _audit.Log("mssql_change_database", "ok", session.ConnectionId, session.Profile.DisplayName, session.Profile.Server, session.CurrentDatabase);
            return Ok($"Active database for '{session.ConnectionId}' switched to '{session.CurrentDatabase}'.");
        }
        catch (Exception ex)
        {
            _audit.Log("mssql_change_database", "error", connectionId, database: database, detail: ex.Message);
            return Error($"Failed to change database to '{database}': {ex.Message}");
        }
    }

    [McpServerTool(Name = "mssql_list_databases", ReadOnly = true, Idempotent = true)]
    [Description("Lists accessible databases on the SQL Server instance for the given connectionId in CSV format.")]
    public Task<CallToolResult> ListDatabasesAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                name AS database_name,
                state_desc AS state,
                compatibility_level,
                recovery_model_desc AS recovery_model
            FROM sys.databases
            WHERE HAS_DBACCESS(name) = 1
            ORDER BY name;
            """;
        return ExecuteCatalogQueryAsync("mssql_list_databases", connectionId, sql, null, ct);
    }

    [McpServerTool(Name = "mssql_list_schemas", ReadOnly = true, Idempotent = true)]
    [Description("Lists user schemas in the current database for the given connectionId in CSV format.")]
    public Task<CallToolResult> ListSchemasAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                s.schema_id,
                s.name AS schema_name,
                USER_NAME(s.principal_id) AS owner_name
            FROM sys.schemas s
            WHERE s.schema_id < 16384
              AND s.name NOT IN ('guest', 'INFORMATION_SCHEMA', 'sys')
            ORDER BY s.name;
            """;
        return ExecuteCatalogQueryAsync("mssql_list_schemas", connectionId, sql, null, ct);
    }

    [McpServerTool(Name = "mssql_list_tables", ReadOnly = true, Idempotent = true)]
    [Description("Lists user tables and approximate row counts in the current database for the given connectionId in CSV format.")]
    public Task<CallToolResult> ListTablesAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Optional schema name filter (e.g. 'dbo').")] string? schema = null,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                s.name AS schema_name,
                t.name AS table_name,
                SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS approx_rows
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.partitions p ON t.object_id = p.object_id
            WHERE (@schema IS NULL OR s.name = @schema)
            GROUP BY s.name, t.name
            ORDER BY s.name, t.name;
            """;
        return ExecuteCatalogQueryAsync("mssql_list_tables", connectionId, sql, schema, ct);
    }

    [McpServerTool(Name = "mssql_list_views", ReadOnly = true, Idempotent = true)]
    [Description("Lists user views in the current database for the given connectionId in CSV format.")]
    public Task<CallToolResult> ListViewsAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Optional schema name filter.")] string? schema = null,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                s.name AS schema_name,
                v.name AS view_name
            FROM sys.views v
            INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
            WHERE (@schema IS NULL OR s.name = @schema)
            ORDER BY s.name, v.name;
            """;
        return ExecuteCatalogQueryAsync("mssql_list_views", connectionId, sql, schema, ct);
    }

    [McpServerTool(Name = "mssql_list_functions", ReadOnly = true, Idempotent = true)]
    [Description("Lists user-defined functions in the current database for the given connectionId in CSV format.")]
    public Task<CallToolResult> ListFunctionsAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Optional schema name filter.")] string? schema = null,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                s.name AS schema_name,
                o.name AS function_name,
                o.type_desc AS function_type
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('FN', 'IF', 'TF', 'AF', 'FS', 'FT')
              AND (@schema IS NULL OR s.name = @schema)
            ORDER BY s.name, o.name;
            """;
        return ExecuteCatalogQueryAsync("mssql_list_functions", connectionId, sql, schema, ct);
    }

    [McpServerTool(Name = "mssql_run_query", ReadOnly = false)]
    [Description("Executes a T-SQL query against an active connectionId and returns compact CSV (default) or JSON. In --readonly mode (default), write/DDL/EXEC statements are blocked.")]
    public async Task<CallToolResult> RunQueryAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The T-SQL query to execute.")] string query,
        [Description("Brief intent description for audit logging.")] string? queryIntent = null,
        [Description("Optional query classification (e.g. SELECT, DDL, DML).")] string? queryTypes = null,
        [Description("Output format: 'csv' (default) or 'json'.")] string? format = "csv",
        CancellationToken ct = default)
    {
        var guard = SqlReadOnlyGuard.Validate(query, _options.ReadOnly);
        if (!guard.Allowed)
        {
            _audit.Log("mssql_run_query", "blocked", connectionId, detail: guard.BlockedReason);
            return Error(guard.BlockedReason!);
        }

        ActiveSession session;
        try
        {
            session = _sessions.GetRequiredSession(connectionId);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        try
        {
            if (session.Connection.State != ConnectionState.Open)
            {
                await session.Connection.OpenAsync(ct);
            }

            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = query;
            cmd.CommandTimeout = session.Profile.CommandTimeout > 0
                ? session.Profile.CommandTimeout
                : _options.DefaultQueryTimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var output = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
                ? await FormatJsonAsync(reader, _options.MaxRows, ct)
                : await CsvFormatter.FormatReaderAsync(reader, _options.MaxRows, ct);

            _audit.Log(
                "mssql_run_query",
                "ok",
                session.ConnectionId,
                session.Profile.DisplayName,
                session.Profile.Server,
                session.CurrentDatabase,
                $"intent={queryIntent};types={queryTypes};len={query.Length}");

            return Ok(output);
        }
        catch (Exception ex)
        {
            _audit.Log(
                "mssql_run_query",
                "error",
                session.ConnectionId,
                session.Profile.DisplayName,
                session.Profile.Server,
                session.CurrentDatabase,
                ex.Message);

            return Error(
                $"SQL execution error: {ex.Message}\n" +
                "Review the SQL error message above, fix the query if possible, and retry.");
        }
    }

    private async Task<CallToolResult> ExecuteCatalogQueryAsync(
        string toolName,
        string connectionId,
        string sql,
        string? schemaFilter,
        CancellationToken ct)
    {
        ActiveSession session;
        try
        {
            session = _sessions.GetRequiredSession(connectionId);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        try
        {
            if (session.Connection.State != ConnectionState.Open)
            {
                await session.Connection.OpenAsync(ct);
            }

            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _options.DefaultQueryTimeoutSeconds;
            if (sql.Contains("@schema", StringComparison.Ordinal))
            {
                cmd.Parameters.AddWithValue("@schema", string.IsNullOrWhiteSpace(schemaFilter) ? DBNull.Value : schemaFilter);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var csv = await CsvFormatter.FormatReaderAsync(reader, _options.MaxRows, ct);
            _audit.Log(toolName, "ok", session.ConnectionId, session.Profile.DisplayName, session.Profile.Server, session.CurrentDatabase);
            return Ok(csv);
        }
        catch (Exception ex)
        {
            _audit.Log(toolName, "error", session.ConnectionId, session.Profile.DisplayName, session.Profile.Server, session.CurrentDatabase, ex.Message);
            return Error($"Error executing {toolName}: {ex.Message}");
        }
    }

    private static async Task<string> FormatJsonAsync(SqlDataReader reader, int maxRows, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        if (rows.Count == 0 && reader.RecordsAffected >= 0)
        {
            return JsonSerializer.Serialize(new { rowsAffected = reader.RecordsAffected }, JsonOptions);
        }

        return JsonSerializer.Serialize(new { rowCount = rows.Count, truncated, rows }, JsonOptions);
    }

    private static object FormatSessionSummary(ActiveSession s) => new
    {
        connectionId = s.ConnectionId,
        profileName = s.Profile.DisplayName,
        source = s.Profile.Source,
        server = s.Profile.Server,
        database = s.CurrentDatabase,
        authenticationType = s.Profile.AuthenticationType,
        credentialSource = s.CredentialSource,
        state = s.Connection.State.ToString(),
        connectedAtUtc = s.ConnectedAtUtc.ToString("O")
    };

    private static CallToolResult Ok(string text) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = text }]
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"ERROR: {message}" }]
    };
}
