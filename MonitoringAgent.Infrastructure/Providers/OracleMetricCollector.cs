using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using Oracle.ManagedDataAccess.Client;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// Oracle Database instance and per-tablespace metric collector.
    /// Requires SELECT on V$SESSION, V$SYSSTAT, V$SGA, V$PGA_TARGET_ADVICE,
    /// V$SYSTEM_WAIT_CLASS, DBA_DATA_FILES, DBA_FREE_SPACE, V$TEMPFILE.
    /// </summary>
    public class OracleMetricCollector : IMetricCollector
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public OracleMetricCollector(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        public async Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default)
        {
            var metric = new InstanceMetricDto
            {
                CollectedAt  = DateTime.UtcNow,
                ProviderType = "Oracle"
            };

            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                // ── 1. Version banner ─────────────────────────────────────
                await using (var cmd = new OracleCommand(
                    "SELECT banner FROM v$version WHERE ROWNUM = 1", conn))
                {
                    metric.ServerName = (string)(await cmd.ExecuteScalarAsync(ct) ?? "Oracle");
                }

                // ── 2. Total sessions & active sessions ───────────────────
                const string sessSql = @"
                    SELECT
                        (SELECT count(*) FROM v$session)                          AS total_sess,
                        (SELECT count(*) FROM v$session WHERE status = 'ACTIVE'
                            AND type != 'BACKGROUND')                             AS active_sess,
                        (SELECT value FROM v$parameter WHERE name = 'sessions')   AS max_sess";

                await using (var cmd = new OracleCommand(sessSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        metric.TotalConnections = Convert.ToInt64(r.GetValue(0));
                        long active = Convert.ToInt64(r.GetValue(1));
                        long maxSess = Convert.ToInt64(r.GetValue(2));
                        metric.WorkerUtilizationPercent = maxSess > 0
                            ? Math.Round(metric.TotalConnections * 100.0 / maxSess, 1) : 0;
                    }
                }

                // ── 3. Execute count (throughput) ─────────────────────────
                await using (var cmd = new OracleCommand(
                    "SELECT value FROM v$sysstat WHERE name = 'execute count'", conn))
                {
                    metric.BatchRequestsPerSec = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0);
                }

                // ── 4. Buffer cache hit ratio ─────────────────────────────
                const string cacheSql = @"
                    SELECT ROUND((1 - (phy.value / NULLIF(cur.value + con.value, 0))) * 100, 2)
                    FROM v$sysstat phy, v$sysstat cur, v$sysstat con
                    WHERE phy.name = 'physical reads'
                      AND cur.name = 'db block gets'
                      AND con.name = 'consistent gets'";
                await using (var cmd = new OracleCommand(cacheSql, conn))
                {
                    var val = await cmd.ExecuteScalarAsync(ct);
                    metric.BufferCacheHitRatio = val == DBNull.Value || val == null
                        ? 0 : Convert.ToDouble(val);
                }

                // ── 5. SGA + PGA memory usage ─────────────────────────────
                const string sgaSql = @"
                    SELECT
                        (SELECT SUM(value) / 1048576 FROM v$sga)         AS sga_mb,
                        (SELECT value / 1048576 FROM v$pgastat
                         WHERE name = 'total PGA allocated')             AS pga_mb";
                await using (var cmd = new OracleCommand(sgaSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        double sgaMb = r.IsDBNull(0) ? 0 : Convert.ToDouble(r.GetValue(0));
                        double pgaMb = r.IsDBNull(1) ? 0 : Convert.ToDouble(r.GetValue(1));
                        metric.SqlMemoryUsageMb = sgaMb + pgaMb;
                        metric.MemoryTargetMb   = sgaMb;  // SGA as target reference
                    }
                }

                // ── 6. Temp tablespace usage ──────────────────────────────
                const string tempSql = @"
                    SELECT COALESCE(SUM(bytes) / 1048576, 0) FROM v$temp_extent_pool";
                await using (var cmd = new OracleCommand(tempSql, conn))
                {
                    metric.TempDbSizeMb = Convert.ToDouble(await cmd.ExecuteScalarAsync(ct) ?? 0);
                }

                // ── 7. System-level wait classes (top waits) ──────────────
                const string waitSql = @"
                    SELECT * FROM (
                        SELECT
                            wait_class,
                            total_waits,
                            ROUND(time_waited / 100, 0) AS wait_time_ms
                        FROM v$system_wait_class
                        WHERE wait_class NOT IN ('Idle')
                        ORDER BY time_waited DESC
                    ) WHERE ROWNUM <= 10";

                await using (var cmd = new OracleCommand(waitSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        metric.TopWaitStats.Add(new WaitStatDto
                        {
                            WaitType   = r.GetString(0),
                            WaitCount  = Convert.ToInt64(r.GetValue(1)),
                            WaitTimeMs = Convert.ToInt64(r.GetValue(2))
                        });
                    }
                }

                // ── 8. CPU usage via OS stats ─────────────────────────────
                const string cpuSql = @"
                    SELECT
                        NVL((SELECT value FROM v$osstat WHERE stat_name = 'BUSY_TIME'), 0)    AS busy,
                        NVL((SELECT value FROM v$osstat WHERE stat_name = 'IDLE_TIME'), 0)    AS idle
                    FROM dual";
                await using (var cmd = new OracleCommand(cpuSql, conn))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        double busy = Convert.ToDouble(r.GetValue(0));
                        double idle = Convert.ToDouble(r.GetValue(1));
                        double total = busy + idle;
                        metric.CpuUsagePercent = total > 0
                            ? Math.Round(busy / total * 100.0, 1) : 0;
                    }
                }

                _logger.LogInformation(
                    "Oracle [{Svr}] — Sess: {Conn} ({Sat:F1}%), Cache: {Cache:F1}%, SGA+PGA: {Mem:F0}MB",
                    metric.ServerName, metric.TotalConnections,
                    metric.WorkerUtilizationPercent, metric.BufferCacheHitRatio,
                    metric.SqlMemoryUsageMb);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect Oracle instance metrics");
            }

            return metric;
        }

        public async Task<List<DatabaseMetricDto>> CollectDatabaseMetricsAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            var results = new List<DatabaseMetricDto>();

            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                // Oracle treats tablespaces (or PDBs in multitenant) as logical units.
                // We iterate over configured "databases" which map to tablespace names.
                foreach (var ts in databases)
                {
                    var metric = new DatabaseMetricDto
                    {
                        DatabaseName = ts,
                        CollectedAt  = DateTime.UtcNow,
                        ProviderType = "Oracle"
                    };

                    // ── Size & free space in tablespace ───────────────────
                    const string tsSql = @"
                        SELECT
                            COALESCE(d.total_mb, 0)       AS total_mb,
                            COALESCE(f.free_mb,  0)       AS free_mb,
                            COALESCE(d.total_mb - f.free_mb, 0) AS used_mb
                        FROM
                            (SELECT tablespace_name, SUM(bytes)/1048576 AS total_mb
                             FROM dba_data_files
                             WHERE tablespace_name = UPPER(:ts)
                             GROUP BY tablespace_name) d
                        LEFT JOIN
                            (SELECT tablespace_name, SUM(bytes)/1048576 AS free_mb
                             FROM dba_free_space
                             WHERE tablespace_name = UPPER(:ts2)
                             GROUP BY tablespace_name) f
                        ON d.tablespace_name = f.tablespace_name";

                    await using (var cmd = new OracleCommand(tsSql, conn))
                    {
                        cmd.Parameters.Add("ts", ts);
                        cmd.Parameters.Add("ts2", ts);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            metric.DatabaseSizeMb      = r.IsDBNull(0) ? 0 : Convert.ToDouble(r.GetValue(0));
                            metric.DataFileSpaceFreeMb = r.IsDBNull(1) ? 0 : Convert.ToDouble(r.GetValue(1));
                        }
                    }

                    // ── Active sessions in this tablespace ────────────────
                    const string actSql = @"
                        SELECT COUNT(*)
                        FROM v$session s
                        JOIN v$process p ON p.addr = s.paddr
                        WHERE s.status = 'ACTIVE'
                          AND s.type != 'BACKGROUND'";

                    await using (var cmd = new OracleCommand(actSql, conn))
                    {
                        metric.ActiveSessions = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
                    }

                    // ── IO stats from v$filestat for this tablespace ──────
                    const string ioSql = @"
                        SELECT
                            COALESCE(SUM(fs.phyrds),      0) AS phys_reads,
                            COALESCE(SUM(fs.phywrts),     0) AS phys_writes,
                            COALESCE(SUM(fs.readtim) * 10, 0) AS read_time_ms,
                            COALESCE(SUM(fs.writetim) * 10, 0) AS write_time_ms
                        FROM v$filestat fs
                        JOIN dba_data_files df ON df.file_id = fs.file#
                        WHERE df.tablespace_name = UPPER(:ts)";

                    await using (var cmd = new OracleCommand(ioSql, conn))
                    {
                        cmd.Parameters.Add("ts", ts);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct))
                        {
                            long reads    = Convert.ToInt64(r.GetValue(0));
                            long writes   = Convert.ToInt64(r.GetValue(1));
                            long readMs   = Convert.ToInt64(r.GetValue(2));
                            long writeMs  = Convert.ToInt64(r.GetValue(3));

                            metric.IoReadBytes       = reads  * 8192L;  // Oracle default block = 8KB
                            metric.IoWriteBytes      = writes * 8192L;
                            metric.IoReadLatencyMs   = readMs;
                            metric.IoWriteLatencyMs  = writeMs;
                            metric.AvgIoReadLatencyMs  = reads  > 0 ? (double)readMs  / reads  : 0;
                            metric.AvgIoWriteLatencyMs = writes > 0 ? (double)writeMs / writes : 0;
                        }
                    }

                    results.Add(metric);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect Oracle database metrics");
            }

            return results;
        }
    }
}
