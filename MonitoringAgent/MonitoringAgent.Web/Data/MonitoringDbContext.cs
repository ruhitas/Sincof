using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

namespace MonitoringAgent.Web.Data
{
    public class MonitoringDbContext : DbContext
    {
        public MonitoringDbContext(DbContextOptions<MonitoringDbContext> options) : base(options) { }

        public DbSet<PayloadEntity> Payloads { get; set; }
        public DbSet<AlertRecord> Alerts { get; set; }
        public DbSet<InstanceMetricRecord> InstanceMetrics { get; set; }
        public DbSet<DatabaseMetricRecord> DatabaseMetrics { get; set; }
        public DbSet<QueryLog> Queries { get; set; }
        public DbSet<IndexIssue> IndexIssues { get; set; }
        public DbSet<WorkerConfig> WorkerConfigs { get; set; }
        public DbSet<TargetServerConfig> TargetServers { get; set; }
        public DbSet<WaitStatRecord> WaitStats { get; set; }
        public DbSet<BlockingEventRecord> BlockingEvents { get; set; }
        public DbSet<DeadlockEventRecord> DeadlockEvents { get; set; }
        public DbSet<DiskTestLog> DiskTestLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PayloadEntity>().HasKey(p => p.Id);
            modelBuilder.Entity<AlertRecord>().HasKey(a => a.Id);
            modelBuilder.Entity<InstanceMetricRecord>().HasKey(i => i.Id);
            modelBuilder.Entity<DatabaseMetricRecord>().HasKey(d => d.Id);
            modelBuilder.Entity<QueryLog>().HasKey(q => q.Id);
            modelBuilder.Entity<IndexIssue>().HasKey(i => i.Id);
            modelBuilder.Entity<WorkerConfig>().HasKey(w => w.Id);
            modelBuilder.Entity<TargetServerConfig>().HasKey(t => t.Id);
            modelBuilder.Entity<WaitStatRecord>().HasKey(w => w.Id);
            modelBuilder.Entity<BlockingEventRecord>().HasKey(b => b.Id);
            modelBuilder.Entity<DeadlockEventRecord>().HasKey(d => d.Id);
            modelBuilder.Entity<DiskTestLog>().HasKey(d => d.Id);
        }
    }

    public class PayloadEntity
    {
        public int Id { get; set; }
        public string RawJson { get; set; } = string.Empty;
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    }

    public class InstanceMetricRecord
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public double CpuUsagePercent { get; set; } // System Total
        public double SqlCpuUsagePercent { get; set; } // SQL Only
        public double MemoryUsageMb { get; set; } // System Used
        public double MemoryTargetMb { get; set; } // System Total
        public double SqlMemoryUsageMb { get; set; } // SQL Only
        public long PLE { get; set; }
        public double BufferCacheHitRatio { get; set; }
        public long BatchRequestsPerSec { get; set; }
        public long TotalConnections { get; set; }
        public double TempDbSizeMb { get; set; }
        public double TempDbLogSizeMb { get; set; }
        public int FailedLoginsLastMinute { get; set; }
        public int MaxDop { get; set; }
        public int CostThresholdForParallelism { get; set; }
        public int SysAdminCount { get; set; }
        public ICollection<WaitStatRecord> TopWaitStats { get; set; } = new List<WaitStatRecord>();
        public DateTime Timestamp { get; set; }

        public string ProviderType { get; set; } = "SqlServer";
        public double DatabaseIoPercent { get; set; }
        public double LogWritePercent { get; set; }
        public double WorkerUtilizationPercent { get; set; }
        public double SessionUtilizationPercent { get; set; }
        public string AzureServiceTier { get; set; } = string.Empty;
    }

    public class DatabaseMetricRecord
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public double DatabaseSizeMb { get; set; }
        public double LogSizeMb { get; set; }
        public double AvgReadLatencyMs { get; set; }
        public double AvgWriteLatencyMs { get; set; }
        public int ActiveSessions { get; set; }
        public long IoReadBytes { get; set; }
        public long IoWriteBytes { get; set; }
        public long IoReadLatencyMs { get; set; }
        public long IoWriteLatencyMs { get; set; }
        public double DataFileSpaceFreeMb { get; set; }
        public double LogFileSpaceFreeMb { get; set; }
        public int VlfCount { get; set; }
        public bool IsAutoShrinkOn { get; set; }
        public bool IsAutoCloseOn { get; set; }
        public bool IsTrustworthyOn { get; set; }
        public bool HasPercentGrowth { get; set; }
        public string StateDesc { get; set; } = string.Empty;
        public int DaysSinceLastBackup { get; set; }
        public DateTime Timestamp { get; set; }
        public string ProviderType { get; set; } = "SqlServer";
    }

    public class AlertRecord
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Recommendation { get; set; } = string.Empty;
        public string AlertId { get; set; } = string.Empty;
        public string Problem { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public double ImpactScore { get; set; }
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Metric { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime RaisedAt { get; set; }
        public bool IsAcknowledged { get; set; }
    }

    public class QueryLog
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public string QueryHash { get; set; } = string.Empty;
        public string QueryPlan { get; set; } = string.Empty;
        public long ExecutionCount { get; set; }
        public double AvgCpuTimeUs { get; set; }
        public long TotalWorkerTimeUs { get; set; }
        public long TotalLogicalReads { get; set; }
        public long TotalPhysicalReads { get; set; }
        public long TotalLogicalWrites { get; set; }
        public long TotalElapsedTimeUs { get; set; }
        public double AvgElapsedTimeUs { get; set; }
        public string Category { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime Timestamp { get; set; }
    }

    public class IndexIssue
    {
        public int Id { get; set; }
        public string DatabaseName { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string SchemaName { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
        public string Suggestion { get; set; } = string.Empty;
        public double FragmentationPercent { get; set; }
        public long PageCount { get; set; }
        public string IssueType { get; set; } = string.Empty;
        public decimal ImpactScore { get; set; }
        public string Script { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime DetectedAt { get; set; }
    }
    
    public class WaitStatRecord
    {
        public int Id { get; set; }
        public int InstanceMetricRecordId { get; set; }
        public string WaitType { get; set; } = string.Empty;
        public long WaitTimeMs { get; set; }
        public long WaitCount { get; set; }
        public long MaxWaitTimeMs { get; set; }
        public long SignalWaitTimeMs { get; set; }
    }

    public class BlockingEventRecord
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
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

    public class DeadlockEventRecord
    {
        public int Id { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string DeadlockGraphXml { get; set; } = string.Empty;
        public string VictimsJson { get; set; } = "[]"; 
        public string ProviderType { get; set; } = "SqlServer";
        public DateTime EventTime { get; set; } = DateTime.UtcNow;
    }

    public class WorkerConfig
    {
        public int Id { get; set; }
        public string WorkerId { get; set; } = string.Empty; // e.g. "PROD-WORKER-01"
        public string WorkerName { get; set; } = string.Empty;
        
        // Target DB lists (Comma separated)
        public string IncludeDBs { get; set; } = string.Empty;
        public string ExcludeDBs { get; set; } = string.Empty;
        
        // Complex settings (Stored as JSON)
        public string IntervalsJson { get; set; } = string.Empty;
        public string FeatureTogglesJson { get; set; } = string.Empty;
        public string ThresholdsJson { get; set; } = string.Empty;
        public string ApiSettingsJson { get; set; } = string.Empty;
        public string IndexAnalysisJson { get; set; } = string.Empty;
        public string AdvancedControlsJson { get; set; } = string.Empty;
        public string LoggingJson { get; set; } = string.Empty;
        
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class TargetServerConfig
    {
        public int Id { get; set; }
        public int WorkerConfigId { get; set; } // Link to Parent config
        public string Alias { get; set; } = string.Empty;
        public string ProviderType { get; set; } = "SqlServer"; // SqlServer, Postgres, Oracle, AzureSql
        public string ConnectionString { get; set; } = string.Empty;
        public string IncludeDBs { get; set; } = string.Empty; // Comma separated for this server
        public bool IsEnabled { get; set; } = true;
    }

    public class DiskTestLog
    {
        public int Id { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string TestPath { get; set; } = string.Empty;

        // Metrics
        public double IOPS { get; set; }
        public double MBps { get; set; }
        public double LatencyMs { get; set; }
        public double CpuUsagePercent { get; set; }

        // Settings used
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
