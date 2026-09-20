using System.Collections.Generic;
using MonitoringAgent.Web.Data;

namespace MonitoringAgent.Web.Models
{
    public class AnalysisViewModel
    {
        public List<InstanceMetricRecord> InstanceMetrics { get; set; } = new();
        public List<DatabaseMetricRecord> DatabaseMetrics { get; set; } = new();
    }
}
