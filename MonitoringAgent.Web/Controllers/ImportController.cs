using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Core.Models;
using MonitoringAgent.Web.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MonitoringAgent.Web.Controllers
{
    public class ImportController : Controller
    {
        private readonly MonitoringDbContext _db;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public ImportController(MonitoringDbContext db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetDirectories(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    // Start with logical drives on Windows
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new { name = d.Name, fullPath = d.Name, isDrive = true })
                        .ToList();
                    return Json(drives);
                }

                if (!Directory.Exists(path))
                    return NotFound("Directory not found.");

                var dirs = Directory.GetDirectories(path)
                    .Select(d => new DirectoryInfo(d))
                    .Select(d => new
                    {
                        name = d.Name,
                        fullPath = d.FullName,
                        isDrive = false
                    })
                    .OrderBy(d => d.name)
                    .ToList();

                var parent = Directory.GetParent(path);
                return Json(new { 
                    currentPath = path,
                    parentPath = parent?.FullName,
                    directories = dirs 
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpGet]
        public IActionResult ListFiles(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                    return BadRequest("Folder path is required.");

                if (!Directory.Exists(folderPath))
                    return NotFound("Directory not found.");

                var files = Directory.GetFiles(folderPath, "payload_*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new
                    {
                        name = f.Name,
                        fullPath = f.FullName,
                        size = (f.Length / 1024.0).ToString("N2") + " KB",
                        lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    })
                    .ToList();

                return Json(files);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ProcessFiles([FromBody] ImportRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.FolderPath) || request.Files == null || !request.Files.Any())
                return BadRequest("Invalid request data.");

            int importedCount = 0;
            int errorCount = 0;

            foreach (var fileName in request.Files)
            {
                try
                {
                    string filePath = Path.Combine(request.FolderPath, fileName);
                    if (!System.IO.File.Exists(filePath)) continue;

                    string json = await System.IO.File.ReadAllTextAsync(filePath);
                    var payloads = JsonSerializer.Deserialize<List<MonitoringPayload>>(json, _jsonOptions);

                    if (payloads != null)
                    {
                        foreach (var payload in payloads)
                        {
                            await SavePayloadToDbAsync(payload, request.ProviderType);
                            importedCount++;
                        }
                    }
                }
                catch (Exception)
                {
                    errorCount++;
                }
            }

            return Json(new { success = true, imported = importedCount, errors = errorCount });
        }

        private async Task SavePayloadToDbAsync(MonitoringPayload payload, string overrideProvider)
        {
            string provider = overrideProvider == "Auto" ? (payload.InstanceMetrics?.ProviderType ?? "SqlServer") : overrideProvider;

            // 1. Instance Metrics
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
                    ProviderType = provider,
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

            // 2. Database Metrics
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
                        ProviderType = provider,
                        Timestamp = dbMetric.CollectedAt == default ? DateTime.UtcNow : dbMetric.CollectedAt
                    });
                }
            }

            // 3. Alerts
            if (payload.Alerts != null)
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
                        ProviderType = provider,
                        RaisedAt = alert.RaisedAt == default ? DateTime.UtcNow : alert.RaisedAt
                    });
                }
            }

            // 4. Queries (Extending from existing monitoring controller logic)
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
                        ProviderType = provider,
                        Timestamp = query.CollectedAt == default ? DateTime.UtcNow : query.CollectedAt
                    });
                }
            }

            // 5. Index Analysis
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
                        DetectedAt = index.AnalyzedAt == default ? DateTime.UtcNow : index.AnalyzedAt,
                        ProviderType = provider
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
                        ProviderType = provider,
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
                        ProviderType = provider,
                        EventTime = deadlock.EventTime == default ? DateTime.UtcNow : deadlock.EventTime
                    });
                }
            }

            // 8. Save Raw Payload
            _db.Payloads.Add(new PayloadEntity
            {
                RawJson = JsonSerializer.Serialize(payload),
                ReceivedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
        }
    }

    public class ImportRequest
    {
        public string FolderPath { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
        public string ProviderType { get; set; } = "SqlServer";
    }
}
