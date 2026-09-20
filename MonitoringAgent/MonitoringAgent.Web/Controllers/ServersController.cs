using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Web.Data;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace MonitoringAgent.Web.Controllers
{
    public class ServersController : Controller
    {
        private readonly MonitoringDbContext _db;

        public ServersController(MonitoringDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            var workers = await _db.WorkerConfigs.ToListAsync();
            return View(workers);
        }

        [HttpPost]
        public async Task<IActionResult> CreateWorker(string workerId, string workerName)
        {
            if (string.IsNullOrEmpty(workerId)) return RedirectToAction(nameof(Index));

            var config = new WorkerConfig
            {
                WorkerId = workerId,
                WorkerName = workerName ?? workerId,
                LastUpdated = DateTime.UtcNow,
                IncludeDBs = "YourAppDb",
                ExcludeDBs = "master, tempdb, model, msdb",
                IntervalsJson = @"{ ""MetricsSeconds"": 60, ""QueriesSeconds"": 300, ""IndexesMinutes"": 1440, ""EventsSeconds"": 15 }",
                FeatureTogglesJson = @"{ ""EnableMetrics"": true, ""EnableQueries"": true, ""EnableIndexAnalysis"": true, ""EnableBlockingDetection"": true, ""EnableDeadlockDetection"": true, ""EnableAlerts"": true, ""EnableApiSending"": false, ""EnableWaitStats"": true, ""EnableTempDbMonitoring"": true, ""EnableVlfMonitoring"": true, ""EnableFailedLoginTracking"": true, ""EnableBufferCacheMonitoring"": true }",
                ThresholdsJson = @"{ ""CpuHighPercent"": 90.0, ""CpuMediumPercent"": 75.0, ""PleLowThreshold"": 300, ""PleCriticalThreshold"": 100, ""BlockingWaitTimeSeconds"": 30, ""MemoryHighPercent"": 90.0, ""VlfHighCount"": 50, ""TempDbHighUsageMb"": 20480.0, ""FailedLoginHighCount"": 10, ""BufferCacheHitRatioLowThreshold"": 95.0 }",
                IndexAnalysisJson = @"{ ""FragmentationThreshold"": 30.0, ""ReorganizeThreshold"": 10.0, ""UnusedIndexDays"": 30, ""MinPageCount"": 1000, ""MissingIndexMinImpact"": 50.0 }",
                AdvancedControlsJson = @"{ ""CircuitBreakerThreshold"": 5, ""CircuitBreakerBackoffSeconds"": 60, ""ThrottleMaxSendsPerWindow"": 120, ""ThrottleWindowSeconds"": 60, ""MaintenanceWindowsUtc"": [ ""02:00-04:00"" ], ""HealthReportIntervalMinutes"": 5 }",
                ApiSettingsJson = @"{ ""BaseUrl"": ""http://localhost:5230"", ""ApiKey"": ""CHANGE_ME_API_KEY"", ""TimeoutSeconds"": 30, ""RetryCount"": 3, ""RetryDelayMs"": 1000 }",

                LoggingJson = @"{ ""MinimumLevel"": ""Information"", ""FilePath"": ""Logs/monitoring-.log"", ""RetainedFileCountLimit"": 31 }"
            };

            _db.WorkerConfigs.Add(config);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Manage(int id)
        {
            var worker = await _db.WorkerConfigs.FirstOrDefaultAsync(w => w.Id == id);
            if (worker == null) return NotFound();

            ViewBag.TargetServers = await _db.TargetServers.Where(t => t.WorkerConfigId == id).ToListAsync();
            return View(worker);
        }

        [HttpPost]
        public async Task<IActionResult> AddTargetServer(TargetServerConfig server)
        {
            if (server.WorkerConfigId == 0) return BadRequest();
            
            _db.TargetServers.Add(server);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Manage), new { id = server.WorkerConfigId });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteTargetServer(int id, int workerConfigId)
        {
            var server = await _db.TargetServers.FindAsync(id);
            if (server != null)
            {
                _db.TargetServers.Remove(server);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Manage), new { id = workerConfigId });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateParams(int id, string type, string json)
        {
            var worker = await _db.WorkerConfigs.FindAsync(id);
            if (worker == null) return NotFound();

            switch (type)
            {
                case "Intervals": worker.IntervalsJson = json; break;
                case "Toggles": worker.FeatureTogglesJson = json; break;
                case "Thresholds": worker.ThresholdsJson = json; break;
                case "Api": worker.ApiSettingsJson = json; break;
                case "IndexAnalysis": worker.IndexAnalysisJson = json; break;
                case "AdvancedControls": worker.AdvancedControlsJson = json; break;
                case "Logging": worker.LoggingJson = json; break;
                case "DBs":
                    // Parse from JSON or similar if needed
                    break;
            }

            worker.LastUpdated = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> DeleteWorker(int id)
        {
            var worker = await _db.WorkerConfigs.FindAsync(id);
            if (worker != null)
            {
                var servers = _db.TargetServers.Where(t => t.WorkerConfigId == id);
                _db.TargetServers.RemoveRange(servers);
                _db.WorkerConfigs.Remove(worker);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
