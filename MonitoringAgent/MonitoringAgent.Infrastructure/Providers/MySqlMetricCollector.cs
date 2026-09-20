using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using MySqlConnector;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// MySQL/MariaDB instance and per-database metric collector.
    /// All GLOBAL STATUS values are cumulative since server start.
    /// </summary>
    public class MySqlMetricCollector : IMetricCollector
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public MySqlMetricCollector(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public async Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default)
        {
            var metric = new InstanceMetricDto { CollectedAt = DateTime.UtcNow, ProviderType = "MySql" };

            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // ── 1. Version & hostname ─────────────────────────────────
                await using (var cmdVer = new MySqlCommand("SELECT @@version, @@hostname", conn))
                await using (var rVer = await cmdVer.ExecuteReaderAsync(ct))
                {
                    if (await rVer.ReadAsync(ct))
                        metric.ServerName = $"MySQL {rVer.GetString(0)} on {rVer.GetString(1)}";
                    await rVer.CloseAsync();
                }

                // ── 2. Global STATUS variables ────────────────────────────
                var status = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                const string statusSql = @"
                    SHOW GLOBAL STATUS WHERE Variable_name IN (
                        'Threads_connected',
                        'Threads_running',
                        'Queries',
                        'Questions',
                        'Slow_queries',
                        'Innodb_buffer_pool_read_requests',
                        'Innodb_buffer_pool_reads',
                        'Innodb_buffer_pool_pages_total',
                        'Innodb_buffer_pool_pages_free',
                        'Innodb_data_pending_reads',
                        'Innodb_data_pending_writes',
                        'Innodb_data_read',
                        'Innodb_data_written',
                        'Innodb_log_waits',
                        'Com_select',
                        'Com_insert',
                        'Com_update',
                        'Com_delete',
                        'Aborted_connects',
                        'Connection_errors_max_connections'
                    )";
                await using (var cmdSt = new MySqlCommand(statusSql, conn))
                await using (var rSt = await cmdSt.ExecuteReaderAsync(ct))
                {
                    while (await rSt.ReadAsync(ct))
                        status[rSt.GetString(0)] = Convert.ToDouble(rSt.GetValue(1));
                    await rSt.CloseAsync();
                }

                metric.TotalConnections = (long)status.GetValueOrDefault("Threads_connected");
                metric.BatchRequestsPerSec = (long)status.GetValueOrDefault("Queries");

                // InnoDB Buffer Pool Cache Hit Ratio
                double req   = status.GetValueOrDefault("Innodb_buffer_pool_read_requests");
                double reads = status.GetValueOrDefault("Innodb_buffer_pool_reads");
                if (req > 0) metric.BufferCacheHitRatio = (1.0 - reads / req) * 100.0;

                // Buffer pool utilization (as a % of DatabaseIoPercent proxy)
                double pndR = status.GetValueOrDefault("Innodb_data_pending_reads");
                double pndW = status.GetValueOrDefault("Innodb_data_pending_writes");
                metric.DatabaseIoPercent = pndR + pndW > 0
                    ? Math.Min(100, (pndR + pndW) * 10.0)  // heuristic
                    : 0;

                // Slow queries as a wait-stat signal
                double slowQ = status.GetValueOrDefault("Slow_queries");
                if (slowQ > 0)
                    metric.TopWaitStats.Add(new WaitStatDto
                    {
                        WaitType    = "SLOW_QUERY_COUNT",
                        WaitTimeMs  = (long)slowQ,
                        WaitCount   = (long)slowQ
                    });

                // InnoDB Log wait (writer blocked waiting for log buffer to flush)
                double logWaits = status.GetValueOrDefault("Innodb_log_waits");
                if (logWaits > 0)
                    metric.TopWaitStats.Add(new WaitStatDto
                    {
                        WaitType   = "INNODB_LOG_WAIT",
                        WaitTimeMs = (long)logWaits
                    });

                // ── 3. Global VARIABLES ───────────────────────────────────
                const string varSql = @"
                    SELECT @@max_connections,
                           @@innodb_thread_concurrency,
                           @@innodb_buffer_pool_size,
                           @@innodb_log_buffer_size,
                           @@long_query_time,
                           @@slow_query_log";

                await using (var cmdVar = new MySqlCommand(varSql, conn))
                await using (var rVar = await cmdVar.ExecuteReaderAsync(ct))
                {
                    if (await rVar.ReadAsync(ct))
                    {
                        int maxConn = rVar.GetInt32(0);
                        metric.MaxDop          = rVar.GetInt32(1);
                        metric.MemoryTargetMb  = rVar.GetInt64(2) / 1048576.0;

                        // Connection saturation %
                        double threadsConn = status.GetValueOrDefault("Threads_connected");
                        metric.WorkerUtilizationPercent = maxConn > 0
                            ? Math.Round(threadsConn / maxConn * 100.0, 1) : 0;
                    }
                    await rVar.CloseAsync();
                }

                // ── 4. SysAdmin / security ────────────────────────────────
                await using (var cmdSa = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.USER_PRIVILEGES WHERE PRIVILEGE_TYPE='SUPER'", conn))
                {
                    metric.SysAdminCount = Convert.ToInt32(await cmdSa.ExecuteScalarAsync(ct) ?? 0);
                }

                _logger.LogInformation(
                    "MySQL [{Svr}] — Conn: {Conn}/{MaxConn}%, Cache: {Cache:F1}%",
                    metric.ServerName, metric.TotalConnections,
                    metric.WorkerUtilizationPercent, metric.BufferCacheHitRatio);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect MySQL instance metrics");
            }

            return metric;
        }

        public async Task<List<DatabaseMetricDto>> CollectDatabaseMetricsAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            var results = new List<DatabaseMetricDto>();

            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                foreach (var db in databases)
                {
                    var metric = new DatabaseMetricDto
                    {
                        DatabaseName = db,
                        CollectedAt  = DateTime.UtcNow,
                        ProviderType = "MySql"
                    };

                    // ── Size (data + index) and free space ───────────────
                    const string sizeSql = @"
                        SELECT
                            COALESCE(SUM(data_length + index_length), 0) / 1048576.0 AS size_mb,
                            COALESCE(SUM(data_free),                  0) / 1048576.0 AS free_mb,
                            COUNT(*)                                                   AS table_count
                        FROM information_schema.TABLES
                        WHERE table_schema = @db AND table_type = 'BASE TABLE'";

                    await using (var cmd = new MySqlCommand(sizeSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@db", db);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            metric.DatabaseSizeMb      = r.GetDouble(0);
                            metric.DataFileSpaceFreeMb = r.GetDouble(1);
                        }
                        await r.CloseAsync();
                    }

                    // ── Active sessions for this schema ──────────────────
                    const string sessSql = @"
                        SELECT COUNT(*) FROM information_schema.PROCESSLIST
                        WHERE db = @db AND command != 'Sleep'";
                    await using (var cmd = new MySqlCommand(sessSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@db", db);
                        metric.ActiveSessions = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
                    }

                    // ── Aggregate IO waits from performance_schema per schema ──
                    const string ioSql = @"
                        SELECT
                            COALESCE(SUM(sum_timer_read)  / 1e9, 0)  AS read_wait_ms,
                            COALESCE(SUM(sum_timer_write) / 1e9, 0)  AS write_wait_ms,
                            COALESCE(SUM(count_read),  0)            AS read_count,
                            COALESCE(SUM(count_write), 0)            AS write_count
                        FROM performance_schema.table_io_waits_summary_by_table
                        WHERE object_schema = @db";
                    await using (var cmd = new MySqlCommand(ioSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@db", db);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            double rdMs = r.GetDouble(0);
                            double wrMs = r.GetDouble(1);
                            long   rdCt = r.GetInt64(2);
                            long   wrCt = r.GetInt64(3);

                            metric.IoReadLatencyMs      = (long)rdMs;
                            metric.IoWriteLatencyMs     = (long)wrMs;
                            metric.AvgIoReadLatencyMs   = rdCt > 0 ? rdMs / rdCt : 0;
                            metric.AvgIoWriteLatencyMs  = wrCt > 0 ? wrMs / wrCt : 0;
                        }
                        await r.CloseAsync();
                    }

                    // ── Last backup (MySQL has no built-in backup tracking) ──
                    // Use general_log or binary log position as a proxy.
                    // DaysSinceLastBackup = -1 means "unknown" (not an error).
                    metric.DaysSinceLastBackup = -1;

                    results.Add(metric);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect MySQL database metrics");
            }

            return results;
        }
    }
}
