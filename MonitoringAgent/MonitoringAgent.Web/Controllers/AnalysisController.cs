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
    public class AnalysisController : Controller
    {
        private readonly MonitoringDbContext _db;

        public AnalysisController(MonitoringDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string serverName, string dbType, DateTime? fromDate, DateTime? toDate)
        {
            var servers = await _db.InstanceMetrics.Select(m => m.ServerName).Distinct().ToListAsync();
            var dbTypes = await _db.InstanceMetrics.Select(m => m.ProviderType).Distinct().ToListAsync();
            
            ViewBag.Servers = servers;
            ViewBag.DbTypes = dbTypes;
            ViewBag.SelectedServer = serverName;
            ViewBag.SelectedDbType = dbType ?? "All";

            DateTime start = fromDate ?? DateTime.UtcNow.AddHours(-24);
            DateTime end = toDate ?? DateTime.UtcNow;

            var metricsQuery = _db.InstanceMetrics.AsNoTracking();
            if (!string.IsNullOrEmpty(serverName))
                metricsQuery = metricsQuery.Where(m => m.ServerName == serverName);
            if (!string.IsNullOrEmpty(dbType) && dbType != "All")
                metricsQuery = metricsQuery.Where(m => m.ProviderType == dbType);
            
            var metrics = await metricsQuery
                .Where(m => m.Timestamp >= start && m.Timestamp <= end)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            var dbMetricsQuery = _db.DatabaseMetrics.AsNoTracking();
            if (!string.IsNullOrEmpty(serverName))
                dbMetricsQuery = dbMetricsQuery.Where(m => m.ServerName == serverName);
            if (!string.IsNullOrEmpty(dbType) && dbType != "All")
                dbMetricsQuery = dbMetricsQuery.Where(m => m.ProviderType == dbType);

            var dbMetrics = await dbMetricsQuery
                .Where(m => m.Timestamp >= start && m.Timestamp <= end)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            ViewBag.FromDate = start.ToString("yyyy-MM-ddTHH:mm");
            ViewBag.ToDate = end.ToString("yyyy-MM-ddTHH:mm");

            var viewModel = new AnalysisViewModel
            {
                InstanceMetrics = metrics,
                DatabaseMetrics = dbMetrics
            };

            return View(viewModel);
        }
    }
}
