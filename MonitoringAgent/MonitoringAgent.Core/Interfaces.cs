using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Core.Interfaces
{
    /// <summary>
    /// Collects server-level and per-database metrics from SQL Server DMVs.
    /// </summary>
    public interface IMetricCollector
    {
        Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default);
        Task<List<DatabaseMetricDto>> CollectDatabaseMetricsAsync(IEnumerable<string> databases, CancellationToken ct = default);
    }

    /// <summary>
    /// Discovers problematic queries: long-running, CPU-heavy, IO-heavy.
    /// </summary>
    public interface IQueryAnalyzer
    {
        Task<List<QueryMetricDto>> GetLongRunningQueriesAsync(IEnumerable<string> databases, CancellationToken ct = default);
        Task<List<QueryMetricDto>> GetCpuIntensiveQueriesAsync(IEnumerable<string> databases, CancellationToken ct = default);
        Task<List<QueryMetricDto>> GetIoIntensiveQueriesAsync(IEnumerable<string> databases, CancellationToken ct = default);
    }

    /// <summary>
    /// Performs advanced index health analysis across databases.
    /// </summary>
    public interface IIndexAnalyzer
    {
        Task<List<IndexAnalysisDto>> GetFragmentedIndexesAsync(string database, CancellationToken ct = default);
        Task<List<IndexAnalysisDto>> GetMissingIndexesAsync(string database, CancellationToken ct = default);
        Task<List<IndexAnalysisDto>> GetUnusedIndexesAsync(string database, CancellationToken ct = default);
        Task<List<IndexAnalysisDto>> GetDuplicateIndexesAsync(string database, CancellationToken ct = default);
        Task<List<IndexAnalysisDto>> GetHeapTablesAsync(string database, CancellationToken ct = default);
    }

    /// <summary>
    /// Detects blocking chains and deadlock events.
    /// </summary>
    public interface IEventListener
    {
        Task<List<BlockingEventDto>> GetBlockingSessionsAsync(CancellationToken ct = default);
        Task<List<DeadlockEventDto>> GetDeadlockEventsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Sends collected data as JSON to the central Web API.
    /// </summary>
    public interface IApiSender
    {
        Task<bool> SendPayloadAsync(MonitoringPayload payload, CancellationToken ct = default);
        Task<bool> SendAlertsAsync(List<AlertDto> alerts, CancellationToken ct = default);
    }

    /// <summary>
    /// Executes DiskSpd tests and parses results.
    /// </summary>
    public interface IDiskSpdService
    {
        Task<DiskTestResult> RunAsync(DiskTestRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// Producers can enqueue tests, the Worker consumes them.
    /// </summary>
    public interface IDiskTestQueue
    {
        ValueTask EnqueueAsync(DiskTestRequest request, CancellationToken ct = default);
        ValueTask<DiskTestRequest> DequeueAsync(CancellationToken ct = default);
    }
}
