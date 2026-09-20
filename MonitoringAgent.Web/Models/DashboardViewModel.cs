using System.Collections.Generic;
using MonitoringAgent.Web.Data;

namespace MonitoringAgent.Web.Models
{
    public class DashboardViewModel
    {
        public int TotalAlerts { get; set; }
        public int HighSeverityAlerts { get; set; }
        public int ActiveBlockingCount { get; set; }
        public int RecentDeadlocksCount { get; set; }
        public int MissingIndexesCount { get; set; }
        public double AverageImpactScore { get; set; }
        public List<InstanceMetricRecord> LastMetrics { get; set; } = new();
        public List<GroupedAlertDto> RecentAlerts { get; set; } = new();
        public dynamic? PerformanceTrend { get; set; }
        public string SelectedDbType { get; set; } = "All";
    }
}
