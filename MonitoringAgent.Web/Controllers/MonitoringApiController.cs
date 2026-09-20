using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Models;
using MonitoringAgent.Web.Data;
using System.Text.Json;

namespace MonitoringAgent.Web.Controllers
{
    [ApiController]
    [Route("api/monitoring")]
    public class MonitoringApiController : ControllerBase
    {
        private readonly MonitoringDbContext _db;
        private readonly ILogger<MonitoringApiController> _logger;

        public MonitoringApiController(MonitoringDbContext db, ILogger<MonitoringApiController> logger)
        {
            _db = db;
            _logger = logger;
        }

        [HttpPost("payload/{provider?}")]
        public async Task<IActionResult> ReceivePayload([FromBody] MonitoringPayload payload, string? provider = null)
        {
            if (payload == null) return BadRequest();

            string currentProvider = provider ?? payload.InstanceMetrics?.ProviderType ?? "SqlServer";
            _logger.LogInformation("Received {Provider} payload from {Server}", currentProvider, payload.InstanceMetrics?.ServerName);

            // 1. Process Instance Metrics
            if (payload.InstanceMetrics != null)
            {
                var instanceMetric = new InstanceMetricRecord
                {
                    ServerName = payload.InstanceMetrics.ServerName,
                    CpuUsagePercent = payload.InstanceMetrics.CpuUsagePercent,
                    SqlCpuUsagePercent = payload.InstanceMetrics.SqlCpuUsagePercent,
                    MemoryUsageMb = payload.InstanceMetrics.MemoryUsageMb,
                    MemoryTargetMb = payload.InstanceMetrics.MemoryTargetMb,
                    SqlMemoryUsageMb = payload.InstanceMetrics.SqlMemoryUsageMb,
                    PLE = payload.InstanceMetrics.PageLifeExpectancy,
                    BufferCacheHitRatio = payload.InstanceMetrics.BufferCacheHitRatio,
                    BatchRequestsPerSec = payload.InstanceMetrics.BatchRequestsPerSec,
                    TotalConnections = payload.InstanceMetrics.TotalConnections,
                    TempDbSizeMb = payload.InstanceMetrics.TempDbSizeMb,
                    TempDbLogSizeMb = payload.InstanceMetrics.TempDbLogSizeMb,
                    FailedLoginsLastMinute = payload.InstanceMetrics.FailedLoginsLastMinute,
                    MaxDop = payload.InstanceMetrics.MaxDop,
                    CostThresholdForParallelism = payload.InstanceMetrics.CostThresholdForParallelism,
                    SysAdminCount = payload.InstanceMetrics.SysAdminCount,
                    ProviderType = currentProvider,
                    DatabaseIoPercent = payload.InstanceMetrics.DatabaseIoPercent,
                    LogWritePercent = payload.InstanceMetrics.LogWritePercent,
                    WorkerUtilizationPercent = payload.InstanceMetrics.WorkerUtilizationPercent,
                    SessionUtilizationPercent = payload.InstanceMetrics.SessionUtilizationPercent,
                    AzureServiceTier = payload.InstanceMetrics.AzureServiceTier,
                    Timestamp = payload.InstanceMetrics.CollectedAt == default ? DateTime.UtcNow : payload.InstanceMetrics.CollectedAt
                };
                
                if (payload.InstanceMetrics.TopWaitStats != null)
                {
                    foreach (var wait in payload.InstanceMetrics.TopWaitStats)
                    {
                        instanceMetric.TopWaitStats.Add(new WaitStatRecord
                        {
                            WaitType = wait.WaitType,
                            WaitTimeMs = wait.WaitTimeMs,
                            WaitCount = wait.WaitCount,
                            MaxWaitTimeMs = wait.MaxWaitTimeMs,
                            SignalWaitTimeMs = wait.SignalWaitTimeMs
                        });
                    }
                }
                
                _db.InstanceMetrics.Add(instanceMetric);
            }

            // 2. Process Alerts
            if (payload.Alerts != null && payload.Alerts.Count > 0)
            {
                foreach (var alert in payload.Alerts)
                {
                    _db.Alerts.Add(new AlertRecord
                    {
                        ServerName = alert.ServerName,
                        DatabaseName = alert.DatabaseName,
                        Severity = alert.Severity,
                        Category = alert.Category,
                        Message = alert.Message,
                        Recommendation = alert.Recommendation,
                        AlertId = alert.AlertId,
                        Problem = alert.Problem,
                        RootCause = alert.RootCause,
                        ImpactScore = alert.ImpactScore,
                        CurrentValue = alert.CurrentValue,
                        ThresholdValue = alert.ThresholdValue,
                        Metric = alert.Metric,
                        ProviderType = currentProvider,
                        RaisedAt = alert.RaisedAt == default ? DateTime.UtcNow : alert.RaisedAt
                    });
                }
            }

            // 3. Process Database Metrics
            if (payload.DatabaseMetrics != null)
            {
                foreach (var dbMetric in payload.DatabaseMetrics)
                {
                    _db.DatabaseMetrics.Add(new DatabaseMetricRecord
                    {
                        ServerName = payload.InstanceMetrics?.ServerName ?? "Unknown",
                        DatabaseName = dbMetric.DatabaseName,
                        DatabaseSizeMb = dbMetric.DatabaseSizeMb,
                        LogSizeMb = dbMetric.LogSizeMb,
                        AvgReadLatencyMs = dbMetric.AvgIoReadLatencyMs,
                        AvgWriteLatencyMs = dbMetric.AvgIoWriteLatencyMs,
                        ActiveSessions = dbMetric.ActiveSessions,
                        IoReadBytes = dbMetric.IoReadBytes,
                        IoWriteBytes = dbMetric.IoWriteBytes,
                        IoReadLatencyMs = dbMetric.IoReadLatencyMs,
                        IoWriteLatencyMs = dbMetric.IoWriteLatencyMs,
                        DataFileSpaceFreeMb = dbMetric.DataFileSpaceFreeMb,
                        LogFileSpaceFreeMb = dbMetric.LogFileSpaceFreeMb,
                        VlfCount = dbMetric.VlfCount,
                        IsAutoShrinkOn = dbMetric.IsAutoShrinkOn,
                        IsAutoCloseOn = dbMetric.IsAutoCloseOn,
                        IsTrustworthyOn = dbMetric.IsTrustworthyOn,
                        HasPercentGrowth = dbMetric.HasPercentGrowth,
                        StateDesc = dbMetric.StateDesc,
                        DaysSinceLastBackup = dbMetric.DaysSinceLastBackup,
                        ProviderType = currentProvider,
                        Timestamp = dbMetric.CollectedAt == default ? DateTime.UtcNow : dbMetric.CollectedAt
                    });
                }
            }

            // 4. Process Queries
            if (payload.Queries != null)
            {
                foreach (var query in payload.Queries)
                {
                    _db.Queries.Add(new QueryLog
                    {
                        ServerName = payload.InstanceMetrics?.ServerName ?? "Unknown",
                        DatabaseName = query.DatabaseName,
                        QueryText = query.QueryText,
                        QueryHash = query.QueryHash,
                        QueryPlan = query.QueryPlan,
                        ExecutionCount = query.ExecutionCount,
                        AvgCpuTimeUs = query.AvgCpuTimeUs,
                        TotalWorkerTimeUs = query.TotalWorkerTimeUs,
                        TotalLogicalReads = query.TotalLogicalReads,
                        TotalPhysicalReads = query.TotalPhysicalReads,
                        TotalLogicalWrites = query.TotalLogicalWrites,
                        TotalElapsedTimeUs = query.TotalElapsedTimeUs,
                        AvgElapsedTimeUs = query.AvgElapsedTimeUs,
                        Category = query.Category,
                        ProviderType = currentProvider,
                        Timestamp = query.CollectedAt == default ? DateTime.UtcNow : query.CollectedAt
                    });
                }
            }

            // 5. Process Index Analysis
            if (payload.IndexAnalysis != null)
            {
                foreach (var index in payload.IndexAnalysis)
                {
                    _db.IndexIssues.Add(new IndexIssue
                    {
                        DatabaseName = index.DatabaseName,
                        TableName = index.TableName,
                        SchemaName = index.SchemaName,
                        IndexName = index.IndexName,
                        Suggestion = index.Suggestion,
                        FragmentationPercent = index.FragmentationPercent,
                        PageCount = index.PageCount,
                        IssueType = index.IssueType,
                        ImpactScore = (decimal)index.ImpactScore,
                        Script = index.Script,
                        ProviderType = currentProvider,
                        DetectedAt = index.AnalyzedAt == default ? DateTime.UtcNow : index.AnalyzedAt
                    });
                }
            }

            // 6. Blocking Events
            if (payload.BlockingEvents != null)
            {
                foreach (var block in payload.BlockingEvents)
                {
                    _db.BlockingEvents.Add(new BlockingEventRecord
                    {
                        ServerName = payload.InstanceMetrics?.ServerName ?? "Unknown",
                        DatabaseName = block.DatabaseName,
                        BlockedSpid = block.BlockedSpid,
                        BlockingSpid = block.BlockingSpid,
                        BlockedQuery = block.BlockedQuery,
                        BlockingQuery = block.BlockingQuery,
                        WaitType = block.WaitType,
                        WaitTimeMs = block.WaitTimeMs,
                        BlockedResource = block.BlockedResource,
                        ProviderType = currentProvider,
                        EventTime = block.EventTime == default ? DateTime.UtcNow : block.EventTime
                    });
                }
            }

            // 7. Deadlock Events
            if (payload.DeadlockEvents != null)
            {
                foreach (var deadlock in payload.DeadlockEvents)
                {
                    _db.DeadlockEvents.Add(new DeadlockEventRecord
                    {
                        ServerName = payload.InstanceMetrics?.ServerName ?? "Unknown",
                        DatabaseName = deadlock.DatabaseName,
                        DeadlockGraphXml = deadlock.DeadlockGraphXml,
                        VictimsJson = System.Text.Json.JsonSerializer.Serialize(deadlock.Victims),
                        ProviderType = currentProvider,
                        EventTime = deadlock.EventTime == default ? DateTime.UtcNow : deadlock.EventTime
                    });
                }
            }

            // 4. Save Raw Payload
            _db.Payloads.Add(new PayloadEntity
            {
                RawJson = JsonSerializer.Serialize(payload),
                ReceivedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            return Ok(new { status = "Success" });
        }

        [HttpPost("alerts")]
        public async Task<IActionResult> ReceiveAlerts([FromBody] List<AlertDto> alerts)
        {
            if (alerts == null || alerts.Count == 0) return Ok();

            foreach (var alert in alerts)
            {
                _db.Alerts.Add(new AlertRecord
                {
                    ServerName = alert.ServerName,
                    DatabaseName = alert.DatabaseName,
                    Severity = alert.Severity,
                    Category = alert.Category,
                    Message = alert.Message,
                    Recommendation = alert.Recommendation,
                    AlertId = alert.AlertId,
                    Problem = alert.Problem,
                    RootCause = alert.RootCause,
                    ImpactScore = alert.ImpactScore,
                    CurrentValue = alert.CurrentValue,
                    ThresholdValue = alert.ThresholdValue,
                    Metric = alert.Metric,
                    ProviderType = alert.ProviderType,
                    RaisedAt = alert.RaisedAt
                });
            }

            await _db.SaveChangesAsync();
            return Ok();
        }
    }
}
