using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using MssqlCl.Models;

namespace MssqlCl.Database;

/// <summary>
/// Provides Query Store diagnostic queries and plan-forcing operations against SQL Server 2016+ and Azure SQL Database.
/// </summary>
public sealed class QueryStoreService
{
    public const int DefaultHours = 24;
    public const int MaxHours = 365 * 24;
    public const int DefaultTop = 10;
    public const int MaxTop = 50;
    public const int MaxHistoryRows = 48;

    private static readonly HashSet<string> ValidMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        "duration",
        "cpu",
        "logical_reads",
        "writes",
        "memory",
        "executions"
    };

    /// <summary>
    /// Validates and resolves a UTC time window from either <paramref name="hours"/> or explicit <paramref name="startUtc"/>/<paramref name="endUtc"/>.
    /// </summary>
    public static (bool Ok, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Error) ResolveWindow(
        int? hours,
        string? startUtc,
        string? endUtc,
        DateTimeOffset? nowUtc = null)
    {
        if (!string.IsNullOrWhiteSpace(startUtc) || !string.IsNullOrWhiteSpace(endUtc))
        {
            if (!DateTimeOffset.TryParse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
                !DateTimeOffset.TryParse(endUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
            {
                return (false, default, default, "Both startUtc and endUtc must be valid ISO 8601 timestamps when either is provided.");
            }

            if (start >= end)
            {
                return (false, default, default, "startUtc must be strictly earlier than endUtc.");
            }

            return (true, start, end, null);
        }

        var resolvedHours = hours ?? DefaultHours;
        if (resolvedHours < 1 || resolvedHours > MaxHours)
        {
            return (false, default, default, $"hours must be between 1 and {MaxHours}.");
        }

        var endNow = nowUtc ?? DateTimeOffset.UtcNow;
        return (true, endNow.AddHours(-resolvedHours), endNow, null);
    }

    /// <summary>
    /// Validates the ranking metric for top-queries lookups.
    /// </summary>
    public static bool IsValidMetric(string? metric, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(metric) ? "duration" : metric.Trim().ToLowerInvariant();
        return ValidMetrics.Contains(normalized);
    }

    /// <summary>
    /// Validates the <c>top</c> row limit.
    /// </summary>
    public static bool IsValidTop(int? top, out int resolved)
    {
        resolved = top ?? DefaultTop;
        return resolved >= 1 && resolved <= MaxTop;
    }

    public async Task<object> GetStatusAsync(ActiveSession session, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = """
            SELECT
                actual_state_desc,
                desired_state_desc,
                readonly_reason,
                current_storage_size_mb,
                max_storage_size_mb,
                flush_interval_seconds,
                interval_length_minutes,
                stale_query_threshold_days,
                max_plans_per_query,
                query_capture_mode_desc,
                size_based_cleanup_mode_desc
            FROM sys.database_query_store_options;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new
            {
                database = session.CurrentDatabase,
                available = false,
                message = "No row returned from sys.database_query_store_options."
            };
        }

        var actualState = reader.IsDBNull(0) ? "UNKNOWN" : reader.GetString(0);
        return new
        {
            database = session.CurrentDatabase,
            available = !string.Equals(actualState, "OFF", StringComparison.OrdinalIgnoreCase),
            actualState,
            desiredState = reader.IsDBNull(1) ? null : reader.GetString(1),
            readOnlyReason = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
            currentStorageSizeMb = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
            maxStorageSizeMb = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
            flushIntervalSeconds = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
            intervalLengthMinutes = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
            staleQueryThresholdDays = reader.IsDBNull(7) ? 0L : Convert.ToInt64(reader.GetValue(7)),
            maxPlansPerQuery = reader.IsDBNull(8) ? 0L : Convert.ToInt64(reader.GetValue(8)),
            queryCaptureMode = reader.IsDBNull(9) ? null : reader.GetString(9),
            sizeBasedCleanupMode = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
    }

    public async Task<object> GetTopQueriesAsync(
        ActiveSession session,
        string metric,
        int top,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var orderBy = metric switch
        {
            "cpu" => "SUM(rs.count_executions * rs.avg_cpu_time) DESC",
            "logical_reads" => "SUM(rs.count_executions * rs.avg_logical_io_reads) DESC",
            "writes" => "SUM(rs.count_executions * rs.avg_logical_io_writes) DESC",
            "memory" => "MAX(rs.max_query_max_used_memory) DESC",
            "executions" => "SUM(rs.count_executions) DESC",
            _ => "SUM(rs.count_executions * rs.avg_duration) DESC"
        };

        var sql = $"""
            SELECT TOP (@top)
                q.query_id,
                OBJECT_SCHEMA_NAME(q.object_id) AS schema_name,
                OBJECT_NAME(q.object_id) AS object_name,
                LEFT(qt.query_sql_text, 500) AS query_sql_text,
                SUM(rs.count_executions) AS executions,
                CAST(SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS DECIMAL(18,2)) AS avg_duration_ms,
                CAST(SUM(rs.count_executions * rs.avg_duration) / 1000.0 AS DECIMAL(18,2)) AS total_duration_ms,
                CAST(SUM(rs.count_executions * rs.avg_cpu_time) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS DECIMAL(18,2)) AS avg_cpu_ms,
                CAST(SUM(rs.count_executions * rs.avg_cpu_time) / 1000.0 AS DECIMAL(18,2)) AS total_cpu_ms,
                CAST(SUM(rs.count_executions * rs.avg_logical_io_reads) / NULLIF(SUM(rs.count_executions), 0) AS DECIMAL(18,2)) AS avg_logical_reads,
                CAST(SUM(rs.count_executions * rs.avg_logical_io_writes) / NULLIF(SUM(rs.count_executions), 0) AS DECIMAL(18,2)) AS avg_logical_writes,
                CAST(MAX(rs.max_query_max_used_memory) * 8.0 AS DECIMAL(18,2)) AS max_memory_kb,
                COUNT(DISTINCT p.plan_id) AS plan_count,
                MAX(CAST(p.is_forced_plan AS INT)) AS has_forced_plan
            FROM sys.query_store_runtime_stats rs
            INNER JOIN sys.query_store_runtime_stats_interval rsi
                ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
            INNER JOIN sys.query_store_plan p
                ON rs.plan_id = p.plan_id
            INNER JOIN sys.query_store_query q
                ON p.query_id = q.query_id
            INNER JOIN sys.query_store_query_text qt
                ON q.query_text_id = qt.query_text_id
            WHERE rsi.start_time >= @startUtc
              AND rsi.end_time <= @endUtc
            GROUP BY q.query_id, q.object_id, LEFT(qt.query_sql_text, 500)
            ORDER BY {orderBy};
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@top", top);
        cmd.Parameters.AddWithValue("@startUtc", startUtc);
        cmd.Parameters.AddWithValue("@endUtc", endUtc);

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schemaName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var objName = reader.IsDBNull(2) ? null : reader.GetString(2);
            items.Add(new
            {
                queryId = reader.GetInt64(0),
                objectName = objName is null ? null : (schemaName is null ? objName : $"{schemaName}.{objName}"),
                querySqlText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                executions = reader.GetInt64(4),
                avgDurationMs = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5),
                totalDurationMs = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
                avgCpuMs = reader.IsDBNull(7) ? 0m : reader.GetDecimal(7),
                totalCpuMs = reader.IsDBNull(8) ? 0m : reader.GetDecimal(8),
                avgLogicalReads = reader.IsDBNull(9) ? 0m : reader.GetDecimal(9),
                avgLogicalWrites = reader.IsDBNull(10) ? 0m : reader.GetDecimal(10),
                maxMemoryKb = reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                planCount = reader.GetInt32(12),
                hasForcedPlan = !reader.IsDBNull(13) && reader.GetInt32(13) == 1
            });
        }

        return new
        {
            database = session.CurrentDatabase,
            metric,
            top,
            window = new { startUtc = startUtc.ToString("O"), endUtc = endUtc.ToString("O") },
            count = items.Count,
            queries = items
        };
    }

    public async Task<object?> GetQueryDetailsAsync(
        ActiveSession session,
        long queryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                q.query_id,
                OBJECT_SCHEMA_NAME(q.object_id) AS schema_name,
                OBJECT_NAME(q.object_id) AS object_name,
                LEFT(qt.query_sql_text, 4000) AS query_sql_text,
                q.initial_compile_start_time,
                q.last_compile_start_time,
                q.last_execution_time,
                q.count_compiles,
                ISNULL(SUM(rs.count_executions), 0) AS executions,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0, 0) AS DECIMAL(18,2)) AS avg_duration_ms,
                CAST(ISNULL(MAX(rs.max_duration) / 1000.0, 0) AS DECIMAL(18,2)) AS max_duration_ms,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_cpu_time) / NULLIF(SUM(rs.count_executions), 0) / 1000.0, 0) AS DECIMAL(18,2)) AS avg_cpu_ms,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_logical_io_reads) / NULLIF(SUM(rs.count_executions), 0), 0) AS DECIMAL(18,2)) AS avg_logical_reads,
                COUNT(DISTINCT p.plan_id) AS plan_count,
                ISNULL(MAX(CAST(p.is_forced_plan AS INT)), 0) AS has_forced_plan
            FROM sys.query_store_query q
            INNER JOIN sys.query_store_query_text qt
                ON q.query_text_id = qt.query_text_id
            LEFT JOIN sys.query_store_plan p
                ON q.query_id = p.query_id
            LEFT JOIN sys.query_store_runtime_stats rs
                ON p.plan_id = rs.plan_id
               AND EXISTS (
                   SELECT 1
                   FROM sys.query_store_runtime_stats_interval rsi
                   WHERE rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
                     AND rsi.start_time >= @startUtc
                     AND rsi.end_time <= @endUtc
               )
            WHERE q.query_id = @queryId
            GROUP BY
                q.query_id,
                q.object_id,
                LEFT(qt.query_sql_text, 4000),
                q.initial_compile_start_time,
                q.last_compile_start_time,
                q.last_execution_time,
                q.count_compiles;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@queryId", queryId);
        cmd.Parameters.AddWithValue("@startUtc", startUtc);
        cmd.Parameters.AddWithValue("@endUtc", endUtc);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var schemaName = reader.IsDBNull(1) ? null : reader.GetString(1);
        var objName = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new
        {
            database = session.CurrentDatabase,
            queryId = reader.GetInt64(0),
            objectName = objName is null ? null : (schemaName is null ? objName : $"{schemaName}.{objName}"),
            querySqlText = reader.IsDBNull(3) ? "" : reader.GetString(3),
            initialCompileStartTime = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(4),
            lastCompileStartTime = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(5),
            lastExecutionTime = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(6),
            countCompiles = reader.IsDBNull(7) ? 0L : reader.GetInt64(7),
            window = new { startUtc = startUtc.ToString("O"), endUtc = endUtc.ToString("O") },
            executions = reader.GetInt64(8),
            avgDurationMs = reader.GetDecimal(9),
            maxDurationMs = reader.GetDecimal(10),
            avgCpuMs = reader.GetDecimal(11),
            avgLogicalReads = reader.GetDecimal(12),
            planCount = reader.GetInt32(13),
            hasForcedPlan = reader.GetInt32(14) == 1
        };
    }

    public async Task<object> GetQueryHistoryAsync(
        ActiveSession session,
        long queryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var sql = $"""
            SELECT TOP ({MaxHistoryRows})
                rsi.start_time,
                rsi.end_time,
                SUM(rs.count_executions) AS executions,
                CAST(SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS DECIMAL(18,2)) AS avg_duration_ms,
                CAST(MAX(rs.max_duration) / 1000.0 AS DECIMAL(18,2)) AS max_duration_ms,
                CAST(SUM(rs.count_executions * rs.avg_cpu_time) / NULLIF(SUM(rs.count_executions), 0) / 1000.0 AS DECIMAL(18,2)) AS avg_cpu_ms,
                CAST(SUM(rs.count_executions * rs.avg_logical_io_reads) / NULLIF(SUM(rs.count_executions), 0) AS DECIMAL(18,2)) AS avg_logical_reads,
                COUNT(DISTINCT rs.plan_id) AS plan_count
            FROM sys.query_store_runtime_stats rs
            INNER JOIN sys.query_store_runtime_stats_interval rsi
                ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
            INNER JOIN sys.query_store_plan p
                ON rs.plan_id = p.plan_id
            WHERE p.query_id = @queryId
              AND rsi.start_time >= @startUtc
              AND rsi.end_time <= @endUtc
            GROUP BY rsi.start_time, rsi.end_time
            ORDER BY rsi.start_time DESC;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@queryId", queryId);
        cmd.Parameters.AddWithValue("@startUtc", startUtc);
        cmd.Parameters.AddWithValue("@endUtc", endUtc);

        var buckets = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            buckets.Add(new
            {
                startUtc = reader.GetDateTimeOffset(0).ToString("O"),
                endUtc = reader.GetDateTimeOffset(1).ToString("O"),
                executions = reader.GetInt64(2),
                avgDurationMs = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                maxDurationMs = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                avgCpuMs = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5),
                avgLogicalReads = reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
                planCount = reader.GetInt32(7)
            });
        }

        buckets.Reverse();
        return new
        {
            database = session.CurrentDatabase,
            queryId,
            window = new { startUtc = startUtc.ToString("O"), endUtc = endUtc.ToString("O") },
            count = buckets.Count,
            intervals = buckets
        };
    }

    public async Task<object> GetWaitStatsAsync(
        ActiveSession session,
        long? queryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string checkSql = "SELECT CASE WHEN OBJECT_ID('sys.query_store_wait_stats') IS NOT NULL THEN 1 ELSE 0 END;";
        await using (var checkCmd = CreateCommand(session, checkSql, timeoutSeconds))
        {
            var hasWaitStats = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct)) == 1;
            if (!hasWaitStats)
            {
                return new
                {
                    database = session.CurrentDatabase,
                    supported = false,
                    message = "sys.query_store_wait_stats is not available on this SQL Server instance (requires SQL Server 2017+ or Azure SQL Database)."
                };
            }
        }

        const string sql = """
            SELECT TOP (20)
                ws.wait_category_desc,
                SUM(ws.total_query_wait_time_ms) AS total_wait_ms,
                CAST(AVG(ws.avg_query_wait_time_ms) AS DECIMAL(18,2)) AS avg_wait_ms,
                CAST(MAX(ws.max_query_wait_time_ms) AS DECIMAL(18,2)) AS max_wait_ms
            FROM sys.query_store_wait_stats ws
            INNER JOIN sys.query_store_runtime_stats_interval rsi
                ON ws.runtime_stats_interval_id = rsi.runtime_stats_interval_id
            INNER JOIN sys.query_store_plan p
                ON ws.plan_id = p.plan_id
            WHERE rsi.start_time >= @startUtc
              AND rsi.end_time <= @endUtc
              AND (@queryId IS NULL OR p.query_id = @queryId)
            GROUP BY ws.wait_category_desc
            ORDER BY SUM(ws.total_query_wait_time_ms) DESC;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@startUtc", startUtc);
        cmd.Parameters.AddWithValue("@endUtc", endUtc);
        cmd.Parameters.AddWithValue("@queryId", queryId.HasValue ? queryId.Value : DBNull.Value);

        var waits = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            waits.Add(new
            {
                waitCategory = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0),
                totalWaitMs = reader.GetInt64(1),
                avgWaitMs = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                maxWaitMs = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)
            });
        }

        return new
        {
            database = session.CurrentDatabase,
            supported = true,
            queryId,
            window = new { startUtc = startUtc.ToString("O"), endUtc = endUtc.ToString("O") },
            count = waits.Count,
            waits
        };
    }

    public async Task<IReadOnlyList<PlanSummaryRow>> GetPlansAsync(
        ActiveSession session,
        long queryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                p.plan_id,
                p.is_forced_plan,
                p.force_failure_count,
                p.last_force_failure_reason_desc,
                p.is_parallel_plan,
                p.compatibility_level,
                p.last_execution_time,
                ISNULL(SUM(rs.count_executions), 0) AS executions,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_duration) / NULLIF(SUM(rs.count_executions), 0) / 1000.0, 0) AS DECIMAL(18,2)) AS avg_duration_ms,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_cpu_time) / NULLIF(SUM(rs.count_executions), 0) / 1000.0, 0) AS DECIMAL(18,2)) AS avg_cpu_ms,
                CAST(ISNULL(SUM(rs.count_executions * rs.avg_logical_io_reads) / NULLIF(SUM(rs.count_executions), 0), 0) AS DECIMAL(18,2)) AS avg_logical_reads
            FROM sys.query_store_plan p
            LEFT JOIN sys.query_store_runtime_stats rs
                ON p.plan_id = rs.plan_id
               AND EXISTS (
                   SELECT 1
                   FROM sys.query_store_runtime_stats_interval rsi
                   WHERE rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
                     AND rsi.start_time >= @startUtc
                     AND rsi.end_time <= @endUtc
               )
            WHERE p.query_id = @queryId
            GROUP BY
                p.plan_id,
                p.is_forced_plan,
                p.force_failure_count,
                p.last_force_failure_reason_desc,
                p.is_parallel_plan,
                p.compatibility_level,
                p.last_execution_time
            ORDER BY p.is_forced_plan DESC, executions DESC, p.plan_id ASC;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@queryId", queryId);
        cmd.Parameters.AddWithValue("@startUtc", startUtc);
        cmd.Parameters.AddWithValue("@endUtc", endUtc);

        var plans = new List<PlanSummaryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            plans.Add(new PlanSummaryRow(
                PlanId: reader.GetInt64(0),
                IsForcedPlan: !reader.IsDBNull(1) && reader.GetBoolean(1),
                ForceFailureCount: reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                LastForceFailureReason: reader.IsDBNull(3) ? null : reader.GetString(3),
                IsParallelPlan: !reader.IsDBNull(4) && reader.GetBoolean(4),
                CompatibilityLevel: reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)),
                LastExecutionTime: reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6),
                Executions: reader.GetInt64(7),
                AvgDurationMs: reader.GetDecimal(8),
                AvgCpuMs: reader.GetDecimal(9),
                AvgLogicalReads: reader.GetDecimal(10)));
        }

        return plans;
    }

    public async Task<object> VerifyForcedPlanAsync(
        ActiveSession session,
        long queryId,
        long? planId,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string sql = """
            SELECT
                p.plan_id,
                p.is_forced_plan,
                p.force_failure_count,
                p.last_force_failure_reason_desc,
                p.last_compile_start_time,
                p.last_execution_time
            FROM sys.query_store_plan p
            WHERE p.query_id = @queryId
              AND (@planId IS NULL OR p.plan_id = @planId)
            ORDER BY p.is_forced_plan DESC, p.plan_id ASC;
            """;

        await using var cmd = CreateCommand(session, sql, timeoutSeconds);
        cmd.Parameters.AddWithValue("@queryId", queryId);
        cmd.Parameters.AddWithValue("@planId", planId.HasValue ? planId.Value : DBNull.Value);

        var rows = new List<object>();
        var anyForced = false;
        var anyFailures = false;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var forced = !reader.IsDBNull(1) && reader.GetBoolean(1);
            var failures = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            if (forced) anyForced = true;
            if (failures > 0) anyFailures = true;

            rows.Add(new
            {
                planId = reader.GetInt64(0),
                isForcedPlan = forced,
                forceFailureCount = failures,
                lastForceFailureReason = reader.IsDBNull(3) ? null : reader.GetString(3),
                lastCompileStartTime = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(4),
                lastExecutionTime = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(5)
            });
        }

        var status = rows.Count == 0
            ? "plan_not_found"
            : !anyForced
                ? "not_forced"
                : anyFailures
                    ? "forced_with_failures"
                    : "verified_active";

        return new
        {
            database = session.CurrentDatabase,
            queryId,
            planId,
            status,
            plans = rows
        };
    }

    public async Task<object> ApplyPlanForcingAsync(
        ActiveSession session,
        long queryId,
        long planId,
        string action,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var proc = string.Equals(action, "unforce", StringComparison.OrdinalIgnoreCase)
            ? "sys.sp_query_store_unforce_plan"
            : "sys.sp_query_store_force_plan";

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = proc;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.Parameters.AddWithValue("@query_id", queryId);
        cmd.Parameters.AddWithValue("@plan_id", planId);

        await cmd.ExecuteNonQueryAsync(ct);

        return new
        {
            success = true,
            database = session.CurrentDatabase,
            action = string.Equals(action, "unforce", StringComparison.OrdinalIgnoreCase) ? "unforce" : "force",
            queryId,
            planId
        };
    }

    private static SqlCommand CreateCommand(ActiveSession session, string sql, int timeoutSeconds)
    {
        var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        return cmd;
    }

    public sealed record PlanSummaryRow(
        long PlanId,
        bool IsForcedPlan,
        long ForceFailureCount,
        string? LastForceFailureReason,
        bool IsParallelPlan,
        int CompatibilityLevel,
        DateTimeOffset? LastExecutionTime,
        long Executions,
        decimal AvgDurationMs,
        decimal AvgCpuMs,
        decimal AvgLogicalReads);
}
