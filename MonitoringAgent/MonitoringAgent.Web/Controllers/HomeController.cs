using Microsoft.AspNetCore.Mvc;
using MonitoringAgent.Web.Data;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Web.Models;
using System.Diagnostics;

namespace MonitoringAgent.Web.Controllers;

public class HomeController : Controller
{
    private readonly MonitoringDbContext _db;

    public HomeController(MonitoringDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(string dbType = "All")
    {
        var alertsQuery = _db.Alerts.AsQueryable();
        var metricsQuery = _db.InstanceMetrics.AsQueryable();
        var indexQuery = _db.IndexIssues.AsQueryable();
        var blockingQuery = _db.BlockingEvents.AsQueryable();
        var deadlockQuery = _db.DeadlockEvents.AsQueryable();

        if (dbType != "All")
        {
            alertsQuery = alertsQuery.Where(a => a.ProviderType == dbType);
            metricsQuery = metricsQuery.Where(i => i.ProviderType == dbType);
            indexQuery = indexQuery.Where(i => i.ProviderType == dbType);
            blockingQuery = blockingQuery.Where(b => b.ProviderType == dbType);
            deadlockQuery = deadlockQuery.Where(d => d.ProviderType == dbType);
        }

        var recentRaw = await alertsQuery
            .Where(a => !a.IsAcknowledged)
            .OrderByDescending(a => a.RaisedAt)
            .Take(100)
            .ToListAsync();

        var recentGrouped = recentRaw
            .GroupBy(a => new { a.ServerName, a.DatabaseName, a.Category, a.Message })
            .Select(g => new GroupedAlertDto
            {
                ServerName = g.Key.ServerName,
                DatabaseName = g.Key.DatabaseName,
                Category = g.Key.Category,
                Message = g.Key.Message,
                Severity = g.First().Severity,
                Recommendation = g.First().Recommendation,
                LatestRaisedAt = g.Max(x => x.RaisedAt),
                OccurrenceCount = g.Count(),
                AlertIds = g.Select(x => x.Id).ToList()
            })
            .OrderByDescending(x => x.LatestRaisedAt)
            .Take(10)
            .ToList();

        var past24Hours = DateTime.UtcNow.AddDays(-1);

        var stats = new DashboardViewModel
        {
            TotalAlerts = await alertsQuery.CountAsync(a => !a.IsAcknowledged),
            HighSeverityAlerts = await alertsQuery.CountAsync(a => a.Severity == "High" && !a.IsAcknowledged),
            ActiveBlockingCount = await blockingQuery.CountAsync(b => b.EventTime >= past24Hours),
            RecentDeadlocksCount = await deadlockQuery.CountAsync(d => d.EventTime >= past24Hours),
            MissingIndexesCount = await indexQuery.CountAsync(i => i.IssueType == "Missing"),
            AverageImpactScore = await alertsQuery.Where(a => !a.IsAcknowledged && a.ImpactScore > 0).AverageAsync(a => (double?)a.ImpactScore) ?? 0,
            LastMetrics = await metricsQuery.OrderByDescending(i => i.Timestamp).Take(10).ToListAsync(),
            RecentAlerts = recentGrouped,
            SelectedDbType = dbType,
            PerformanceTrend = await metricsQuery
                .OrderByDescending(i => i.Timestamp)
                .Take(20)
                .Select(i => new { i.Timestamp, i.CpuUsagePercent })
                .ToListAsync()
        };
        return View(stats);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
