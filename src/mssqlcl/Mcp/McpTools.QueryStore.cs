using System.ComponentModel;
using System.Data;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MssqlCl.Database;
using MssqlCl.Models;

namespace MssqlCl.Mcp;

public sealed partial class MssqlTools
{
    private readonly QueryStoreService _queryStore = new();

    [McpServerTool(Name = "mssql_query_store_status", ReadOnly = true, Idempotent = true)]
    [Description("Checks whether Query Store is enabled on the active database and returns its state, storage usage, retention, and capture mode.")]
    public async Task<CallToolResult> QueryStoreStatusAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        CancellationToken ct = default)
    {
        return await ExecuteQueryStoreReadAsync("mssql_query_store_status", connectionId, async session =>
        {
            var status = await _queryStore.GetStatusAsync(session, GetTimeout(session), ct);
            return JsonSerializer.Serialize(status, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_top_queries", ReadOnly = true, Idempotent = true)]
    [Description("Lists the top resource-consuming queries from Query Store over a time window, ranked by duration, cpu, logical_reads, writes, memory, or executions.")]
    public async Task<CallToolResult> QueryStoreTopQueriesAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Ranking metric: 'duration' (default), 'cpu', 'logical_reads', 'writes', 'memory', or 'executions'.")] string? metric = "duration",
        [Description("Maximum number of queries to return (1-50, default 10).")] int? top = 10,
        [Description("Lookback window in hours (1-8760, default 24). Ignored if startUtc/endUtc are provided.")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        if (!QueryStoreService.IsValidMetric(metric, out var normalizedMetric))
        {
            return Error("Invalid metric. Use one of: duration, cpu, logical_reads, writes, memory, executions.");
        }

        if (!QueryStoreService.IsValidTop(top, out var resolvedTop))
        {
            return Error($"top must be between 1 and {QueryStoreService.MaxTop}.");
        }

        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_top_queries", connectionId, async session =>
        {
            var res = await _queryStore.GetTopQueriesAsync(session, normalizedMetric, resolvedTop, start, end, GetTimeout(session), ct);
            return JsonSerializer.Serialize(res, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_query_details", ReadOnly = true, Idempotent = true)]
    [Description("Returns the SQL text, compile metadata, and aggregated runtime metrics for a specific Query Store queryId.")]
    public async Task<CallToolResult> QueryStoreQueryDetailsAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("Lookback window in hours (1-8760, default 24).")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_query_details", connectionId, async session =>
        {
            var details = await _queryStore.GetQueryDetailsAsync(session, queryId, start, end, GetTimeout(session), ct);
            if (details is null)
            {
                throw new InvalidOperationException($"Query Store queryId {queryId} was not found in database '{session.CurrentDatabase}'.");
            }
            return JsonSerializer.Serialize(details, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_history", ReadOnly = true, Idempotent = true)]
    [Description("Returns time-bucketed execution history (executions, duration, CPU, logical reads, plan count) for a Query Store queryId.")]
    public async Task<CallToolResult> QueryStoreHistoryAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("Lookback window in hours (1-8760, default 24).")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_history", connectionId, async session =>
        {
            var history = await _queryStore.GetQueryHistoryAsync(session, queryId, start, end, GetTimeout(session), ct);
            return JsonSerializer.Serialize(history, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_wait_stats", ReadOnly = true, Idempotent = true)]
    [Description("Returns top Query Store wait categories for the active database or a specific queryId (SQL Server 2017+ / Azure SQL Database).")]
    public async Task<CallToolResult> QueryStoreWaitStatsAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("Optional Query Store query_id to filter waits for a single query.")] long? queryId = null,
        [Description("Lookback window in hours (1-8760, default 24).")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_wait_stats", connectionId, async session =>
        {
            var waits = await _queryStore.GetWaitStatsAsync(session, queryId, start, end, GetTimeout(session), ct);
            return JsonSerializer.Serialize(waits, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_plans", ReadOnly = true, Idempotent = true)]
    [Description("Lists all Query Store execution plans for a queryId, including runtime metrics, forced status, and any force failures.")]
    public async Task<CallToolResult> QueryStorePlansAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("Lookback window in hours (1-8760, default 24).")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_plans", connectionId, async session =>
        {
            var plans = await _queryStore.GetPlansAsync(session, queryId, start, end, GetTimeout(session), ct);
            return JsonSerializer.Serialize(new
            {
                database = session.CurrentDatabase,
                queryId,
                window = new { startUtc = start.ToString("O"), endUtc = end.ToString("O") },
                count = plans.Count,
                plans
            }, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_analyze_query", ReadOnly = true, Idempotent = true)]
    [Description("Returns a consolidated Query Store diagnostic for a queryId: query details, execution plans, wait stats, and regression/multi-plan flags.")]
    public async Task<CallToolResult> QueryStoreAnalyzeQueryAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("Lookback window in hours (1-8760, default 24).")] int? hours = 24,
        [Description("Optional UTC window start (ISO 8601).")] string? startUtc = null,
        [Description("Optional UTC window end (ISO 8601).")] string? endUtc = null,
        CancellationToken ct = default)
    {
        var (ok, start, end, winErr) = QueryStoreService.ResolveWindow(hours, startUtc, endUtc);
        if (!ok)
        {
            return Error(winErr!);
        }

        return await ExecuteQueryStoreReadAsync("mssql_query_store_analyze_query", connectionId, async session =>
        {
            var timeout = GetTimeout(session);
            var details = await _queryStore.GetQueryDetailsAsync(session, queryId, start, end, timeout, ct);
            if (details is null)
            {
                throw new InvalidOperationException($"Query Store queryId {queryId} was not found in database '{session.CurrentDatabase}'.");
            }

            var plans = await _queryStore.GetPlansAsync(session, queryId, start, end, timeout, ct);
            var waits = await _queryStore.GetWaitStatsAsync(session, queryId, start, end, timeout, ct);

            var payload = new
            {
                details,
                diagnostics = new
                {
                    multiplePlansDetected = plans.Count > 1,
                    hasForcedPlan = plans.Any(p => p.IsForcedPlan),
                    hasForceFailures = plans.Any(p => p.ForceFailureCount > 0)
                },
                plans,
                waits
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_verify_forced_plan", ReadOnly = true, Idempotent = true)]
    [Description("Verifies whether a forced plan in Query Store is active and free of force failures for a queryId (and optional planId).")]
    public async Task<CallToolResult> QueryStoreVerifyForcedPlanAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("Optional specific Query Store plan_id.")] long? planId = null,
        CancellationToken ct = default)
    {
        return await ExecuteQueryStoreReadAsync("mssql_query_store_verify_forced_plan", connectionId, async session =>
        {
            var result = await _queryStore.VerifyForcedPlanAsync(session, queryId, planId, GetTimeout(session), ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }, ct);
    }

    [McpServerTool(Name = "mssql_query_store_force_plan", ReadOnly = false)]
    [Description("Forces or unforces a Query Store execution plan using sp_query_store_force_plan / sp_query_store_unforce_plan. Blocked in --readonly mode (requires --readwrite).")]
    public async Task<CallToolResult> QueryStoreForcePlanAsync(
        [Description("The connectionId returned by mssql_connect.")] string connectionId,
        [Description("The Query Store query_id.")] long queryId,
        [Description("The Query Store plan_id.")] long planId,
        [Description("Action to perform: 'force' (default) or 'unforce'.")] string? action = "force",
        CancellationToken ct = default)
    {
        if (_options.ReadOnly)
        {
            const string msg = "Blocked in --readonly mode: forcing or unforcing Query Store plans requires --readwrite.";
            _audit.Log("mssql_query_store_force_plan", "blocked", connectionId, detail: msg);
            return Error(msg);
        }

        if (!string.Equals(action, "force", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "unforce", StringComparison.OrdinalIgnoreCase))
        {
            return Error("Invalid action. Use 'force' or 'unforce'.");
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

            var res = await _queryStore.ApplyPlanForcingAsync(session, queryId, planId, action!, GetTimeout(session), ct);
            _audit.Log(
                "mssql_query_store_force_plan",
                "ok",
                session.ConnectionId,
                session.Profile.DisplayName,
                session.Profile.Server,
                session.CurrentDatabase,
                $"action={action};queryId={queryId};planId={planId}");

            return Ok(JsonSerializer.Serialize(res, JsonOptions));
        }
        catch (Exception ex)
        {
            _audit.Log(
                "mssql_query_store_force_plan",
                "error",
                session.ConnectionId,
                session.Profile.DisplayName,
                session.Profile.Server,
                session.CurrentDatabase,
                ex.Message);
            return Error($"Query Store plan forcing error: {ex.Message}");
        }
    }

    private int GetTimeout(ActiveSession session) =>
        session.Profile.CommandTimeout > 0
            ? session.Profile.CommandTimeout
            : _options.DefaultQueryTimeoutSeconds;

    private async Task<CallToolResult> ExecuteQueryStoreReadAsync(
        string toolName,
        string connectionId,
        Func<ActiveSession, Task<string>> action,
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

            var output = await action(session);
            _audit.Log(toolName, "ok", session.ConnectionId, session.Profile.DisplayName, session.Profile.Server, session.CurrentDatabase);
            return Ok(output);
        }
        catch (Exception ex)
        {
            _audit.Log(toolName, "error", session.ConnectionId, session.Profile.DisplayName, session.Profile.Server, session.CurrentDatabase, ex.Message);
            return Error($"Query Store error: {ex.Message}");
        }
    }
}
