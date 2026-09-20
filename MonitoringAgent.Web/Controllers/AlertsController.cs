using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Web.Data;
using MonitoringAgent.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MonitoringAgent.Web.Controllers
{
    public class AlertsController : Controller
    {
        private readonly MonitoringDbContext _db;

        public AlertsController(MonitoringDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string severity, string category, string database, string targetResource, string search, DateTime? fromDate, DateTime? toDate, int page = 1)
        {
            int pageSize = 10;
            var query = _db.Alerts.Where(a => !a.IsAcknowledged);

            // Fetch distinct lookup data for filters - Dynamically from DB
            ViewBag.Databases = await _db.Alerts.Select(a => a.DatabaseName).Distinct().OrderBy(x => x).ToListAsync();
            ViewBag.Categories = await _db.Alerts.Select(a => a.Category).Distinct().OrderBy(x => x).ToListAsync();
            ViewBag.Metrics = await _db.Alerts.Select(a => a.Metric).Where(m => m != null).Distinct().OrderBy(x => x).ToListAsync();

            if (fromDate.HasValue)
                query = query.Where(a => a.RaisedAt >= fromDate.Value);
            
            if (toDate.HasValue)
                query = query.Where(a => a.RaisedAt <= toDate.Value);

            if (!string.IsNullOrEmpty(severity))
                query = query.Where(a => a.Severity == severity);

            if (!string.IsNullOrEmpty(category))
                query = query.Where(a => a.Category == category);

            // Dynamic Metric Filter
            string metric = Request.Query["metric"];
            if (!string.IsNullOrEmpty(metric))
            {
                query = query.Where(a => a.Metric == metric);
                ViewBag.MetricFilter = metric;
            }

            if (!string.IsNullOrEmpty(database))
                query = query.Where(a => a.DatabaseName == database);

            if (!string.IsNullOrEmpty(targetResource) && targetResource.Contains("|||"))
            {
                var parts = targetResource.Split("|||", 2);
                var trDb = parts[0];
                var trSrv = parts[1];
                query = query.Where(a => (a.DatabaseName ?? "") == trDb && (a.ServerName ?? "") == trSrv);
            }

            if (!string.IsNullOrEmpty(search))
                query = query.Where(a => a.Message.Contains(search) || (a.Problem != null && a.Problem.Contains(search)));

            // Build Target Resource pairs dynamically for the filter
            var targetResourcePairs = await _db.Alerts
                .Select(a => new { a.DatabaseName, a.ServerName })
                .Distinct()
                .ToListAsync();
            ViewBag.TargetResources = targetResourcePairs
                .Select(p => new { 
                    Value = (p.DatabaseName ?? "") + "|||" + (p.ServerName ?? ""),
                    Display = (string.IsNullOrEmpty(p.DatabaseName) ? "Instance" : p.DatabaseName) + " (" + (p.ServerName ?? "") + ")"
                })
                .OrderBy(p => p.Display)
                .ToList();

            // Handle new metric filter if passed (usually from UI)
            string mParam = Request.Query["metric"];
            if (!string.IsNullOrEmpty(mParam))
            {
                query = query.Where(a => a.Metric == mParam);
                ViewBag.MetricFilter = mParam;
            }

            var rawAlerts = await query.OrderByDescending(a => a.RaisedAt).ToListAsync();

            var groupedList = rawAlerts
                .GroupBy(a => new { a.ServerName, a.DatabaseName, a.Category, a.Message, a.Problem, a.RootCause })
                .Select(g => {
                    var first = g.OrderByDescending(x => x.RaisedAt).First();
                    return new GroupedAlertDto
                    {
                        ServerName = g.Key.ServerName,
                        DatabaseName = g.Key.DatabaseName,
                        Severity = first.Severity,
                        Category = g.Key.Category,
                        Message = g.Key.Message,
                        Recommendation = first.Recommendation,
                        Problem = g.Key.Problem,
                        RootCause = g.Key.RootCause,
                        ImpactScore = first.ImpactScore,
                        CurrentValue = first.CurrentValue,
                        ThresholdValue = first.ThresholdValue,
                        Metric = first.Metric,
                        AlertId = first.AlertId,
                        LatestRaisedAt = first.RaisedAt,
                        OccurrenceCount = g.Count(),
                        AlertIds = g.Select(x => x.Id).ToList()
                    };
                })
                .OrderByDescending(x => x.LatestRaisedAt)
                .ToList();

            var paginated = new PaginatedList<GroupedAlertDto>(
                groupedList.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                groupedList.Count, page, pageSize);

            ViewBag.Severity = severity;
            ViewBag.Category = category;
            ViewBag.Database = database;
            ViewBag.TargetResource = targetResource;
            ViewBag.Search = search;
            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");

            return View(paginated);
        }

        [HttpPost]
        public async Task<IActionResult> Acknowledge(string ids)
        {
            if (string.IsNullOrEmpty(ids)) return RedirectToAction(nameof(Index));

            var idList = ids.Split(',').Select(int.Parse).ToList();
            var alerts = await _db.Alerts.Where(a => idList.Contains(a.Id)).ToListAsync();
            
            foreach (var alert in alerts)
            {
                alert.IsAcknowledged = true;
            }

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> GetHistory(string server, string database, string category, string message)
        {
            // Safely handle parameters to prevent NullReferenceException on ToLower()
            string dbParam = (string.IsNullOrWhiteSpace(database) || database == "Instance") ? null : database.Trim();
            string srvParam = (server ?? "").Trim();
            string catParam = (category ?? "").Trim();
            string msgParam = (message ?? "").Trim();

            // Perform query with null-safe string comparisons
            var history = await _db.Alerts
                .Where(a => !a.IsAcknowledged &&
                            (a.ServerName != null && a.ServerName.ToLower() == srvParam.ToLower()) &&
                            (a.Category != null && a.Category.ToLower() == catParam.ToLower()) &&
                            a.Message == msgParam)
                .ToListAsync();

            // Filter by database name (Instance level vs Database level)
            if (string.IsNullOrEmpty(dbParam))
            {
                history = history.Where(a => string.IsNullOrEmpty(a.DatabaseName)).ToList();
            }
            else
            {
                history = history.Where(a => a.DatabaseName != null && a.DatabaseName.ToLower() == dbParam.ToLower()).ToList();
            }

            var result = history.OrderByDescending(a => a.RaisedAt)
                .Select(a => new {
                    a.RaisedAt,
                    a.Severity
                })
                .ToList();

            // Group by hour for the chart
            var chartData = result
                .GroupBy(a => new { a.RaisedAt.Date, a.RaisedAt.Hour })
                .Select(g => new {
                    Timestamp = new DateTime(g.Key.Date.Year, g.Key.Date.Month, g.Key.Date.Day, g.Key.Hour, 0, 0),
                    Count = g.Count()
                })
                .OrderBy(x => x.Timestamp)
                .ToList();

            return Json(new { history = result, chartData });
        }
    }
}
