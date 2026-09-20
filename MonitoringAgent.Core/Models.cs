using System;
using System.Collections.Generic;

namespace MonitoringAgent.Core.Models
{
    // ─────────────────────────────────────────────────────────────
    // Instance-level metrics (CPU, Memory, PLE)
    // ─────────────────────────────────────────────────────────────
    public class InstanceMetricDto
    {
        public string ServerName { get; set; } = string.Empty;
        public double CpuUsagePercent { get; set; } // Total System
        public double SqlCpuUsagePercent { get; set; } // SQL Process Only
        public double MemoryUsageMb { get; set; } // Total System Used
        public double MemoryTargetMb { get; set; } // Total System Physical
        public double SqlMemoryUsageMb { get; set; } // SQL Process Only
        public long PageLifeExpectancy { get; set; }
        public long BatchRequestsPerSec { get; set; }
        public long TotalConnections { get; set; }
        public double TempDbSizeMb { get; set; }
        public double TempDbLogSizeMb { get; set; }
        public double BufferCacheHitRatio { get; set; }
        public int FailedLoginsLastMinute { get; set; }
        public int MaxDop { get; set; }
        public int CostThresholdForParallelism { get; set; }
        public int SysAdminCount { get; set; }
        public List<WaitStatDto> TopWaitStats { get; set; } = new();
        public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

        // Multi-Provider & Azure SQL
        public string ProviderType { get; set; } = "SqlServer";
        public double DatabaseIoPercent { get; set; }
        public double LogWritePercent { get; set; }
        public double WorkerUtilizationPercent { get; set; }
        public double SessionUtilizationPercent { get; set; }
        public string AzureServiceTier { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────
    // Per-database metrics (sessions, IO, size)
    // ─────────────────────────────────────────────────────────────
    public class DatabaseMetricDto
    {
        public string DatabaseName { get; set; } = string.Empty;
        public int ActiveSessions { get; set; }
        public long IoReadBytes { get; set; }
        public long IoWriteBytes { get; set; }
        public long IoReadLatencyMs { get; set; }
        public long IoWriteLatencyMs { get; set; }
        public double AvgIoReadLatencyMs { get; set; }
        public double AvgIoWriteLatencyMs { get; set; }
        public double DatabaseSizeMb { get; set; }
        public double DataFileSpaceFreeMb { get; set; }
        public double LogSizeMb { get; set; }
        public double LogFileSpaceFreeMb { get; set; }
        public int VlfCount { get; set; }
        // New Configuration metrics
        public bool IsAutoShrinkOn { get; set; }
        public bool IsAutoCloseOn { get; set; }
        public bool IsTrustworthyOn { get; set; }
        public bool HasPercentGrowth { get; set; }
        public string StateDesc { get; set; } = string.Empty;
        public int DaysSinceLastBackup { get; set; } = -1;
        public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
        public string ProviderType { get; set; } = "SqlServer";
    }

    // ─────────────────────────────────────────────────────────────
    // Query analysis (long-running, CPU-intensive, IO-intensive)
    // ─────────────────────────────────────────────────────────────
    public class QueryMetricDto
    {
        public string DatabaseName { get; set; } = string.Empty;
        public string QueryHash { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public string QueryPlan { get; set; } = string.Empty;

        /// <summary>Total CPU time in microseconds consumed by this query across all executions.</summary>
        public long TotalWorkerTimeUs { get; set; }

        /// <summary>Total logical reads (8KB pages) across all executions.</summary>
        public long TotalLogicalReads { get; set; }

        /// <summary>Total physical reads (8KB pages) across all executions.</summary>
        public long TotalPhysicalReads { get; set; }

        /// <summary>Total logical writes across all executions.</summary>
        public long TotalLogicalWrites { get; set; }

        /// <summary>Total elapsed time in microseconds across all executions.</summary>
        public long TotalElapsedTimeUs { get; set; }

        public long ExecutionCount { get; set; }

        /// <summary>Average CPU time per execution in microseconds (computed).</summary>
        public double AvgCpuTimeUs => ExecutionCount > 0 ? (double)TotalWorkerTimeUs / ExecutionCount : 0;

        /// <summary>
        /// Average elapsed time per execution in microseconds.
        /// Can be set directly by non-SQL Server providers that already have the average.
        /// </summary>
        private double _avgElapsedTimeUs;
        public double AvgElapsedTimeUs
        {
            get => _avgElapsedTimeUs > 0 ? _avgElapsedTimeUs
                   : (ExecutionCount > 0 ? (double)TotalElapsedTimeUs / ExecutionCount : 0);
            set => _avgElapsedTimeUs = value;
        }

        /// <summary>Classification: LongRunning, CpuIntensive, IoIntensive, ImplicitConversion, KeyLookupExpensive, PlanIssue</summary>
        public string Category { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────
    // Index analysis (fragmented, missing, unused, duplicate, heap)
    // ─────────────────────────────────────────────────────────────
    public class IndexAnalysisDto
    {
        public string DatabaseName { get; set; } = string.Empty;
        public string SchemaName { get; set; } = "dbo";
        public string TableName { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;

        /// <summary>Issue type: Fragmented | Missing | Unused | Duplicate | Heap | StaleStats | Bloat | NoIndexUsed</summary>
        public string IssueType { get; set; } = string.Empty;

        /// <summary>Human-readable action: REBUILD, REORGANIZE, CREATE INDEX, DROP INDEX, ADD CLUSTERED INDEX, REMOVE DUPLICATE, UPDATE STATISTICS, OPTIMIZE TABLE</summary>
        public string Suggestion { get; set; } = string.Empty;

        /// <summary>Impact score (higher = more important). For missing indexes calculated from DMV improvement measure.</summary>
        public double ImpactScore { get; set; }

        /// <summary>Fragmentation percentage (only for Fragmented type).</summary>
        public double FragmentationPercent { get; set; }

        /// <summary>Page count of the index.</summary>
        public long PageCount { get; set; }

        /// <summary>Ready-to-execute script for the suggestion.</summary>
        public string Script { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";

        /// <summary>When this issue was detected.</summary>
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Alias for AnalyzedAt — used by multi-provider analyzers.</summary>
        public DateTime DetectedAt { get => AnalyzedAt; set => AnalyzedAt = value; }
    }

    // ─────────────────────────────────────────────────────────────
    // Deadlock events
    // ─────────────────────────────────────────────────────────────
    public class DeadlockEventDto
    {
        public string DatabaseName { get; set; } = string.Empty;
        public string DeadlockGraphXml { get; set; } = string.Empty;
        public List<DeadlockVictimInfo> Victims { get; set; } = new();
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime EventTime { get; set; } = DateTime.UtcNow;
    }

    public class DeadlockVictimInfo
    {
        public int Spid { get; set; }
        public string QueryText { get; set; } = string.Empty;
        public string LockMode { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────
    // Blocking events
    // ─────────────────────────────────────────────────────────────
    public class BlockingEventDto
    {
        public string DatabaseName { get; set; } = string.Empty;
        public int BlockedSpid { get; set; }
        public int BlockingSpid { get; set; }
        public string BlockedQuery { get; set; } = string.Empty;
        public string BlockingQuery { get; set; } = string.Empty;
        public string WaitType { get; set; } = string.Empty;
        public int WaitTimeMs { get; set; }
        public string BlockedResource { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime EventTime { get; set; } = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────
    // Alert DTO raised when thresholds are breached
    // ─────────────────────────────────────────────────────────────
    public class AlertDto
    {
        public string AlertId { get; set; } = Guid.NewGuid().ToString("N");
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;

        /// <summary>Severity: Low | Medium | High</summary>
        public string Severity { get; set; } = "Low";

        /// <summary>Category: CPU, Memory, PLE, Blocking, Deadlock, Index, Query</summary>
        public string Category { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>Specific problem detected</summary>
        public string Problem { get; set; } = string.Empty;

        /// <summary>Identified root cause of the issue</summary>
        public string RootCause { get; set; } = string.Empty;
        
        /// <summary>Actionable advice on how to investigate or resolve the alert.</summary>
        public string Recommendation { get; set; } = string.Empty;

        /// <summary>Numerical impact score (High=100, Medium=50, Low=20)</summary>
        public double ImpactScore { get; set; }
        
        public double CurrentValue { get; set; }
        public double MetricValue { get; set; } // Alias for CurrentValue
        public double ThresholdValue { get; set; }
        public DateTime RaisedAt { get; set; } = DateTime.UtcNow;
        public string Metric { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public string Timestamp => RaisedAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // ─────────────────────────────────────────────────────────────
    // Combined payload for API submission
    // ─────────────────────────────────────────────────────────────
    public class MonitoringPayload
    {
        public InstanceMetricDto? InstanceMetrics { get; set; }
        public List<DatabaseMetricDto> DatabaseMetrics { get; set; } = new();

        /// <summary>Top query analysis results (long-running, CPU, IO, plan issues).</summary>
        public List<QueryMetricDto> TopQueries { get; set; } = new();

        /// <summary>Alias for backwards compat — maps to TopQueries.</summary>
        public List<QueryMetricDto> Queries { get => TopQueries; set => TopQueries.AddRange(value); }

        public List<IndexAnalysisDto> IndexAnalysis { get; set; } = new();
        public List<BlockingEventDto> BlockingEvents { get; set; } = new();
        public List<DeadlockEventDto> DeadlockEvents { get; set; } = new();
        public List<AlertDto> Alerts { get; set; } = new();
        public List<DiskTestResult> DiskTestResults { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────
    // Wait statistics
    // ─────────────────────────────────────────────────────────────
    public class WaitStatDto
    {
        public string WaitType { get; set; } = string.Empty;
        public long WaitTimeMs { get; set; }
        public long WaitCount { get; set; }
        public long MaxWaitTimeMs { get; set; }
        public long SignalWaitTimeMs { get; set; }
    }

    // ─────────────────────────────────────────────────────────────
    // DiskSpd test request and result
    // ─────────────────────────────────────────────────────────────
    public class DiskTestRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
        public string ServerName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int FileSizeGB { get; set; } = 1;
        public int DurationSec { get; set; } = 10;
        public int WritePercent { get; set; } = 0;
        public int BlockSizeKB { get; set; } = 4;
        public int Threads { get; set; } = 1;
        public int QueueDepth { get; set; } = 1;
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    }

    public class DiskTestResult
    {
        public string RequestId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string TestPath { get; set; } = string.Empty;
        
        // Settings used
        public int FileSizeGB { get; set; }
        public int DurationSec { get; set; }
        public int WritePercent { get; set; }
        public int BlockSizeKB { get; set; }
        public int Threads { get; set; }
        public int QueueDepth { get; set; }

        // Metrics
        public double IOPS { get; set; }
        public double MBps { get; set; }
        public double LatencyMs { get; set; }
        public double CpuUsagePercent { get; set; }

        public string RawOutput { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool Success { get; set; }
        public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Database table for persistent disk test history.
    /// </summary>
    public class DiskTestLog
    {
        public int Id { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string TestPath { get; set; } = string.Empty;
        
        public double IOPS { get; set; }
        public double MBps { get; set; }
        public double LatencyMs { get; set; }
        public double CpuUsagePercent { get; set; }
        
        public int FileSizeGB { get; set; }
        public int DurationSec { get; set; }
        public int WritePercent { get; set; }
        public int BlockSizeKB { get; set; }
        public int Threads { get; set; }
        public int QueueDepth { get; set; }

        public string RawOutput { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime TestTime { get; set; } = DateTime.UtcNow;
    }
}
