using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Application.Services
{
    // ═══════════════════════════════════════════════════════════════
    // THRESHOLD EVALUATION SERVICE — compares metrics against config
    // ═══════════════════════════════════════════════════════════════
    public class ThresholdEvaluationService
    {
        private readonly ThresholdsConfig _thresholds;
        private readonly RecommendationService _recommendationService;
        private readonly ILogger<ThresholdEvaluationService> _logger;
        
        // Advanced Features: History & Suppression
        private readonly Dictionary<string, AlertDto> _alertHistory = new();
        private readonly Dictionary<string, DateTime> _lastAlertTime = new(); // For cooldown
        private readonly TimeSpan _alertCooldown = TimeSpan.FromHours(1);

        public ThresholdEvaluationService(
            IOptions<MonitoringConfig> options,
            RecommendationService recommendationService,
            ILogger<ThresholdEvaluationService> logger)
        {
            _thresholds = options.Value.Thresholds;
            _recommendationService = recommendationService;
            _logger = logger;
        }

        public List<AlertDto> EvaluateAll(
            InstanceMetricDto? instance,
            List<DatabaseMetricDto> databases,
            List<QueryMetricDto> queries,
            List<IndexAnalysisDto> indexes,
            List<BlockingEventDto> blocking,
            List<DeadlockEventDto> deadlocks)
        {
            var rawAlerts = new List<AlertDto>();

            // Collect all potential alerts
            if (instance != null)
            {
                if (instance.CpuUsagePercent > _thresholds.CpuHigh)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("High CPU Usage", instance.CpuUsagePercent, _thresholds.CpuHigh, "CPU", instance.ServerName));
                
                if (instance.PageLifeExpectancy < _thresholds.MemoryPressurePLE)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Low PLE", instance.PageLifeExpectancy, _thresholds.MemoryPressurePLE, "PLE", instance.ServerName));

                if (instance.SqlCpuUsagePercent > _thresholds.DTUHigh)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("High DTU/CPU", instance.SqlCpuUsagePercent, _thresholds.DTUHigh, "DTU", instance.ServerName));

                // Config checks
                if (instance.MaxDop == 0)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Configuration: MaxDOP", 0, 8, "Configuration", instance.ServerName));

                if (instance.CostThresholdForParallelism < 50)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Configuration: Cost Threshold", instance.CostThresholdForParallelism, 50, "Configuration", instance.ServerName));

                // Security checks
                if (instance.SysAdminCount > _thresholds.MaxSysAdminCount)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("High SysAdmin Count", instance.SysAdminCount, _thresholds.MaxSysAdminCount, "Security", instance.ServerName));

                if (instance.FailedLoginsLastMinute > _thresholds.MaxFailedLoginsPerMin)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Failed Logins High", instance.FailedLoginsLastMinute, _thresholds.MaxFailedLoginsPerMin, "Security", instance.ServerName));

                // IO/Cache checks
                if (instance.BufferCacheHitRatio < _thresholds.MinBufferCacheHitRatio)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Low Buffer Cache Hit", instance.BufferCacheHitRatio, _thresholds.MinBufferCacheHitRatio, "IO", instance.ServerName));

                if (instance.TempDbSizeMb > _thresholds.MaxTempDbSizeMb)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("TempDB Bloat", instance.TempDbSizeMb, _thresholds.MaxTempDbSizeMb, "Storage", instance.ServerName));
            }

            foreach (var db in databases)
            {
                if (db.VlfCount > _thresholds.VlfHighCount)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("VLF Count High", db.VlfCount, _thresholds.VlfHighCount, "Log", "", db.DatabaseName));

                if (db.DaysSinceLastBackup > _thresholds.MaxBackupDays && db.DatabaseName != "tempdb")
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Missing Backup", db.DaysSinceLastBackup, _thresholds.MaxBackupDays, "Security", "", db.DatabaseName, db.DatabaseName));

                // Practice checks
                if (db.IsAutoShrinkOn)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Bad Practice: Auto-Shrink", 1, 0, "Configuration", "", db.DatabaseName, db.DatabaseName));

                if (db.IsAutoCloseOn)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Bad Practice: Auto-Close", 1, 0, "Configuration", "", db.DatabaseName, db.DatabaseName));

                if (db.IsTrustworthyOn)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Bad Practice: Trustworthy", 1, 0, "Security", "", db.DatabaseName, db.DatabaseName));

                if (db.AvgIoReadLatencyMs > _thresholds.IoHigh || db.AvgIoWriteLatencyMs > _thresholds.IoHigh)
                {
                    double val = Math.Max(db.AvgIoReadLatencyMs, db.AvgIoWriteLatencyMs);
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("High IO Latency", val, _thresholds.IoHigh, "IO", "", db.DatabaseName));
                }

                if (db.ActiveSessions > 100)
                {
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("MySQL: Connection Limit", db.ActiveSessions, 100, "Connections", "", db.DatabaseName));
                }

                // Oracle Tablespace check
                double tablespaceUsage = (1 - (db.DataFileSpaceFreeMb / (db.DatabaseSizeMb > 0 ? db.DatabaseSizeMb : 1))) * 100;
                if (tablespaceUsage > _thresholds.MaxTablespaceUsagePercent)
                {
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("Oracle: Tablespace Full", tablespaceUsage, _thresholds.MaxTablespaceUsagePercent, "Storage", "", db.DatabaseName, "DATA_TS"));
                }

                double logUsage = (1 - (db.LogFileSpaceFreeMb / (db.LogSizeMb > 0 ? db.LogSizeMb : 1))) * 100;
                if (logUsage > _thresholds.LogUsageHigh)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation("High Log Usage", logUsage, _thresholds.LogUsageHigh, "LogUsage", "", db.DatabaseName));

                if (db.ProviderType == "AzureSql" || instance?.ProviderType == "AzureSql")
                {
                    if (instance?.LogWritePercent > _thresholds.AzureLogWriteLimit)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("Azure: Log Write Governance", instance.LogWritePercent, _thresholds.AzureLogWriteLimit, "Azure", instance.ServerName, db.DatabaseName));

                    if (instance?.DatabaseIoPercent > _thresholds.AzureDataIoLimit)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("Azure: Data IO Governance", instance.DatabaseIoPercent, _thresholds.AzureDataIoLimit, "Azure", instance.ServerName, db.DatabaseName));
                }

                // MySQL Specific
                if (db.ProviderType == "MySql")
                {
                    if (db.ActiveSessions > _thresholds.MaxMySqlConnections)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("MySQL: Connection Limit", db.ActiveSessions, _thresholds.MaxMySqlConnections, "Connections", "", db.DatabaseName));
                    
                    if (db.AvgIoReadLatencyMs > _thresholds.IoHigh)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("MySQL: Slow IO Detected", db.AvgIoReadLatencyMs, _thresholds.IoHigh, "IO", "", db.DatabaseName));
                }

                // PostgreSQL Specific
                if (db.ProviderType == "Postgres")
                {
                    if (db.ActiveSessions > _thresholds.MaxPostgresConnections)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("Postgres: High Connection Count", db.ActiveSessions, _thresholds.MaxPostgresConnections, "Connections", "", db.DatabaseName));
                    
                    if (db.DataFileSpaceFreeMb < 500)
                        rawAlerts.Add(_recommendationService.GenerateRecommendation("Postgres: Low Disk Space", db.DataFileSpaceFreeMb, 500, "Storage", "", db.DatabaseName));
                }
            }

            // ── Single-pass query analysis ────────────────────────────────
            foreach (var q in queries)
            {
                double durationMs = q.AvgElapsedTimeUs / 1000.0;

                if (durationMs > _thresholds.LongQueryMs)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation(
                        "Long Running Query", durationMs, _thresholds.LongQueryMs,
                        "Query", "", q.DatabaseName, q.QueryText));

                if (q.Category == "NoIndexUsed" || q.Category?.Contains("Scan", StringComparison.OrdinalIgnoreCase) == true)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation(
                        "Full Table Scan", durationMs, 0,
                        "QueryPlan", "", q.DatabaseName, q.QueryText));

                if (q.Category == "ImplicitConversion")
                    rawAlerts.Add(_recommendationService.GenerateRecommendation(
                        "ImplicitConversion", 1, 0,
                        "QueryPlan", "", q.DatabaseName, q.QueryText));

                if (q.Category == "KeyLookupExpensive")
                    rawAlerts.Add(_recommendationService.GenerateRecommendation(
                        "KeyLookupExpensive", 1, 0,
                        "QueryPlan", "", q.DatabaseName, q.QueryText));
            }

            // ── Single-pass index analysis ────────────────────────────────
            foreach (var idx in indexes)
            {
                switch (idx.IssueType)
                {
                    case "Fragmented" when idx.FragmentationPercent > _thresholds.FragmentationHigh:
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "Index Fragmentation", idx.FragmentationPercent, _thresholds.FragmentationHigh,
                            "Index", "", idx.DatabaseName, $"{idx.TableName}.{idx.IndexName}"));
                        break;

                    case "Missing":
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "Missing Index", idx.ImpactScore, 0,
                            "Index", "", idx.DatabaseName, idx.TableName));
                        break;

                    case "Unused":
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "Unused Index", idx.ImpactScore, 0,
                            "Index", "", idx.DatabaseName, idx.IndexName));
                        break;

                    case "Duplicate":
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "Duplicate Index", idx.ImpactScore, 0,
                            "Index", "", idx.DatabaseName, idx.IndexName));
                        break;

                    case "StaleStats":
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "StaleStats", idx.ImpactScore, 0,
                            "Statistics", "", idx.DatabaseName, $"{idx.TableName}.{idx.IndexName}"));
                        break;

                    case "Heap":
                        rawAlerts.Add(_recommendationService.GenerateRecommendation(
                            "Bad Practice: Heap Table", idx.ImpactScore, 0,
                            "Configuration", "", idx.DatabaseName, idx.TableName));
                        break;
                }
            }

            // ── Blocking & Deadlocks ──────────────────────────────────────
            foreach (var block in blocking)
            {
                if (block.WaitTimeMs > _thresholds.BlockingDurationMs)
                    rawAlerts.Add(_recommendationService.GenerateRecommendation(
                        "Blocking Detected", block.WaitTimeMs, _thresholds.BlockingDurationMs,
                        "Blocking", "", block.DatabaseName));
            }

            if (deadlocks.Count >= _thresholds.DeadlockCount)
            {
                var d = deadlocks.FirstOrDefault();
                rawAlerts.Add(_recommendationService.GenerateRecommendation(
                    "Deadlock Detected", deadlocks.Count, _thresholds.DeadlockCount,
                    "Deadlock", "", d?.DatabaseName ?? ""));
            }

            // ── Wait-stat based alerts (all providers) ────────────────────
            if (instance != null)
            {
                foreach (var ws in instance.TopWaitStats)
                {
                    switch (ws.WaitType)
                    {
                        case "AD_HOC_PLAN_BLOAT":
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "AD_HOC_PLAN_BLOAT", ws.WaitTimeMs, 100, "Memory", instance.ServerName));
                            break;

                        case "SLOW_QUERY_COUNT" when ws.WaitCount > 50:
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "MySQL: Slow Query Count", ws.WaitCount, 50, "Query", instance.ServerName));
                            break;

                        case "INNODB_LOG_WAIT" when ws.WaitCount > 10:
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "MySQL: InnoDB Log Wait", ws.WaitCount, 10, "IO", instance.ServerName));
                            break;

                        case "LOCK_WAIT" when ws.WaitCount > 20:
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "Postgres: Lock Wait", ws.WaitCount, 20, "Blocking", instance.ServerName));
                            break;

                        case "IDLE_IN_TRANSACTION" when ws.WaitCount > 5:
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "Postgres: Idle In Transaction", ws.WaitCount, 5, "Blocking", instance.ServerName));
                            break;

                        case "REPLICA_LAG_SECONDS" when ws.WaitTimeMs > 30000:
                            rawAlerts.Add(_recommendationService.GenerateRecommendation(
                                "Postgres: Replica Lag", ws.WaitTimeMs / 1000.0, 30, "Replication", instance.ServerName));
                            break;
                    }
                }
            }

            // Apply Suppression & History
            return ProcessAlerts(rawAlerts);
        }

        private List<AlertDto> ProcessAlerts(List<AlertDto> newAlerts)
        {
            var filtered = new List<AlertDto>();
            var now = DateTime.UtcNow;

            foreach (var alert in newAlerts)
            {
                // Create unique key based on category, metric, and target
                string key = $"{alert.Category}_{alert.Metric}_{alert.DatabaseName}_{alert.ServerName}";

                // Suppress duplicates within cooldown window
                if (_lastAlertTime.TryGetValue(key, out var lastTime))
                {
                    if (now - lastTime < _alertCooldown)
                    {
                        _logger.LogDebug("Alert suppressed due to cooldown: {Key}", key);
                        continue;
                    }
                }

                // Update history and timestamp
                _lastAlertTime[key] = now;
                _alertHistory[key] = alert;
                
                filtered.Add(alert);
            }

            return filtered;
        }

        public IEnumerable<AlertDto> GetRecommendationHistory() => _alertHistory.Values;
    }

    // ═══════════════════════════════════════════════════════════════
    // RECOMMENDATION SERVICE — generates advice for detected issues
    // ═══════════════════════════════════════════════════════════════
    public class RecommendationService
    {
        public AlertDto GenerateRecommendation(string issueType, double val, double threshold, string category, string server = "", string db = "", string context = "")
        {
            var alert = new AlertDto
            {
                ServerName = server,
                DatabaseName = db,
                Category = category,
                MetricValue = val,
                ThresholdValue = threshold,
                CurrentValue = val,
                RaisedAt = DateTime.UtcNow,
                Metric = issueType,
                AlertId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()
            };

            // Calculate Dynamic Impact Score based on how much the threshold was breached
            double breachRatio = threshold > 0 ? (val / threshold) : 1.0;
            double baseImpact = 0;

            switch (issueType)
            {
                case "High CPU Usage":
                case "High DTU/CPU":
                    baseImpact = 70;
                    alert.Problem = $"High CPU utilization ({val:F1}%) on {server}.";
                    alert.RootCause = breachRatio > 1.5 ? "Critical CPU saturation likely caused by parallel scan or infinite loop." : "High workload or missing indexes causing excessive logical reads.";
                    alert.Recommendation = category == "Azure" 
                        ? "Azure SQL Throttling: Scale up your DTU/vCore tier or optimize query patterns to reduce resource consumption."
                        : "1. Check top CPU consumers. \n2. Verify if MAXDOP is appropriate. \n3. Look for missing indexes in cached plans.";
                    alert.Severity = breachRatio > 1.2 ? "High" : "Medium";
                    break;

                case "Azure: Log Write Governance":
                    baseImpact = 90;
                    alert.Problem = $"Azure SQL Log Write hitting governance limit ({val:F1}%).";
                    alert.RootCause = "Transaction log throughput limit reached for the current Azure service tier.";
                    alert.Recommendation = "Reduce large transaction sizes. Consider batching inserts/updates. Upgrading service tier may be necessary.";
                    alert.Severity = "High";
                    break;

                case "Azure: Data IO Governance":
                    baseImpact = 90;
                    alert.Problem = $"Azure SQL Data IO hitting governance limit ({val:F1}%).";
                    alert.RootCause = "IOPS or throughput limit reached for the current Azure storage tier.";
                    alert.Recommendation = "Increase memory to improve buffer hits. Optimize queries to reduce physical reads. Upgrade tier.";
                    alert.Severity = "High";
                    break;

                case "MySQL: Connection Limit":
                    baseImpact = 85;
                    alert.Problem = $"MySQL active connections ({val:F0}) approaching max_connections.";
                    alert.RootCause = "Application connection pooling issues or unexpected traffic spike.";
                    alert.Recommendation = "1. Check 'show processlist' for hung connections. \n2. Increase 'max_connections' in my.cnf if hardware allows. \n3. Verify application idle timeout.";
                    alert.Severity = "High";
                    break;

                case "MySQL: Slow IO Detected":
                    baseImpact = 60;
                    alert.Problem = $"MySQL disk read latency is high ({val:F1}ms).";
                    alert.RootCause = "InnoDB buffer pool miss or slow storage subsystem.";
                    alert.Recommendation = "Check innodb_buffer_pool_size. If it's too small, increase it to fit dataset in memory. Verify storage IOPS.";
                    alert.Severity = "Medium";
                    break;

                case "Postgres: High Connection Count":
                    baseImpact = 80;
                    alert.Problem = $"PostgreSQL connections ({val:F0}) hitting high watermark.";
                    alert.RootCause = "Each Postgres connection consumes significant memory. High count may lead to OOM.";
                    alert.Recommendation = "Consider using a connection pooler like PGBouncer. Optimize application to close connections faster.";
                    alert.Severity = "High";
                    break;

                case "Postgres: Low Disk Space":
                    baseImpact = 95;
                    alert.Problem = $"PostgreSQL database disk space is very low ({val:F0} MB remaining).";
                    alert.RootCause = "Rapid table growth or failed autovacuum causing bloat.";
                    alert.Recommendation = "1. Check for table bloat using pgstattuple. \n2. Ensure autovacuum is running effectively. \n3. Expand disk volume immediately.";
                    alert.Severity = "Critical";
                    break;

                case "Oracle: Tablespace Full":
                    baseImpact = 90;
                    alert.Problem = $"Oracle Tablespace {context} is {val:F1}% full.";
                    alert.RootCause = "Data growth exceeded allocated datafile size.";
                    alert.Recommendation = $"ALTER TABLESPACE {context} ADD DATAFILE ... SIZE ... AUTOEXTEND ON;";
                    alert.Severity = "High";
                    break;

                case "Low PLE":
                    baseImpact = 80;
                    alert.Problem = $"Page Life Expectancy is low: {val:F0} seconds.";
                    alert.RootCause = "Memory pressure: SQL Server is frequently flushing data from buffer pool to disk.";
                    alert.Recommendation = "Analyze buffer pool usage. Identifiy queries with largest memory grants. Consider adding physical RAM.";
                    alert.Severity = "High";
                    break;

                case "High IO Latency":
                    baseImpact = 50;
                    alert.Problem = $"Disk IO Latency is high ({val:F1} ms) on {db}.";
                    alert.RootCause = "Physical storage is struggling to keep up with IO requests or inefficient queries are causing huge physical reads.";
                    alert.Recommendation = "Check storage subsystem health. Optimize queries to reduce physical IO. Add indexes to avoid large scans.";
                    alert.Severity = val > 50 ? "High" : "Medium";
                    break;

                case "Long Running Query":
                    baseImpact = 40;
                    alert.Problem = $"Query exceeded the {threshold:N0}ms threshold (Actual: {val:N0}ms).";
                    alert.RootCause = "Possible missing index or outdated statistics for the specific query hash.";
                    alert.Recommendation = $"Analyze query plan for: {context.Substring(0, Math.Min(50, context.Length))}... \nAdd missing indexes if suggested.";
                    alert.Severity = breachRatio > 2 ? "High" : "Medium";
                    break;

                case "Index Fragmentation":
                    baseImpact = 20;
                    alert.Problem = $"High fragmentation ({val:F1}%) on index: {context}.";
                    alert.RootCause = "Frequent DML operations (Insert/Update/Delete) on the underlying table.";
                    alert.Recommendation = val > 30 ? $"REBUILD INDEX on {context};" : $"REORGANIZE INDEX on {context};";
                    alert.Severity = "Low";
                    break;

                case "Blocking Detected":
                    baseImpact = 60;
                    alert.Problem = $"Active session blocking detected for {val:F0}ms.";
                    alert.RootCause = "Long-running transactions or missing indexes on filter columns causing row/page lock escalation.";
                    alert.Recommendation = "Identify head blocker. Check for open transactions without commit. Use READ_COMMITTED_SNAPSHOT if possible.";
                    alert.Severity = breachRatio > 5 ? "High" : "Medium";
                    break;

                case "Deadlock Detected":
                    baseImpact = 100;
                    alert.Problem = "Deadlock encountered between two or more processes.";
                    alert.RootCause = "Cyclic dependency: Process A holds Lock 1 and wants Lock 2, while Process B holds Lock 2 and wants Lock 1.";
                    alert.Recommendation = "Review deadlock graph. Ensure consistent object access order. Add missing indexes to reduce lock footprint.";
                    alert.Severity = "High";
                    break;

                case "High Log Usage":
                    baseImpact = 80;
                    alert.Problem = $"Transaction log is {val:F1}% full on {db}.";
                    alert.RootCause = "Active long-running transaction or log backup failure preventing log truncation.";
                    alert.Recommendation = "Check DBCC OPENTRAN. Ensure log backups are running. Increase log file size if growth is legitimate.";
                    alert.Severity = val > 90 ? "Critical" : "High";
                    break;

                case "Missing Backup":
                    baseImpact = 100;
                    alert.Problem = $"No backup found for database {db} for the last {val:F0} days.";
                    alert.RootCause = "Backup maintenance plan failure or new database not included in existing jobs.";
                    alert.Recommendation = $"Execute: BACKUP DATABASE [{db}] TO DISK = '...'; immediately.";
                    alert.Severity = "High";
                    break;

                case "Failed Logins High":
                    baseImpact = 90;
                    alert.Problem = $"Detected {val:F0} failed login attempts in 1 minute.";
                    alert.RootCause = "Potential brute-force attack or misconfigured application service account.";
                    alert.Recommendation = "Check error log for originating IP. Audit failed logins. Change service account passwords if needed.";
                    alert.Severity = "High";
                    break;

                case "Bad Practice: Auto-Shrink":
                    baseImpact = 50;
                    alert.Problem = $"AUTO_SHRINK is enabled on {context}.";
                    alert.RootCause = "Non-standard configuration. Causes severe fragmentation and CPU churn.";
                    alert.Recommendation = $"Run: ALTER DATABASE [{context}] SET AUTO_SHRINK OFF;";
                    alert.Severity = "Medium";
                    break;

                case "ImplicitConversion":
                    baseImpact = 45;
                    alert.Problem = "Implicit conversion detected in query plan.";
                    alert.RootCause = "Data type mismatch between filter parameter and table column forcing a scan.";
                    alert.Recommendation = "Ensure the application parameter data type matches the database column data type (e.g., NVarChar vs VarChar).";
                    alert.Severity = "Medium";
                    break;

                case "AD_HOC_PLAN_BLOAT":
                    baseImpact = 35;
                    alert.Problem = $"Ad-hoc plan cache bloat: {val:F0} MB wasted.";
                    alert.RootCause = "SQL Server is caching many single-use plans, reducing memory available for data pages.";
                    alert.Recommendation = "Enable 'optimize for ad hoc workloads' in SQL Server instance settings.";
                    alert.Severity = "Low";
                    break;

                case "StaleStats":
                    baseImpact = 55;
                    alert.Problem = $"Stale statistics detected on {context}.";
                    alert.RootCause = "Large amount of data changes without a corresponding statistics update, likely leading to poor execution plans.";
                    alert.Recommendation = $"Run: UPDATE STATISTICS {context} WITH FULLSCAN;";
                    alert.Severity = "Medium";
                    break;

                case "KeyLookupExpensive":
                    baseImpact = 30;
                    alert.Problem = "Expensive Key Lookup detected in plan.";
                    alert.RootCause = "The non-clustered index does not cover all columns requested by the SELECT statement.";
                    alert.Recommendation = "Add the identified columns to the INCLUDE list of the non-clustered index.";
                    alert.Severity = "Low";
                    break;

                case "MySQL: Slow Query Count":
                    baseImpact = 50;
                    alert.Problem = $"{val:F0} slow queries detected (exceeding long_query_time).";
                    alert.RootCause = "Queries running beyond long_query_time threshold — likely missing indexes or suboptimal query plans.";
                    alert.Recommendation = "Enable slow query log (slow_query_log=ON) and use EXPLAIN to analyze the slowest queries. Consider pt-query-digest for aggregation.";
                    alert.Severity = val > 200 ? "High" : "Medium";
                    break;

                case "MySQL: InnoDB Log Wait":
                    baseImpact = 65;
                    alert.Problem = $"InnoDB log buffer wait detected ({val:F0} occurrences).";
                    alert.RootCause = "Write throughput exceeds the InnoDB log buffer flush capacity. Possible innodb_log_buffer_size too small or heavy commit rate.";
                    alert.Recommendation = "Increase innodb_log_buffer_size (default 16 MB, try 64-256 MB). Check innodb_log_file_size. Consider innodb_flush_log_at_trx_commit=2 for non-critical workloads.";
                    alert.Severity = "High";
                    break;

                case "Postgres: Lock Wait":
                    baseImpact = 70;
                    alert.Problem = $"{val:F0} sessions waiting on lock acquisition.";
                    alert.RootCause = "Concurrent transactions competing for the same row or table locks. Long transactions holding locks block others.";
                    alert.Recommendation = "Use pg_blocking_pids() to identify the blocking query. Check for long-running transactions. Consider row-level locking strategies and index coverage.";
                    alert.Severity = val > 50 ? "High" : "Medium";
                    break;

                case "Postgres: Idle In Transaction":
                    baseImpact = 75;
                    alert.Problem = $"{val:F0} sessions idle in transaction for > 5 minutes.";
                    alert.RootCause = "Application opened a transaction but did not commit or rollback — holds locks on all modified rows preventing vacuum.";
                    alert.Recommendation = "Set idle_in_transaction_session_timeout = '10min' in postgresql.conf. Audit application connection pooling and transaction management.";
                    alert.Severity = "High";
                    break;

                case "Postgres: Replica Lag":
                    baseImpact = 85;
                    alert.Problem = $"Streaming replication lag is {val:F0} seconds.";
                    alert.RootCause = "Replica cannot apply WAL as fast as primary generates it — network bottleneck, slow replica disk IO, or hot standby queries blocking apply.";
                    alert.Recommendation = "Check pg_stat_replication on primary. Monitor replica disk IO. Consider wal_keep_size. Disable hot standby queries if lag is query-caused.";
                    alert.Severity = val > 300 ? "High" : "Medium";
                    break;

                case "Duplicate Index":
                    baseImpact = 40;
                    alert.Problem = $"Duplicate index detected: {context}.";
                    alert.RootCause = "Two indexes cover the same leading columns, consuming extra disk space and slowing all DML operations.";
                    alert.Recommendation = "Verify both indexes then drop the redundant one. Use the script provided in the Index Analysis report.";
                    alert.Severity = "Low";
                    break;

                case "Full Table Scan":
                    baseImpact = 55;
                    alert.Problem = $"Query performing full table scan on {db}.";
                    alert.RootCause = "No suitable index exists for the filter/join predicates, or statistics are stale causing the optimizer to choose a scan.";
                    alert.Recommendation = "Use EXPLAIN/execution plan to identify the missing index. Run UPDATE STATISTICS or ANALYZE TABLE first to verify the plan is optimal.";
                    alert.Severity = "Medium";
                    break;

                case "Bad Practice: Heap Table":
                    baseImpact = 60;
                    alert.Problem = $"Table '{context}' has no clustered index (heap or no PK).";
                    alert.RootCause = "Tables without a clustered index store rows in no particular order, causing full scans and forwarded record overhead.";
                    alert.Recommendation = "Add a PRIMARY KEY or CLUSTERED INDEX. Use the generated script in the Index Analysis section.";
                    alert.Severity = "Medium";
                    break;

                default:
                    baseImpact = 10;
                    alert.Problem = issueType;
                    alert.RootCause = "Anomaly detected by threshold baseline.";
                    alert.Recommendation = "Investigate the specific metric and compare with historical baseline.";
                    alert.Severity = "Low";
                    break;
            }

            // Apply dynamic factor to impact score (capped at 100)
            alert.ImpactScore = Math.Min(100, baseImpact * Math.Sqrt(breachRatio));
            
            alert.Message = $"{alert.Problem} (Score: {alert.ImpactScore:F0})";
            return alert;
        }
    }
}
