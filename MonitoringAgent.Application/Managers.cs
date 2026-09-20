using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Application.Services
{
    // ═══════════════════════════════════════════════════════════════
    // METRIC SERVICE — orchestrates instance + database metric collection
    // ═══════════════════════════════════════════════════════════════
    public class MetricService
    {
        private readonly IMetricCollector _collector;
        private readonly ILogger<MetricService> _logger;

        public MetricService(IMetricCollector collector, ILogger<MetricService> logger)
        {
            _collector = collector;
            _logger = logger;
        }

        public async Task<InstanceMetricDto?> CollectInstanceAsync(CancellationToken ct)
        {
            try
            {
                var metrics = await _collector.CollectInstanceMetricsAsync(ct);
                _logger.LogInformation(
                    "Instance metrics — CPU: {Cpu}%, Memory: {Mem}MB, PLE: {Ple}s, Connections: {Conn}",
                    metrics.CpuUsagePercent, metrics.MemoryUsageMb,
                    metrics.PageLifeExpectancy, metrics.TotalConnections);
                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect instance metrics");
                return null;
            }
        }

        public async Task<List<DatabaseMetricDto>> CollectDatabasesAsync(
            IEnumerable<string> databases, CancellationToken ct)
        {
            try
            {
                var metrics = await _collector.CollectDatabaseMetricsAsync(databases, ct);
                _logger.LogInformation("Collected metrics for {Count} databases", metrics.Count);
                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to collect database metrics");
                return new List<DatabaseMetricDto>();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // QUERY ANALYSIS MANAGER — runs all 3 query analysis types
    // ═══════════════════════════════════════════════════════════════
    public class QueryAnalysisManager
    {
        private readonly IQueryAnalyzer _analyzer;
        private readonly ILogger<QueryAnalysisManager> _logger;

        public QueryAnalysisManager(IQueryAnalyzer analyzer, ILogger<QueryAnalysisManager> logger)
        {
            _analyzer = analyzer;
            _logger = logger;
        }

        public async Task<List<QueryMetricDto>> AnalyzeAllAsync(
            IEnumerable<string> databases, CancellationToken ct)
        {
            var all = new List<QueryMetricDto>();

            try
            {
                var longRunning = await _analyzer.GetLongRunningQueriesAsync(databases, ct);
                _logger.LogInformation("Found {Count} long-running queries", longRunning.Count);
                all.AddRange(longRunning);

                var cpuHeavy = await _analyzer.GetCpuIntensiveQueriesAsync(databases, ct);
                _logger.LogInformation("Found {Count} CPU-intensive queries", cpuHeavy.Count);
                all.AddRange(cpuHeavy);

                var ioHeavy = await _analyzer.GetIoIntensiveQueriesAsync(databases, ct);
                _logger.LogInformation("Found {Count} IO-intensive queries", ioHeavy.Count);
                all.AddRange(ioHeavy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during query analysis");
            }

            // Deduplicate by QueryHash (same query may appear in multiple categories)
            return all
                .GroupBy(q => q.QueryHash)
                .Select(g => g.First())
                .ToList();
        }

        public async Task<List<QueryMetricDto>> GetPlanIssuesAsync(CancellationToken ct = default)
        {
            if (_analyzer is MonitoringAgent.Infrastructure.Sql.QueryAnalyzerService sqlAnalyzer)
            {
                try
                {
                    return await sqlAnalyzer.GetPlanIssuesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting plan issues.");
                }
            }
            return new List<QueryMetricDto>();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // INDEX ANALYSIS MANAGER — runs all 5 index analysis types per DB
    // ═══════════════════════════════════════════════════════════════
    public class IndexAnalysisManager
    {
        private readonly IIndexAnalyzer _analyzer;
        private readonly ILogger<IndexAnalysisManager> _logger;

        public IndexAnalysisManager(IIndexAnalyzer analyzer, ILogger<IndexAnalysisManager> logger)
        {
            _analyzer = analyzer;
            _logger = logger;
        }

        public async Task<List<IndexAnalysisDto>> AnalyzeAllAsync(
            IEnumerable<string> databases, CancellationToken ct)
        {
            var all = new List<IndexAnalysisDto>();

            foreach (var db in databases)
            {
                try
                {
                    _logger.LogInformation("Starting index analysis for [{Database}]...", db);

                    var fragmented = await _analyzer.GetFragmentedIndexesAsync(db, ct);
                    _logger.LogInformation("  [{Db}] Fragmented indexes: {Count}", db, fragmented.Count);
                    all.AddRange(fragmented);

                    var missing = await _analyzer.GetMissingIndexesAsync(db, ct);
                    _logger.LogInformation("  [{Db}] Missing indexes: {Count}", db, missing.Count);
                    all.AddRange(missing);

                    var unused = await _analyzer.GetUnusedIndexesAsync(db, ct);
                    _logger.LogInformation("  [{Db}] Unused indexes: {Count}", db, unused.Count);
                    all.AddRange(unused);

                    var duplicates = await _analyzer.GetDuplicateIndexesAsync(db, ct);
                    _logger.LogInformation("  [{Db}] Duplicate indexes: {Count}", db, duplicates.Count);
                    all.AddRange(duplicates);

                    var heaps = await _analyzer.GetHeapTablesAsync(db, ct);
                    _logger.LogInformation("  [{Db}] Heap tables: {Count}", db, heaps.Count);
                    all.AddRange(heaps);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error analyzing indexes for [{Database}]", db);
                }
            }

            return all;
        }

        public async Task<List<IndexAnalysisDto>> GetStaleStatisticsAsync(string database, CancellationToken ct = default)
        {
            // Only supported by the SQL Server implementation currently
            if (_analyzer is MonitoringAgent.Infrastructure.Sql.IndexAnalysisService sqlAnalyzer)
            {
                try
                {
                    return await sqlAnalyzer.GetStaleStatisticsAsync(database, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting stale statistics for [{Database}]", database);
                }
            }
            return new List<IndexAnalysisDto>();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // EVENT MONITOR MANAGER — blocking & deadlock detection
    // ═══════════════════════════════════════════════════════════════
    public class EventMonitorManager
    {
        private readonly IEventListener _listener;
        private readonly ILogger<EventMonitorManager> _logger;

        public EventMonitorManager(IEventListener listener, ILogger<EventMonitorManager> logger)
        {
            _listener = listener;
            _logger = logger;
        }

        public async Task<(List<BlockingEventDto> Blocking, List<DeadlockEventDto> Deadlocks)>
            DetectEventsAsync(CancellationToken ct)
        {
            var blocking = new List<BlockingEventDto>();
            var deadlocks = new List<DeadlockEventDto>();

            try
            {
                blocking = await _listener.GetBlockingSessionsAsync(ct);
                if (blocking.Count > 0)
                    _logger.LogWarning("⚠ Detected {Count} blocking sessions!", blocking.Count);

                deadlocks = await _listener.GetDeadlockEventsAsync(ct);
                if (deadlocks.Count > 0)
                    _logger.LogWarning("⚠ Detected {Count} deadlock events!", deadlocks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during event detection");
            }

            return (blocking, deadlocks);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ALERT MANAGER — evaluates thresholds and generates alerts
    //   Severity levels: Low (informational), Medium (action needed), High (critical)
    // ═══════════════════════════════════════════════════════════════
    public class AlertManager
    {
        private readonly AlertThresholds _thresholds;
        private readonly ILogger<AlertManager> _logger;

        public AlertManager(IOptions<MonitoringConfig> options, ILogger<AlertManager> logger)
        {
            _thresholds = options.Value.AlertThresholds;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates all collected data against configured thresholds and generates alerts.
        /// </summary>
        public List<AlertDto> EvaluateAlerts(
            InstanceMetricDto? instance,
            List<DatabaseMetricDto> databases,
            List<BlockingEventDto> blocking,
            List<DeadlockEventDto> deadlocks,
            List<IndexAnalysisDto> indexes)
        {
            var alerts = new List<AlertDto>();

            if (instance != null)
            {
                // ── CPU Alerts ──
                string cpuRec = @"[HIGH CPU USAGE]
1. Find top CPU-consuming active queries:
SELECT TOP 10 session_id, status, command, cpu_time, text 
FROM sys.dm_exec_requests CROSS APPLY sys.dm_exec_sql_text(sql_handle) ORDER BY cpu_time DESC;
2. Check wait stats for CXPACKET (parallelism issues) or SOS_SCHEDULER_YIELD.
3. Ensure missing indexes are created to reduce logical reads (which cause high CPU).";

                if (instance.CpuUsagePercent >= _thresholds.CpuHighPercent)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "High", "CPU",
                        $"CPU usage critically high at {instance.CpuUsagePercent:F1}%",
                        instance.CpuUsagePercent, _thresholds.CpuHighPercent, cpuRec));
                }
                else if (instance.CpuUsagePercent >= _thresholds.CpuMediumPercent)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "Medium", "CPU",
                        $"CPU usage elevated at {instance.CpuUsagePercent:F1}%",
                        instance.CpuUsagePercent, _thresholds.CpuMediumPercent, cpuRec));
                }

                // ── Page Life Expectancy Alerts ──
                string pleRec = @"[MEMORY PRESSURE / LOW PLE] RAM data pages are flushing too fast.
1. Check SQL Max Memory:
EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'max server memory (MB)';
(Increase if too low. e.g., EXEC sp_configure 'max server memory (MB)', 4096; RECONFIGURE;)

2. Find queries causing Large Scans (RAM Dumpers):
SELECT TOP 10 qs.total_logical_reads AS LogicalReads, SUBSTRING(st.text,1,200) AS QueryText
FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
ORDER BY LogicalReads DESC;

3. Add covering indexes and avoid SELECT * to stop full table scans.";

                if (instance.PageLifeExpectancy <= _thresholds.PleCriticalThreshold)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "High", "PLE",
                        $"Page Life Expectancy critically low: {instance.PageLifeExpectancy}s (threshold: {_thresholds.PleCriticalThreshold}s)",
                        instance.PageLifeExpectancy, _thresholds.PleCriticalThreshold, pleRec));
                }
                else if (instance.PageLifeExpectancy <= _thresholds.PleLowThreshold)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "Medium", "PLE",
                        $"Page Life Expectancy low: {instance.PageLifeExpectancy}s (threshold: {_thresholds.PleLowThreshold}s)",
                        instance.PageLifeExpectancy, _thresholds.PleLowThreshold, pleRec));
                }

                // ── Memory Alert ──
                if (instance.MemoryTargetMb > 0)
                {
                    var memPct = (instance.MemoryUsageMb / instance.MemoryTargetMb) * 100;
                    if (memPct >= _thresholds.MemoryHighPercent)
                    {
                        alerts.Add(CreateAlert(instance.ServerName, "", "High", "Memory",
                            $"Memory usage at {memPct:F1}% ({instance.MemoryUsageMb:F0}MB / {instance.MemoryTargetMb:F0}MB)",
                            memPct, _thresholds.MemoryHighPercent,
                            "SQL Server is reaching its max memory limit. Check 'max server memory' configuration and monitor active connections."));
                    }
                }

                // ── Buffer Cache Alert ──
                if (instance.BufferCacheHitRatio > 0 && instance.BufferCacheHitRatio < _thresholds.BufferCacheHitRatioLowThreshold)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "Medium", "BufferCache",
                        $"Buffer Cache Hit Ratio is suspiciously low: {instance.BufferCacheHitRatio:F1}% (threshold: {_thresholds.BufferCacheHitRatioLowThreshold}%)",
                        instance.BufferCacheHitRatio, _thresholds.BufferCacheHitRatioLowThreshold,
                        "SQL is frequently reading from disk rather than RAM. This often means missing indexes are forcing physical scans. Please review query plans."));
                }

                // ── TempDB Alert ──
                if (instance.TempDbSizeMb > _thresholds.TempDbHighUsageMb)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "tempdb", "High", "Storage",
                        $"TempDB size excessively high: {instance.TempDbSizeMb:F0}MB (threshold: {_thresholds.TempDbHighUsageMb}MB)",
                        instance.TempDbSizeMb, _thresholds.TempDbHighUsageMb,
                        "TempDB is bloated. This can be caused by uncommitted transactions, heavy #temp table usage, or version store overhead. Identify the query holding the version store."));
                }

                // ── Security Alerts ──
                if (instance.FailedLoginsLastMinute >= _thresholds.FailedLoginHighCount)
                {
                    alerts.Add(CreateAlert(instance.ServerName, "", "High", "Security",
                        $"Unusual amount of failed logins detected: {instance.FailedLoginsLastMinute} failures in the last minute.",
                        instance.FailedLoginsLastMinute, _thresholds.FailedLoginHighCount,
                        "Possible brute-force attack or a misconfigured application service account repeatedly failing. Check SQL Server Error Logs immediately to identify the source IP."));
                }

                if (instance.SysAdminCount > 5)
                {
                    string saRec = @"[SECURITY AUDIT] Too many accounts have SysAdmin privileges.
1. Review the list of sysadmins:
SELECT p.name AS LoginName, p.type_desc FROM sys.server_principals p
JOIN sys.server_role_members rm ON p.principal_id = rm.member_principal_id
WHERE rm.role_principal_id = (SELECT principal_id FROM sys.server_principals WHERE name = 'sysadmin');
2. Remove any unnecessary or personal accounts from the sysadmin role.";
                    alerts.Add(CreateAlert(instance.ServerName, "", "Medium", "Security",
                        $"High number of SysAdmins detected ({instance.SysAdminCount}). Possible security risk.",
                        instance.SysAdminCount, 5, saRec));
                }

                // ── Instance Configuration Alerts ──
                if (instance.MaxDop == 0)
                {
                    string dopRec = @"[CONFIG] MAXDOP is set to 0 (Default). This can cause uneven CPU usage.
Recommended: Set MAXDOP to 8 or equal to the number of cores in a single NUMA node.
EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'max degree of parallelism', 8; RECONFIGURE;";
                    alerts.Add(CreateAlert(instance.ServerName, "", "Medium", "Configuration",
                        "MAXDOP is set to 0. All cores can be used for a single query, potentially causing bottlenecks.",
                        0, 8, dopRec));
                }

                if (instance.CostThresholdForParallelism < 50)
                {
                    string costRec = @"[CONFIG] Cost Threshold for Parallelism is low (" + instance.CostThresholdForParallelism + @").
Default (5) is often too low for modern servers, causing small queries to use multiple CPU threads unnecessarily.
Recommended: Set it to 50.
EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'cost threshold for parallelism', 50; RECONFIGURE;";
                    alerts.Add(CreateAlert(instance.ServerName, "", "Low", "Configuration",
                        $"Cost Threshold for Parallelism is low ({instance.CostThresholdForParallelism})",
                        instance.CostThresholdForParallelism, 50, costRec));
                }
            }

            // ── VLF Alerts & Security/Config Alerts ──
            foreach (var dbMetric in databases)
            {
                if (dbMetric.VlfCount > _thresholds.VlfHighCount)
                {
                    string vlfRec = @"[HIGH VLF COUNT] Too many Virtual Log Files degrade write speed and DB startup time.
1. Backup the Transaction Log (if in Full Recovery).
2. Shrink the Log File to clear inactive VLFs: DBCC SHRINKFILE (1, 0);
3. Pre-grow the Log File manually in larger chunks (e.g., 512MB/1GB) to prevent micro-VLF creation.";

                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "Medium", "Storage",
                        $"High number of Virtual Log Files (VLFs) detected: {dbMetric.VlfCount} (threshold: {_thresholds.VlfHighCount})",
                        dbMetric.VlfCount, _thresholds.VlfHighCount, vlfRec));
                }

                // ── Disk Latency Alerts ──
                if (dbMetric.AvgIoReadLatencyMs > _thresholds.DiskReadLatencyHighMs)
                {
                    string ioReadRec = @"[HIGH READ LATENCY] Slow disk reads are stalling queries.
1. Check if the disk subsystem is saturated (SAN, Storage Spaces).
2. Find expensive IO queries reading heavily:
SELECT TOP 10 session_id, wait_type, wait_time, wait_resource, text 
FROM sys.dm_exec_requests CROSS APPLY sys.dm_exec_sql_text(sql_handle) WHERE wait_type LIKE 'PAGEIOLATCH%';
3. Add missing indexes (seek instead of scan) to drastically reduce disk reads.";
                    
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "High", "Performance",
                        $"Average IO Read Latency is high: {dbMetric.AvgIoReadLatencyMs:F1}ms (threshold: {_thresholds.DiskReadLatencyHighMs}ms)",
                        dbMetric.AvgIoReadLatencyMs, _thresholds.DiskReadLatencyHighMs, ioReadRec));
                }

                if (dbMetric.AvgIoWriteLatencyMs > _thresholds.DiskWriteLatencyHighMs)
                {
                    string ioWriteRec = @"[HIGH WRITE LATENCY] Slow disk writes delay COMMITs and block users.
1. Ensure High-Performance power plan is enabled in Windows OS.
2. Check wait_type 'WRITELOG' which means Transaction Log disk is slow.
3. Consider moving Data and Log files to separate physical or faster NVMe/SSD disks.";
                    
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "High", "Performance",
                        $"Average IO Write Latency is high: {dbMetric.AvgIoWriteLatencyMs:F1}ms (threshold: {_thresholds.DiskWriteLatencyHighMs}ms)",
                        dbMetric.AvgIoWriteLatencyMs, _thresholds.DiskWriteLatencyHighMs, ioWriteRec));
                }

                // ── Auto-Shrink / Auto-Close Alerts (Configuration/Performance) ──
                if (dbMetric.IsAutoShrinkOn)
                {
                    string shrinkRec = @"[BAD PRACTICE] Auto-Shrink heavily fragments indexes and burns CPU/IO.
Disable it immediately:
ALTER DATABASE [" + dbMetric.DatabaseName + @"] SET AUTO_SHRINK OFF;";
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "Medium", "Configuration",
                        $"AUTO-SHRINK is enabled on database {dbMetric.DatabaseName}", 1, 0, shrinkRec));
                }

                if (dbMetric.IsAutoCloseOn)
                {
                    string closeRec = @"[BAD PRACTICE] Auto-Close drops plans and closes connections, harming performance.
Disable it immediately:
ALTER DATABASE [" + dbMetric.DatabaseName + @"] SET AUTO_CLOSE OFF;";
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "Medium", "Configuration",
                        $"AUTO-CLOSE is enabled on database {dbMetric.DatabaseName}", 1, 0, closeRec));
                }

                if (dbMetric.IsTrustworthyOn)
                {
                    string trustRec = @"[SECURITY RISK] TRUSTWORTHY is ON, allowing database objects to access resources outside the DB.
Disable it if not explicitly required by features like CLR or Service Broker:
ALTER DATABASE [" + dbMetric.DatabaseName + @"] SET TRUSTWORTHY OFF;";
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "High", "Security",
                        $"TRUSTWORTHY property is enabled for {dbMetric.DatabaseName}", 1, 0, trustRec));
                }

                if (dbMetric.HasPercentGrowth)
                {
                    string growthRec = @"[CONFIG] Percentage file growth can cause huge, slow autogrow events.
Recommended: Set file growth to a fixed value (e.g., 256MB or 512MB).
ALTER DATABASE [" + dbMetric.DatabaseName + @"] MODIFY FILE (NAME = 'DataFileName', FILEGROWTH = 512MB);";
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "Low", "Configuration",
                        $"One or more files for {dbMetric.DatabaseName} use percentage growth.", 1, 0, growthRec));
                }

                // ── Backup Alerts (Security/Disaster Recovery) ──
                if (dbMetric.DaysSinceLastBackup >= _thresholds.DaysSinceLastBackupWarning && dbMetric.DatabaseName != "tempdb")
                {
                    string backupRec = @"[CRITICAL RISK] Database hasn't had a Full Backup recently!
1. Check SQL Server Agent Jobs for failed backup jobs.
2. Start a full backup immediately:
BACKUP DATABASE [" + dbMetric.DatabaseName + @"] TO DISK = 'C:\Backups\" + dbMetric.DatabaseName + @".bak' WITH COMPRESSION, INIT;
3. Ensure daily automated backup plans exist via Maintenance Plans or Ola Hallengren scripts.";
                    
                    alerts.Add(CreateAlert("", dbMetric.DatabaseName, "High", "Security",
                        $"No full backup taken in {dbMetric.DaysSinceLastBackup} days!", 
                        dbMetric.DaysSinceLastBackup, _thresholds.DaysSinceLastBackupWarning, backupRec));
                }
            }

            // ── Blocking Alerts ──
            foreach (var b in blocking)
            {
                var severity = b.WaitTimeMs > 60000 ? "High" : "Medium";
                alerts.Add(CreateAlert("", b.DatabaseName, severity, "Blocking",
                    $"SPID {b.BlockedSpid} blocked by SPID {b.BlockingSpid} for {b.WaitTimeMs / 1000}s (wait: {b.WaitType})",
                    b.WaitTimeMs / 1000.0, _thresholds.BlockingWaitTimeSeconds,
                    "Identify why the blocking session is holding locks. It may be due to missing indexes causing scans, or uncommitted transactions. Consider RCSI isolation."));
            }

            // ── Deadlock Alerts — always High severity ──
            foreach (var d in deadlocks)
            {
                string deadlockRec = @"[DEADLOCK DETECTED] Two processes collided waiting for exclusive locks.
1. Identify the conflict: View the Deadlock Graph XML to see which SPIDs and Tables collided.
2. Prevent Table Scans: Add covering indexes so queries only lock specific rows, not the whole table.
3. Consistency: Ensure application code updates tables in the exact same sequence.
4. Consider SNAPSHOT ISOLATION or READ COMMITTED SNAPSHOT (RCSI) if readers are blocking writers.";

                alerts.Add(CreateAlert("", d.DatabaseName, "High", "Deadlock",
                    $"Deadlock detected at {d.EventTime:u}",
                    1, 0, deadlockRec));
            }

            // ── Index Alerts — High-impact missing or severely fragmented ──
            var criticalIndexes = indexes.Where(i =>
                (i.IssueType == "Missing" && i.ImpactScore > 500) ||
                (i.IssueType == "Fragmented" && i.FragmentationPercent > 80));
            foreach (var idx in criticalIndexes)
            {
                var advice = idx.IssueType == "Missing"
                    ? "Execute the generated Index CREATE Script during a maintenance window to instantly improve performance."
                    : "Schedule an INDEX REBUILD during maintenance hours to resolve severe fragmentation and restore seq I/O speed.";

                alerts.Add(CreateAlert("", idx.DatabaseName, "Medium", "Index",
                    $"[{idx.IssueType}] {idx.TableName}.{idx.IndexName} — Impact: {idx.ImpactScore:F0}, Suggestion: {idx.Suggestion}",
                    idx.ImpactScore, 500,
                    advice));
            }

            if (alerts.Count > 0)
                _logger.LogWarning("Generated {Count} alerts: {High} High, {Med} Medium, {Low} Low",
                    alerts.Count,
                    alerts.Count(a => a.Severity == "High"),
                    alerts.Count(a => a.Severity == "Medium"),
                    alerts.Count(a => a.Severity == "Low"));

            return alerts;
        }

        private static AlertDto CreateAlert(string server, string db, string severity,
            string category, string message, double current, double threshold, string recommendation)
        {
            return new AlertDto
            {
                ServerName     = server,
                DatabaseName   = db,
                Severity       = severity,
                Category       = category,
                Message        = message,
                Recommendation = recommendation,
                CurrentValue   = current,
                ThresholdValue = threshold,
                RaisedAt       = DateTime.UtcNow
            };
        }
    }
}
