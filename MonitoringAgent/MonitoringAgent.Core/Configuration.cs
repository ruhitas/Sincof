using System.Collections.Generic;

namespace MonitoringAgent.Core.Configuration
{
    /// <summary>
    /// Root configuration section bound from appsettings.json → "SqlMonitoring".
    /// </summary>
    public class MonitoringConfig
    {
        public const string SectionName = "SqlMonitoring";

        /// <summary>Backward compatibility for single connection string.</summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>List of remote servers to monitor of various types.</summary>
        public List<DatabaseTarget> TargetServers { get; set; } = new();

        /// <summary>Explicit list of databases to monitor for the primary connection.</summary>
        public string[] IncludeDBs { get; set; } = [];

        /// <summary>Databases to skip (system DBs by default).</summary>
        public string[] ExcludeDBs { get; set; } = ["master", "tempdb", "model", "msdb"];

        public CollectionIntervals CollectionIntervals { get; set; } = new();
        public FeatureToggles FeatureToggles { get; set; } = new();
        public IndexAnalysisConfig IndexAnalysis { get; set; } = new();
        public AlertThresholds AlertThresholds { get; set; } = new();
        public ThresholdsConfig Thresholds { get; set; } = new();
        public ApiConfig Api { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public AdvancedControlsConfig AdvancedControls { get; set; } = new();
        public DiskSpdConfig DiskSpd { get; set; } = new();
    }

    public class CollectionIntervals
    {
        /// <summary>How often to collect instance + database metrics (seconds).</summary>
        public int MetricsSeconds { get; set; } = 60;

        /// <summary>How often to analyze top queries (seconds).</summary>
        public int QueriesSeconds { get; set; } = 300;

        /// <summary>How often to run full index analysis (minutes — expensive operation).</summary>
        public int IndexesMinutes { get; set; } = 1440;

        /// <summary>How often to check for blocking/deadlocks (seconds).</summary>
        public int EventsSeconds { get; set; } = 15;
    }

    public class FeatureToggles
    {
        public bool EnableMetrics { get; set; } = true;
        public bool EnableQueries { get; set; } = true;
        public bool EnableIndexAnalysis { get; set; } = true;
        public bool EnableBlockingDetection { get; set; } = true;
        public bool EnableDeadlockDetection { get; set; } = true;
        public bool EnableAlerts { get; set; } = true;
        public bool EnableApiSending { get; set; } = true;
        
        // Advanced Premium Metrics Toggles
        public bool EnableWaitStats { get; set; } = true;
        public bool EnableTempDbMonitoring { get; set; } = true;
        public bool EnableVlfMonitoring { get; set; } = true;
        public bool EnableFailedLoginTracking { get; set; } = true;
        public bool EnableBufferCacheMonitoring { get; set; } = true;
        public bool EnableDiskSpd { get; set; } = true;

        // Multi-Provider Toggles
        public bool EnablePostgres { get; set; } = true;
        public bool EnableMySql { get; set; } = true;
        public bool EnableOracle { get; set; } = true;
        public bool EnableAzureSql { get; set; } = true;
    }

    public class IndexAnalysisConfig
    {
        /// <summary>Indexes above this % fragmentation are flagged (30% = REBUILD, 10-30% = REORGANIZE).</summary>
        public double FragmentationThreshold { get; set; } = 30.0;

        /// <summary>Indexes not used (seek/scan/lookup = 0) for this many days are flagged as unused.</summary>
        public int UnusedIndexDays { get; set; } = 30;

        /// <summary>Skip indexes with fewer pages than this (small tables are not worth analyzing).</summary>
        public int MinPageCount { get; set; } = 1000;

        /// <summary>Missing index suggestions below this impact score are filtered out.</summary>
        public double MissingIndexMinImpact { get; set; } = 50.0;

        /// <summary>Reorganize threshold (indexes between this and FragmentationThreshold get REORGANIZE).</summary>
        public double ReorganizeThreshold { get; set; } = 10.0;
    }

    public class AlertThresholds
    {
        /// <summary>CPU % thresholds: [Low, Medium, High].</summary>
        public double CpuHighPercent { get; set; } = 90.0;
        public double CpuMediumPercent { get; set; } = 75.0;

        /// <summary>Page Life Expectancy below this value triggers alert.</summary>
        public long PleLowThreshold { get; set; } = 300;
        public long PleCriticalThreshold { get; set; } = 100;

        /// <summary>Blocking wait time threshold in seconds to raise alert.</summary>
        public int BlockingWaitTimeSeconds { get; set; } = 30;

        /// <summary>Memory usage % thresholds.</summary>
        public double MemoryHighPercent { get; set; } = 90.0;

        /// <summary>VLF count high threshold per database.</summary>
        public int VlfHighCount { get; set; } = 50;
        
        /// <summary>TempDB usage in MB threshold to trigger alert.</summary>
        public double TempDbHighUsageMb { get; set; } = 20000.0; // 20 GB
        
        /// <summary>Failed logins in the last cycle to trigger alert.</summary>
        public int FailedLoginHighCount { get; set; } = 10;
        
        /// <summary>Buffer Cache Hit Ratio threshold.</summary>
        public double BufferCacheHitRatioLowThreshold { get; set; } = 95.0;

        /// <summary>Disk I/O latency thresholds in milliseconds.</summary>
        public double DiskReadLatencyHighMs { get; set; } = 50.0;
        public double DiskWriteLatencyHighMs { get; set; } = 50.0;
        
        /// <summary>Days since last backup threshold.</summary>
        public int DaysSinceLastBackupWarning { get; set; } = 1;
    }

    public class ThresholdsConfig
    {
        public double CpuHigh { get; set; } = 80;
        public double IoHigh { get; set; } = 70;
        public int MemoryPressurePLE { get; set; } = 100;
        public int LongQueryMs { get; set; } = 5000;
        public double FragmentationHigh { get; set; } = 30;
        public int DeadlockCount { get; set; } = 1;
        public int BlockingDurationMs { get; set; } = 3000;
        public int UnusedIndexDays { get; set; } = 7;
        public double DTUHigh { get; set; } = 80;
        public double LogUsageHigh { get; set; } = 70;

        // SQL Server Configuration & Health Thresholds
        public int VlfHighCount { get; set; } = 50;
        public int MaxBackupDays { get; set; } = 1;
        public int MaxSysAdminCount { get; set; } = 5;
        public int MaxFailedLoginsPerMin { get; set; } = 10;
        public double MinBufferCacheHitRatio { get; set; } = 95.0;
        public double MaxTempDbSizeMb { get; set; } = 20480; // 20GB
        public double MaxConnectionUsagePercent { get; set; } = 80.0;
        public double MaxTablespaceUsagePercent { get; set; } = 85.0;
        public double AzureLogWriteLimit { get; set; } = 90.0;
        public double AzureDataIoLimit { get; set; } = 90.0;
        public int MaxMySqlConnections { get; set; } = 150;
        public double MinMySqlBufferHitRatio { get; set; } = 90.0;
        public int MaxPostgresConnections { get; set; } = 80;
        public double MinPostgresCacheHitRatio { get; set; } = 95.0;
        public double MaxOracleLibraryCacheWaitMs { get; set; } = 100.0;
    }

    public class ApiConfig
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 30;
        public int RetryCount { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 1000;
    }

    public class LoggingConfig
    {
        public string MinimumLevel { get; set; } = "Information";
        public string FilePath { get; set; } = "Logs/monitoring-.log";
        public int RetainedFileCountLimit { get; set; } = 31;
    }

    public class AdvancedControlsConfig
    {
        public int CircuitBreakerThreshold { get; set; } = 5;
        public int CircuitBreakerBackoffSeconds { get; set; } = 60;
        public int ThrottleMaxSendsPerWindow { get; set; } = 120;
        public int ThrottleWindowSeconds { get; set; } = 60;
        public string[] MaintenanceWindowsUtc { get; set; } = [];
        public int HealthReportIntervalMinutes { get; set; } = 5;
    }

    public class DatabaseTarget
    {
        public string Alias { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        /// <summary>Type: SqlServer | Postgres | MySql | Oracle | AzureSql</summary>
        public string ProviderType { get; set; } = "SqlServer";
        public string[] IncludeDBs { get; set; } = [];
        public string[] ExcludeDBs { get; set; } = [];
        public bool IsEnabled { get; set; } = true;
        public DiskSpdConfig? DiskSpd { get; set; }
    }

    public class DiskSpdConfig
    {
        public string TestPath { get; set; } = string.Empty;
        public int FileSizeGB { get; set; } = 1;
        public int DurationSec { get; set; } = 10;
        public int WritePercent { get; set; } = 0;
        public int BlockSizeKB { get; set; } = 4;
        public int Threads { get; set; } = 1;
        public int QueueDepth { get; set; } = 1;
        /// <summary>Optional override for binary architecture: amd64 | x86 | arm64</summary>
        public string PreferredArchitecture { get; set; } = string.Empty;
    }
}
