using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using Npgsql;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// PostgreSQL instance and per-database metric collector.
    /// Compatible with PostgreSQL 12+.
    /// </summary>
    public class PostgresMetricCollector : IMetricCollector
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public PostgresMetricCollector(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public async Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default)
        {
            var metric = new InstanceMetricDto
            {
                CollectedAt  = DateTime.UtcNow,
                ProviderType = "Postgres"
            };

            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // ── 1. Version + Configuration ────────────────────────────
                const string verSql = @"
                    SELECT
                        version()                                           AS ver,
                        current_setting('max_connections')::int            AS max_conn,
                        current_setting('shared_buffers')                  AS shared_buf,
                        current_setting('work_mem')                        AS work_mem,
                        current_setting('effective_cache_size')            AS cache_sz,
                        current_setting('wal_buffers')                     AS wal_buf,
                        current_setting('checkpoint_completion_target')    AS chk_target";

                await using (var cmd = new NpgsqlCommand(verSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        metric.ServerName = r.GetString(0);
                        metric.MaxDop     = r.GetInt32(1);  // reuse MaxDop for max_connections
                    }
                    await r.CloseAsync();
                }

                // ── 2. Activity: connections, active queries, locks ───────
                const string actSql = @"
                    SELECT
                        (SELECT count(*) FROM pg_stat_activity)                 AS total_conn,
                        (SELECT count(*) FROM pg_stat_activity WHERE state = 'active') AS active_conn,
                        (SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock') AS lock_waits,
                        (SELECT count(*) FROM pg_locks WHERE NOT granted)       AS blocked_locks,
                        (SELECT count(*) FROM pg_stat_activity
                          WHERE state = 'idle in transaction'
                            AND now() - state_change > interval '5 minutes')    AS long_idle_tx";

                await using (var cmd = new NpgsqlCommand(actSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        metric.TotalConnections = r.GetInt64(0);
                        long   active    = r.GetInt64(1);
                        long   lockWaits = r.GetInt64(2);
                        long   blocked   = r.GetInt64(3);
                        long   idleTx    = r.GetInt64(4);

                        // Connection saturation
                        metric.WorkerUtilizationPercent = metric.MaxDop > 0
                            ? Math.Round(metric.TotalConnections * 100.0 / metric.MaxDop, 1) : 0;

                        if (lockWaits > 0)
                            metric.TopWaitStats.Add(new WaitStatDto
                            {
                                WaitType   = "LOCK_WAIT",
                                WaitTimeMs = lockWaits,
                                WaitCount  = lockWaits
                            });

                        if (blocked > 0)
                            metric.TopWaitStats.Add(new WaitStatDto
                            {
                                WaitType   = "BLOCKED_LOCK",
                                WaitTimeMs = blocked,
                                WaitCount  = blocked
                            });

                        if (idleTx > 0)
                            metric.TopWaitStats.Add(new WaitStatDto
                            {
                                WaitType   = "IDLE_IN_TRANSACTION",
                                WaitTimeMs = idleTx * 300000,  // 5+ min each
                                WaitCount  = idleTx
                            });
                    }
                    await r.CloseAsync();
                }

                // ── 3. Throughput: commits + rollbacks (all databases) ────
                await using (var cmd = new NpgsqlCommand(
                    "SELECT SUM(xact_commit), SUM(xact_rollback) FROM pg_stat_database", conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        metric.BatchRequestsPerSec = r.IsDBNull(0) ? 0 : Convert.ToInt64(r.GetValue(0));
                    }
                    await r.CloseAsync();
                }

                // ── 4. Buffer cache hit ratio (all databases) ─────────────
                const string cacheSql = @"
                    SELECT
                        SUM(blks_hit)::float /
                        NULLIF(SUM(blks_hit) + SUM(blks_read), 0) * 100.0 AS hit_ratio
                    FROM pg_stat_database
                    WHERE datname NOT IN ('template0','template1')";

                await using (var cmd = new NpgsqlCommand(cacheSql, conn))
                {
                    var val = await cmd.ExecuteScalarAsync(ct);
                    metric.BufferCacheHitRatio = val == DBNull.Value || val == null ? 0 : Convert.ToDouble(val);
                }

                // ── 5. Replication lag (if standby) ──────────────────────
                await using (var cmd = new NpgsqlCommand(
                    @"SELECT CASE WHEN pg_is_in_recovery()
                             THEN EXTRACT(EPOCH FROM (now() - pg_last_xact_replay_timestamp()))
                             ELSE 0 END", conn))
                {
                    var lag = await cmd.ExecuteScalarAsync(ct);
                    double lagSec = lag == DBNull.Value || lag == null ? 0 : Convert.ToDouble(lag);
                    if (lagSec > 30)
                        metric.TopWaitStats.Add(new WaitStatDto
                        {
                            WaitType   = "REPLICA_LAG_SECONDS",
                            WaitTimeMs = (long)(lagSec * 1000),
                            WaitCount  = 1
                        });
                }

                // ── 6. SysAdmin role count ────────────────────────────────
                await using (var cmd = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_roles WHERE rolsuper = true", conn))
                {
                    metric.SysAdminCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
                }

                _logger.LogInformation(
                    "Postgres [{Svr}] — Conn: {Conn} ({Sat:F1}%), Cache: {Cache:F1}%",
                    metric.ServerName, metric.TotalConnections,
                    metric.WorkerUtilizationPercent, metric.BufferCacheHitRatio);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect PostgreSQL instance metrics");
            }

            return metric;
        }

        public async Task<List<DatabaseMetricDto>> CollectDatabaseMetricsAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            var results = new List<DatabaseMetricDto>();

            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                foreach (var db in databases)
                {
                    var metric = new DatabaseMetricDto
                    {
                        DatabaseName = db,
                        CollectedAt  = DateTime.UtcNow,
                        ProviderType = "Postgres"
                    };

                    // ── Size + sessions for this database ────────────────
                    const string sizeSql = @"
                        SELECT
                            pg_database_size($1) / 1048576.0                                AS size_mb,
                            (SELECT count(*) FROM pg_stat_activity WHERE datname = $1)      AS active_sess,
                            (SELECT count(*) FROM pg_stat_activity
                             WHERE datname = $1 AND state = 'active')                       AS running_queries";

                    await using (var cmd = new NpgsqlCommand(sizeSql, conn))
                    {
                        cmd.Parameters.AddWithValue(db);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            metric.DatabaseSizeMb = r.GetDouble(0);
                            metric.ActiveSessions = Convert.ToInt32(r.GetValue(1));
                        }
                        await r.CloseAsync();
                    }

                    // ── IO stats: block reads/writes from pg_statio_user_tables ──
                    const string ioSql = @"
                        SELECT
                            COALESCE(SUM(heap_blks_read),  0) AS heap_reads,
                            COALESCE(SUM(heap_blks_hit),   0) AS heap_hits,
                            COALESCE(SUM(idx_blks_read),   0) AS idx_reads,
                            COALESCE(SUM(idx_blks_hit),    0) AS idx_hits,
                            COALESCE(SUM(toast_blks_read), 0) AS toast_reads
                        FROM pg_statio_user_tables";

                    await using (var cmd = new NpgsqlCommand(ioSql, conn))
                    await using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        if (await r.ReadAsync(ct))
                        {
                            long heapReads = r.GetInt64(0);
                            long heapHits  = r.GetInt64(1);
                            long idxReads  = r.GetInt64(2);
                            long idxHits   = r.GetInt64(3);

                            metric.IoReadBytes  = heapReads * 8192L;   // 8KB pages
                            metric.IoWriteBytes = idxReads  * 8192L;

                            // Block cache ratio
                            double total = heapReads + heapHits + idxReads + idxHits;
                            double hits  = heapHits  + idxHits;
                            if (total > 0)
                                metric.AvgIoReadLatencyMs = (1.0 - hits / total) * 100.0; // miss ratio as AvgRead proxy
                        }
                        await r.CloseAsync();
                    }

                    // ── WAL / log usage: check pg_wal_progress if available ──
                    // Use log file size as a proxy (only superuser can see pg_ls_waldir)
                    // Fallback: log usage = 0 (safe, no crash)
                    metric.LogSizeMb = 0;

                    // ── Autovacuum health: tables with huge dead tuple counts ──
                    const string vacSql = @"
                        SELECT SUM(n_dead_tup) FROM pg_stat_user_tables";
                    await using (var cmd = new NpgsqlCommand(vacSql, conn))
                    {
                        var deadTup = await cmd.ExecuteScalarAsync(ct);
                        double totalDead = deadTup == DBNull.Value || deadTup == null
                            ? 0 : Convert.ToDouble(deadTup);
                        // Store in VlfCount (repurposed) as dead tuples / 1000
                        metric.VlfCount = (int)Math.Min(int.MaxValue, totalDead / 1000.0);
                    }

                    results.Add(metric);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect PostgreSQL database metrics");
            }

            return results;
        }
    }
}
