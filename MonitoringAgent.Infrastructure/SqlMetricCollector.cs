using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Sql
{
    // ═══════════════════════════════════════════════════════════════
    // BASE CLASS — shared connection helpers for all SQL services
    // ═══════════════════════════════════════════════════════════════
    public abstract class SqlServiceBase
    {
        protected readonly string _connectionString;
        protected readonly ILogger _logger;

        protected SqlServiceBase(IOptions<MonitoringConfig> options, ILogger logger, string? connectionString = null)
        {
            _connectionString = connectionString ?? options.Value.ConnectionString;
            _logger = logger;
        }

        /// <summary>Opens a connection to the master/default database.</summary>
        protected async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            return conn;
        }

        /// <summary>Opens a connection targeting a specific database.</summary>
        protected async Task<SqlConnection> OpenConnectionToDbAsync(string databaseName, CancellationToken ct = default)
        {
            var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = databaseName };
            var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            return conn;
        }

        /// <summary>Safe reader helper — converts DBNull to default.</summary>
        protected static T SafeGet<T>(SqlDataReader reader, string column, T defaultValue = default!)
        {
            var ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal)) return defaultValue;
            return (T)Convert.ChangeType(reader.GetValue(ordinal), typeof(T));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SQL METRIC COLLECTOR — Instance + Database level metrics
    // ═══════════════════════════════════════════════════════════════
    public class SqlMetricCollector : SqlServiceBase, IMetricCollector
    {
        protected readonly MonitoringConfig _config;

        public SqlMetricCollector(IOptions<MonitoringConfig> options, ILogger<SqlMetricCollector> logger, string? connectionString = null)
            : base(options, logger, connectionString) 
        { 
            _config = options.Value;
        }

        /// <summary>
        /// Collects instance-level metrics:
        /// - CPU usage via sys.dm_os_ring_buffers (ring buffer for scheduler monitor)
        /// - Memory from sys.dm_os_process_memory
        /// - Page Life Expectancy from sys.dm_os_performance_counters (Buffer Manager)
        /// - Batch Requests/sec from performance counters
        /// - Total connections from sys.dm_exec_connections
        /// </summary>
        public virtual async Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default)
        {
            var metric = new InstanceMetricDto { CollectedAt = DateTime.UtcNow };

            // ── CPU % via ring buffers ──
            // We compute Total OS CPU = 100 - SystemIdle. 
            // SqlCpu is just what SQL uses, but user wants to see PC performance.
            const string cpuSql = @"
                SELECT TOP 1
                    record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int') AS SystemIdle,
                    record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') AS SqlCpu
                FROM (
                    SELECT CAST(record AS XML) AS record
                    FROM sys.dm_os_ring_buffers
                    WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                      AND record LIKE '%<SystemHealth>%'
                ) AS x
                ORDER BY record.value('(./Record/@id)[1]', 'int') DESC;
            ";

            // ── Memory + PLE + Batch Requests ──
            const string perfSql = @"
                SELECT @@SERVERNAME AS ServerName;

                -- Total OS RAM Info (New & Better)
                SELECT 
                    total_physical_memory_kb / 1024.0 AS TotalSystemMemoryMb,
                    available_physical_memory_kb / 1024.0 AS AvailableSystemMemoryMb
                FROM sys.dm_os_sys_memory;

                -- SQL Internal Memory
                SELECT 
                    (SELECT ISNULL(SUM(cntr_value), 0) / 1024.0 FROM sys.dm_os_performance_counters WHERE counter_name = 'Total Server Memory (KB)') AS SqlMemoryUsageMb;

                SELECT ISNULL(SUM(CAST(cntr_value AS BIGINT)), 0) AS cntr_value 
                FROM sys.dm_os_performance_counters 
                WHERE counter_name LIKE '%Page life expectancy%' 
                  AND object_name LIKE '%Buffer%';

                SELECT cntr_value 
                FROM sys.dm_os_performance_counters 
                WHERE counter_name LIKE '%Batch Requests/sec%';

                SELECT COUNT(*) AS TotalConnections FROM sys.dm_exec_connections;

                SELECT [value] AS MaxDop FROM sys.configurations WHERE name = 'max degree of parallelism';
                SELECT [value] AS CostThreshold FROM sys.configurations WHERE name = 'cost threshold for parallelism';
                
                SELECT COUNT(*) AS SysAdminCount FROM sys.server_principals p
                JOIN sys.server_role_members rm ON p.principal_id = rm.member_principal_id
                WHERE rm.role_principal_id = (SELECT principal_id FROM sys.server_principals WHERE name = 'sysadmin');
            ";

            try
            {
                await using var conn = await OpenConnectionAsync(ct);

                // CPU
                try
                {
                    await using var cpuCmd = new SqlCommand(cpuSql, conn);
                    await using var cpuReader = await cpuCmd.ExecuteReaderAsync(ct);
                    if (await cpuReader.ReadAsync(ct))
                    {
                        int idle = SafeGet<int>(cpuReader, "SystemIdle");
                        int sqlCpu = SafeGet<int>(cpuReader, "SqlCpu");
                        metric.CpuUsagePercent = 100 - idle; // Total PC
                        metric.SqlCpuUsagePercent = sqlCpu;  // SQL Only
                    }
                }
                catch { }

                await using var perfCmd = new SqlCommand(perfSql, conn);
                await using var reader = await perfCmd.ExecuteReaderAsync(ct);

                // Result set 1: ServerName
                if (await reader.ReadAsync(ct))
                    metric.ServerName = SafeGet<string>(reader, "ServerName", "Unknown");

                // Result set 2: System Memory
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                {
                    double total = SafeGet<double>(reader, "TotalSystemMemoryMb");
                    double avail = SafeGet<double>(reader, "AvailableSystemMemoryMb");
                    metric.MemoryTargetMb = total; 
                    metric.MemoryUsageMb = total - avail; // Used PC RAM
                }

                // Result set 3: SQL Memory
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                {
                    metric.SqlMemoryUsageMb = SafeGet<double>(reader, "SqlMemoryUsageMb");
                }

                // Result set 4: PLE
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.PageLifeExpectancy = SafeGet<long>(reader, "cntr_value");

                // Result set 5: Batch Requests/sec
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.BatchRequestsPerSec = SafeGet<long>(reader, "cntr_value");

                // Result set 6: Total connections
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.TotalConnections = SafeGet<long>(reader, "TotalConnections");

                // Result set 6: MAXDOP
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.MaxDop = SafeGet<int>(reader, "MaxDop");

                // Result set 7: Cost Threshold
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.CostThresholdForParallelism = SafeGet<int>(reader, "CostThreshold");

                // Result set 8: SysAdmin Count
                if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                    metric.SysAdminCount = SafeGet<int>(reader, "SysAdminCount");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect core instance metrics");
            }

            // ── Premium Advanced Metrics ──
            if (_config.FeatureToggles.EnableBufferCacheMonitoring)
            {
                try
                {
                    const string bchrSql = @"
                        SELECT TOP 1
                           (CAST(A.cntr_value AS FLOAT) / NULLIF(CAST(B.cntr_value AS FLOAT), 0)) * 100.0 AS BufferCacheHitRatio
                        FROM sys.dm_os_performance_counters A
                        JOIN sys.dm_os_performance_counters B ON A.object_name = B.object_name
                        WHERE A.counter_name = 'Buffer cache hit ratio' 
                          AND B.counter_name = 'Buffer cache hit ratio base';";
                    await using var conn = await OpenConnectionAsync(ct);
                    await using var cmd = new SqlCommand(bchrSql, conn);
                    metric.BufferCacheHitRatio = (double)(await cmd.ExecuteScalarAsync(ct) ?? 0.0);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to collect Buffer Cache Hit Ratio."); }
            }

            if (_config.FeatureToggles.EnableTempDbMonitoring)
            {
                try
                {
                    const string tempSql = @"
                        SELECT 
                            ISNULL(SUM(CASE WHEN type = 0 THEN size * 8.0 / 1024 END), 0) AS TempDbSizeMb,
                            ISNULL(SUM(CASE WHEN type = 1 THEN size * 8.0 / 1024 END), 0) AS TempDbLogSizeMb
                        FROM tempdb.sys.database_files;";
                    await using var conn = await OpenConnectionAsync(ct);
                    await using var cmd = new SqlCommand(tempSql, conn);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        metric.TempDbSizeMb = SafeGet<double>(reader, "TempDbSizeMb");
                        metric.TempDbLogSizeMb = SafeGet<double>(reader, "TempDbLogSizeMb");
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to collect TempDB metrics."); }
            }

            if (_config.FeatureToggles.EnableWaitStats)
            {
                try
                {
                    const string waitSql = @"
                        SELECT TOP 10 
                            wait_type, wait_time_ms, waiting_tasks_count, max_wait_time_ms, signal_wait_time_ms
                        FROM sys.dm_os_wait_stats
                        WHERE wait_type NOT IN ('DIRTY_PAGE_POLL', 'HADR_FILESTREAM_IOMGR_IOCOMPLETION', 'LAZYWRITER_SLEEP', 'LOGMGR_QUEUE', 'REQUEST_FOR_DEADLOCK_SEARCH', 'SLEEP_TASK', 'SLEEP_SYSTEMTASK', 'SQLTRACE_BUFFER_FLUSH', 'WAITFOR', 'BROKER_TASK_STOP', 'CLR_MANUAL_EVENT', 'CLR_AUTO_EVENT', 'DISPATCHER_QUEUE_SEMAPHORE', 'FT_IFTS_SCHEDULER_IDLE_WAIT', 'XE_DISPATCHER_WAIT', 'XE_TIMER_EVENT', 'BROKER_RECEIVE_WAITFOR', 'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP', 'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP', 'SP_SERVER_DIAGNOSTICS_SLEEP')
                          AND wait_time_ms > 0
                        ORDER BY wait_time_ms DESC;";
                    await using var conn = await OpenConnectionAsync(ct);
                    await using var cmd = new SqlCommand(waitSql, conn);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        metric.TopWaitStats.Add(new WaitStatDto
                        {
                            WaitType = SafeGet<string>(reader, "wait_type", ""),
                            WaitTimeMs = SafeGet<long>(reader, "wait_time_ms"),
                            WaitCount = SafeGet<long>(reader, "waiting_tasks_count"),
                            MaxWaitTimeMs = SafeGet<long>(reader, "max_wait_time_ms"),
                            SignalWaitTimeMs = SafeGet<long>(reader, "signal_wait_time_ms"),
                        });
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to collect Wait Stats."); }
            }

            if (_config.FeatureToggles.EnableFailedLoginTracking)
            {
                try
                {
                    // Read SQL Server error log for last 1 minute failed logins. Works across all editions/versions.
                    const string loginSql = @"
                        DECLARE @startDate DATETIME = DATEADD(minute, -1, GETDATE());
                        CREATE TABLE #ErrorLog (LogDate DATETIME, ProcessInfo NVARCHAR(50), Text NVARCHAR(4000));
                        INSERT INTO #ErrorLog EXEC xp_readerrorlog 0, 1, N'Login failed', NULL, @startDate, NULL, N'DESC';
                        SELECT COUNT(*) FROM #ErrorLog;
                        DROP TABLE #ErrorLog;";
                    await using var conn = await OpenConnectionAsync(ct);
                    await using var cmd = new SqlCommand(loginSql, conn);
                    metric.FailedLoginsLastMinute = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to collect Failed Logins."); }
            }

            // ── NEW: Plan Cache Bloat Analysis ──
            try
            {
                const string cacheBloatSql = @"
                    SELECT 
                        SUM(CASE WHEN usecounts = 1 THEN size_in_bytes ELSE 0 END) / 1024.0 / 1024.0 AS SingleUsePlanMb,
                        SUM(size_in_bytes) / 1024.0 / 1024.0 AS TotalPlanCacheMb
                    FROM sys.dm_exec_cached_plans
                    WHERE objtype = 'Adhoc';";
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(cacheBloatSql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    double singleUse = SafeGet<double>(reader, "SingleUsePlanMb");
                    double total = SafeGet<double>(reader, "TotalPlanCacheMb");
                    if (total > 100 && (singleUse / total) > 0.5)
                    {
                        metric.TopWaitStats.Add(new WaitStatDto { WaitType = "AD_HOC_PLAN_BLOAT", WaitTimeMs = (long)singleUse });
                    }
                }
            }
            catch { }

            return metric;
        }

        /// <summary>
        /// Collects per-database metrics:
        /// - Active sessions from sys.dm_exec_sessions filtered by database_id
        /// - IO stats from sys.dm_io_virtual_file_stats (aggregated across all files)
        /// - Database + log size from sys.master_files
        /// </summary>
        public virtual async Task<List<DatabaseMetricDto>> CollectDatabaseMetricsAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            var results = new List<DatabaseMetricDto>();

            foreach (var db in databases)
            {
                var metric = new DatabaseMetricDto { DatabaseName = db, CollectedAt = DateTime.UtcNow };

                // ── IO stats from sys.dm_io_virtual_file_stats ──
                // This DMV provides cumulative IO statistics per database file since last SQL restart.
                // We aggregate bytes read/written and latency across all files for the database.
                const string sql = @"
                    -- Active sessions: count running sessions for this database
                    SELECT COUNT(*) AS ActiveSessions
                    FROM sys.dm_exec_sessions
                    WHERE database_id = DB_ID(@dbName) AND status = 'running';

                    -- IO: aggregate read/write bytes and latency across all files
                    SELECT 
                        ISNULL(SUM(num_of_bytes_read), 0)    AS IoReadBytes,
                        ISNULL(SUM(num_of_bytes_written), 0) AS IoWriteBytes,
                        ISNULL(SUM(io_stall_read_ms), 0)     AS IoReadLatencyMs,
                        ISNULL(SUM(io_stall_write_ms), 0)    AS IoWriteLatencyMs,
                        CASE WHEN SUM(num_of_reads) > 0 THEN CAST(SUM(io_stall_read_ms) AS FLOAT) / SUM(num_of_reads) ELSE 0 END AS AvgIoReadLatencyMs,
                        CASE WHEN SUM(num_of_writes) > 0 THEN CAST(SUM(io_stall_write_ms) AS FLOAT) / SUM(num_of_writes) ELSE 0 END AS AvgIoWriteLatencyMs
                    FROM sys.dm_io_virtual_file_stats(DB_ID(@dbName), NULL);

                    -- Size: data files (type 0) and log files (type 1) from sys.master_files
                    SELECT 
                        ISNULL(SUM(CASE WHEN type = 0 THEN size * 8.0 / 1024 END), 0) AS DatabaseSizeMb,
                        ISNULL(SUM(CASE WHEN type = 1 THEN size * 8.0 / 1024 END), 0) AS LogSizeMb,
                        MAX(CAST(is_percent_growth AS INT)) AS HasPercentGrowth
                    FROM sys.master_files
                    WHERE database_id = DB_ID(@dbName);

                    -- DB State and Config
                    SELECT is_auto_shrink_on, is_auto_close_on, is_trustworthy_on, state_desc
                    FROM sys.databases
                    WHERE name = @dbName;

                    -- Backup Status
                    SELECT ISNULL(DATEDIFF(day, MAX(backup_finish_date), GETDATE()), 9999) AS DaysSinceLastBackup
                    FROM msdb.dbo.backupset
                    WHERE database_name = @dbName AND type = 'D';
                ";

                try
                {
                    await using var conn = await OpenConnectionAsync(ct);
                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@dbName", db);
                    cmd.CommandTimeout = 15;

                    await using (var reader = await cmd.ExecuteReaderAsync(ct))
                    {
                        if (await reader.ReadAsync(ct))
                            metric.ActiveSessions = SafeGet<int>(reader, "ActiveSessions");

                        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                        {
                            metric.IoReadBytes = SafeGet<long>(reader, "IoReadBytes");
                            metric.IoWriteBytes = SafeGet<long>(reader, "IoWriteBytes");
                            metric.IoReadLatencyMs = SafeGet<long>(reader, "IoReadLatencyMs");
                            metric.IoWriteLatencyMs = SafeGet<long>(reader, "IoWriteLatencyMs");
                            metric.AvgIoReadLatencyMs = SafeGet<double>(reader, "AvgIoReadLatencyMs");
                            metric.AvgIoWriteLatencyMs = SafeGet<double>(reader, "AvgIoWriteLatencyMs");
                        }

                        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                        {
                            metric.DatabaseSizeMb = SafeGet<double>(reader, "DatabaseSizeMb");
                            metric.LogSizeMb = SafeGet<double>(reader, "LogSizeMb");
                            metric.HasPercentGrowth = SafeGet<int>(reader, "HasPercentGrowth") == 1;
                        }

                        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                        {
                            metric.IsAutoShrinkOn = SafeGet<bool>(reader, "is_auto_shrink_on");
                            metric.IsAutoCloseOn = SafeGet<bool>(reader, "is_auto_close_on");
                            metric.IsTrustworthyOn = SafeGet<bool>(reader, "is_trustworthy_on");
                            metric.StateDesc = SafeGet<string>(reader, "state_desc", "UNKNOWN");
                        }

                        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
                        {
                            var days = SafeGet<int>(reader, "DaysSinceLastBackup");
                            metric.DaysSinceLastBackup = days == 9999 ? -1 : days;
                        }
                    }

                    results.Add(metric);

                    // ── Premium Advanced Database Metrics ──
                    if (_config.FeatureToggles.EnableVlfMonitoring)
                    {
                        try
                        {
                            // DBCC LOGINFO works on all SQL Server versions up to very latest
                            string vlfSql = $"DBCC LOGINFO(N'{db.Replace("'", "''")}');";
                            await using var vlfCmd = new SqlCommand(vlfSql, conn);
                            await using var vlfReader = await vlfCmd.ExecuteReaderAsync(ct);
                            int vlfCount = 0;
                            while (await vlfReader.ReadAsync(ct)) vlfCount++;
                            metric.VlfCount = vlfCount;
                        }
                        catch (Exception ex) { _logger.LogInformation(ex, "VLF check skipped for {Database}", db); }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to collect metrics for database {Database}", db);
                }
            }

            return results;
        }
    }
}
