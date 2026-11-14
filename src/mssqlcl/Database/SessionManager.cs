using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using MssqlCl.Discovery;
using MssqlCl.Models;

namespace MssqlCl.Database;

public sealed class ActiveSession : IAsyncDisposable
{
    public required string ConnectionId { get; init; }
    public required ConnectionProfile Profile { get; init; }
    public required SqlConnection Connection { get; set; }
    public required string CredentialSource { get; init; }
    public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string CurrentDatabase =>
        string.IsNullOrWhiteSpace(Connection.Database) ? Profile.Database : Connection.Database;

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// Manages concurrent active SQL Server sessions keyed by connectionId.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CredentialResolver _credentialResolver;

    public SessionManager(CredentialResolver credentialResolver)
    {
        _credentialResolver = credentialResolver;
    }

    public async Task<(ActiveSession Session, string ServerContext)> ConnectAsync(
        ConnectionProfile profile,
        string? databaseOverride,
        CancellationToken ct)
    {
        var cred = _credentialResolver.Resolve(profile);
        if (!cred.Success)
        {
            throw new InvalidOperationException(cred.Error ?? "Credential resolution failed.");
        }

        var csb = SqlConnectionFactory.BuildConnectionString(profile, cred.Password, databaseOverride);
        var conn = new SqlConnection(csb.ConnectionString);

        try
        {
            await conn.OpenAsync(ct);
            var context = await QueryServerContextAsync(conn, ct);
            var id = "conn-" + Guid.NewGuid().ToString("N")[..8];
            var session = new ActiveSession
            {
                ConnectionId = id,
                Profile = profile,
                Connection = conn,
                CredentialSource = cred.Source
            };
            _sessions[id] = session;
            return (session, context);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public ActiveSession GetRequiredSession(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            if (_sessions.Count == 1)
            {
                return _sessions.Values.First();
            }
            throw new ArgumentException("connectionId is required. Call mssql_connect first.");
        }

        if (!_sessions.TryGetValue(connectionId.Trim(), out var session))
        {
            throw new KeyNotFoundException($"No active connection found for connectionId '{connectionId}'.");
        }

        return session;
    }

    public async Task<bool> DisconnectAsync(string connectionId)
    {
        if (_sessions.TryRemove(connectionId.Trim(), out var session))
        {
            await session.DisposeAsync();
            return true;
        }
        return false;
    }

    public async Task ChangeDatabaseAsync(string connectionId, string database, CancellationToken ct)
    {
        var session = GetRequiredSession(connectionId);
        if (session.Connection.State != ConnectionState.Open)
        {
            await session.Connection.OpenAsync(ct);
        }

        try
        {
            await session.Connection.ChangeDatabaseAsync(database, ct);
        }
        catch (SqlException ex) when (ex.Number == 40508 || ex.Message.Contains("USE statement is not supported", StringComparison.OrdinalIgnoreCase))
        {
            // Azure SQL Database does not support USE [db]; reconnect targeting the new catalog.
            var cred = _credentialResolver.Resolve(session.Profile);
            if (!cred.Success)
            {
                throw new InvalidOperationException(cred.Error ?? "Credential resolution failed while switching Azure SQL database.");
            }

            var csb = SqlConnectionFactory.BuildConnectionString(session.Profile, cred.Password, database);
            var newConn = new SqlConnection(csb.ConnectionString);
            try
            {
                await newConn.OpenAsync(ct);
                var oldConn = session.Connection;
                session.Connection = newConn;
                await oldConn.DisposeAsync();
            }
            catch
            {
                await newConn.DisposeAsync();
                throw;
            }
        }
    }

    public IReadOnlyCollection<ActiveSession> ListSessions() => _sessions.Values.ToList();

    private static async Task<string> QueryServerContextAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                @@SERVERNAME AS ServerName,
                DB_NAME() AS CurrentDatabase,
                ORIGINAL_LOGIN() AS LoginName,
                SUSER_SNAME() AS SessionUser,
                CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)) AS ProductVersion,
                CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)) AS Edition,
                CAST(SERVERPROPERTY('Collation') AS NVARCHAR(128)) AS ServerCollation;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return string.Empty;
        }

        return $"ServerName={reader["ServerName"]}, Database={reader["CurrentDatabase"]}, Login={reader["LoginName"]}, Version={reader["ProductVersion"]}, Edition={reader["Edition"]}, Collation={reader["ServerCollation"]}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _sessions)
        {
            if (_sessions.TryRemove(kv.Key, out var session))
            {
                await session.DisposeAsync();
            }
        }
    }
}
