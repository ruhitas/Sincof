using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MonitoringAgent.Web.Data;
using System.Text.Json;

namespace MonitoringAgent.Web.Controllers
{
    public class DiskAnalysisController : Controller
    {
        private readonly MonitoringDbContext _context;

        public DiskAnalysisController(MonitoringDbContext context)
        {
            _context = context;
        }

        // GET: DiskAnalysis
        public async Task<IActionResult> Index()
        {
            var logs = await _context.DiskTestLogs
                .OrderByDescending(x => x.TestTime)
                .ToListAsync();
            return View(logs);
        }

        // GET: DiskAnalysis/Details/5
        public async Task<IActionResult> Details(int id)
        {
            var log = await _context.DiskTestLogs.FirstOrDefaultAsync(m => m.Id == id);
            if (log == null) return NotFound();
            return View(log);
        }

        // POST: DiskAnalysis/Import
        [HttpPost]
        public async Task<IActionResult> Import(IFormFile jsonFile)
        {
            if (jsonFile == null || jsonFile.Length == 0)
            {
                TempData["Error"] = "Please select a valid JSON file.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var stream = jsonFile.OpenReadStream();
                var result = await JsonSerializer.DeserializeAsync<DiskTestLog>(stream, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (result != null)
                {
                    // Clean fields if necessary (Reset Id)
                    result.Id = 0;
                    if (result.TestTime == default) result.TestTime = DateTime.UtcNow;

                    _context.DiskTestLogs.Add(result);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Disk analysis result imported successfully!";
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Import failed: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var log = await _context.DiskTestLogs.FindAsync(id);
            if (log != null)
            {
                _context.DiskTestLogs.Remove(log);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
