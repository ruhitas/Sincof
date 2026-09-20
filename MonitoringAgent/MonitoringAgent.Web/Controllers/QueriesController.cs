using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Web.Data;
using MonitoringAgent.Web.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MonitoringAgent.Web.Controllers
{
    public class QueriesController : Controller
    {
        private readonly MonitoringDbContext _db;

        public QueriesController(MonitoringDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string serverName, string databaseName, string category, DateTime? fromDate, DateTime? toDate, int page = 1)
        {
            int pageSize = 10;
            
            ViewBag.Servers = await _db.Queries.Select(q => q.ServerName).Distinct().ToListAsync();
            ViewBag.Databases = await _db.Queries.Select(q => q.DatabaseName).Distinct().ToListAsync();
            ViewBag.Categories = await _db.Queries.Select(q => q.Category).Distinct().ToListAsync();

            var query = _db.Queries.AsQueryable();

            if (!string.IsNullOrEmpty(serverName))
                query = query.Where(q => q.ServerName == serverName);
            
            if (!string.IsNullOrEmpty(databaseName))
                query = query.Where(q => q.DatabaseName == databaseName);

            if (!string.IsNullOrEmpty(category))
                query = query.Where(q => q.Category == category);

            if (fromDate.HasValue)
                query = query.Where(q => q.Timestamp >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(q => q.Timestamp <= toDate.Value);

            // Group by QueryHash directly in the database to avoid loading all into memory
            var groupedQuery = query
                .GroupBy(q => new { q.ProviderType, q.ServerName, q.DatabaseName, q.QueryHash, q.QueryText })
                .Select(g => new QuerySummaryViewModel
                {
                    ProviderType = g.Key.ProviderType,
                    ServerName = g.Key.ServerName,
                    DatabaseName = g.Key.DatabaseName,
                    QueryHash = g.Key.QueryHash,
                    QueryText = g.Key.QueryText,
                    TotalExecutions = g.Sum(x => x.ExecutionCount),
                    AvgCpuTimeUs = g.Average(x => x.AvgCpuTimeUs),
                    AvgElapsedTimeUs = g.Average(x => x.AvgElapsedTimeUs),
                    TotalLogicalReads = g.Sum(x => x.TotalLogicalReads),
                    Category = g.Max(x => x.Category), // Max works better for SQL translation than First
                    LastSeen = g.Max(x => x.Timestamp)
                });

            // Get total count for pagination
            int totalItems = await groupedQuery.CountAsync();

            // Fetch only current page items
            var items = await groupedQuery
                .OrderByDescending(q => q.LastSeen) // Sıralama son tarihten başlayarak
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var paginatedList = new PaginatedList<QuerySummaryViewModel>(items, totalItems, page, pageSize);

            ViewBag.SelectedServer = serverName;
            ViewBag.SelectedDatabase = databaseName;
            ViewBag.SelectedCategory = category;
            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");

            return View(paginatedList);
        }
    }

    public class QuerySummaryViewModel
    {
        public string ProviderType { get; set; } = "SqlServer";
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public string QueryHash { get; set; } = string.Empty;
        public string QueryText { get; set; } = string.Empty;
        public long TotalExecutions { get; set; }
        public double AvgCpuTimeUs { get; set; }
        public double AvgElapsedTimeUs { get; set; }
        public long TotalLogicalReads { get; set; }
        public string Category { get; set; } = string.Empty;
        public System.DateTime LastSeen { get; set; }
    }
}
