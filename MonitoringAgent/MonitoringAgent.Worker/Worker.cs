using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Application.Services;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using MonitoringAgent.Infrastructure;
using MonitoringAgent.Infrastructure.Disk;

namespace MonitoringAgent.Worker
{
    // ═══════════════════════════════════════════════════════════════
    //  LOOP STATE MACHINE
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Lifecycle states for each monitoring loop.</summary>
    public enum LoopState
    {
        Idle,        // not yet started
        Running,     // healthy, collecting normally
        Degraded,    // collecting but circuit-breaker trip count > 0
        Paused,      // maintenance window or feature toggle = false
        Stopped      // cancelled / shut down
    }

    // ═══════════════════════════════════════════════════════════════
    //  CIRCUIT BREAKER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-loop circuit breaker.
    /// CLOSED   → normal operation.
    /// OPEN     → consecutive errors exceeded threshold; requests rejected for BackoffDuration.
    /// HALF-OPEN → a single probe is allowed through; success → CLOSED, failure → OPEN.
    /// </summary>
    internal sealed class CircuitBreaker
    {
        private enum BreakerState { Closed, Open, HalfOpen }

        private readonly string _name;
        private readonly ILogger _log;
        private readonly int _threshold;
        private readonly TimeSpan _backoff;
        private BreakerState _state = BreakerState.Closed;
        private int _failCount = 0;
        private DateTime _openedAt = DateTime.MinValue;
        private readonly object _lock = new();

        public CircuitBreaker(string name, ILogger log, int threshold = 5, int backoffSeconds = 60)
        {
            _name = name;
            _log = log;
            _threshold = threshold;
            _backoff = TimeSpan.FromSeconds(backoffSeconds);
        }

        /// <summary>Returns true when the loop is allowed to execute.</summary>
        public bool CanExecute()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case BreakerState.Closed: return true;
                    case BreakerState.HalfOpen: return true;
                    case BreakerState.Open:
                        if (DateTime.UtcNow - _openedAt >= _backoff)
                        {
                            _state = BreakerState.HalfOpen;
                            _log.LogWarning("[{Name}] Circuit HALF-OPEN — probe attempt", _name);
                            return true;
                        }
                        return false;
                    default: return false;
                }
            }
        }

        /// <summary>Call after a successful execution cycle.</summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                if (_state == BreakerState.HalfOpen)
                    _log.LogInformation("[{Name}] Circuit CLOSED — probe succeeded", _name);

                _failCount = 0;
                _state = BreakerState.Closed;
            }
        }

        /// <summary>Call after a failed execution cycle.</summary>
        public void RecordFailure()
        {
            lock (_lock)
            {
                _failCount++;
                if (_state == BreakerState.HalfOpen || _failCount >= _threshold)
                {
                    _state = BreakerState.Open;
                    _openedAt = DateTime.UtcNow;
                    _log.LogError("[{Name}] Circuit OPEN — {Count} consecutive failures. " +
                                  "Backing off for {Secs}s", _name, _failCount, _backoff.TotalSeconds);
                }
            }
        }

        public bool IsOpen => _state == BreakerState.Open;
        public bool IsClosed => _state == BreakerState.Closed;
        public int FailCount => _failCount;
    }

    // ═══════════════════════════════════════════════════════════════
    //  LOOP HEALTH SNAPSHOT
    // ═══════════════════════════════════════════════════════════════

    public sealed class LoopHealth
    {
        public string Name { get; init; } = "";
        public LoopState State { get; set; } = LoopState.Idle;
        public DateTime LastSuccessUtc { get; set; } = DateTime.MinValue;
        public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;
        public long TotalCycles { get; set; } = 0;
        public long FailedCycles { get; set; } = 0;
        public long SkippedCycles { get; set; } = 0;
        public TimeSpan CurrentInterval { get; set; }
        public bool CircuitOpen { get; set; } = false;
        public string? LastErrorMessage { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  PAYLOAD THROTTLE  (rolling-window token bucket)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Limits the number of API sends within a rolling time window.
    /// Prevents flooding when many events fire simultaneously.
    /// </summary>
    internal sealed class PayloadThrottle
    {
        private readonly int _maxPerWindow;
        private readonly TimeSpan _window;
        private readonly Queue<DateTime> _timestamps = new();
        private readonly object _lock = new();

        public PayloadThrottle(int maxPerWindow, TimeSpan window)
        {
            _maxPerWindow = maxPerWindow;
            _window = window;
        }

        /// <summary>Returns true if a send is allowed; false if the window is full.</summary>
        public bool TryAcquire()
        {
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow - _window;
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                    _timestamps.Dequeue();

                if (_timestamps.Count >= _maxPerWindow)
                    return false;

                _timestamps.Enqueue(DateTime.UtcNow);
                return true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  MAINTENANCE WINDOW CHECKER
    // ═══════════════════════════════════════════════════════════════

    internal static class MaintenanceWindow
    {
        /// <summary>
        /// Returns true if current UTC time falls inside any configured maintenance window.
        /// Format: "HH:mm-HH:mm"  e.g. ["02:00-04:00", "23:00-01:00"] (midnight-wrap supported).
        /// </summary>
        public static bool IsActive(IEnumerable<string> windows)
        {
            var nowUtc = DateTime.UtcNow.TimeOfDay;

            foreach (var w in windows)
            {
                var parts = w.Split('-');
                if (parts.Length != 2) continue;
                if (!TimeSpan.TryParse(parts[0].Trim(), out var start)) continue;
                if (!TimeSpan.TryParse(parts[1].Trim(), out var end)) continue;

                bool inWindow = start <= end
                    ? nowUtc >= start && nowUtc < end
                    : nowUtc >= start || nowUtc < end;   // wraps midnight

                if (inWindow) return true;
            }
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADAPTIVE INTERVAL
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Dynamically adjusts the collection interval:
    ///   Failure → doubles up to (maxFactor × base)
    ///   Success → divides by recoveryStep, floors at base
    /// </summary>
    internal sealed class AdaptiveInterval
    {
        private readonly TimeSpan _base;
        private readonly double _maxFactor;
        private readonly double _backoffStep;
        private readonly double _recoveryStep;
        private double _currentSecs;

        public AdaptiveInterval(TimeSpan baseInterval,
                                double maxFactor = 4.0,
                                double backoffStep = 2.0,
                                double recoveryStep = 1.25)
        {
            _base = baseInterval;
            _maxFactor = maxFactor;
            _backoffStep = backoffStep;
            _recoveryStep = recoveryStep;
            _currentSecs = baseInterval.TotalSeconds;
        }

        public TimeSpan Current => TimeSpan.FromSeconds(_currentSecs);

        public void RecordSuccess()
            => _currentSecs = Math.Max(_base.TotalSeconds, _currentSecs / _recoveryStep);

        public void RecordFailure()
            => _currentSecs = Math.Min(_base.TotalSeconds * _maxFactor, _currentSecs * _backoffStep);
    }

    // ═══════════════════════════════════════════════════════════════
    //  WORKER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Main background worker. Runs independent loops for:
    ///   1. Metrics + Queries  (fast, frequent)
    ///   2. Index Analysis     (slow, infrequent — runs on separate timer)
    ///   3. Event Detection    (very frequent — blocking &amp; deadlocks)
    ///
    /// Advanced controls (all non-breaking — safe defaults if not configured):
    ///   • Circuit Breaker      — opens after N consecutive failures; auto-recovers
    ///   • Loop State Machine   — Idle / Running / Degraded / Paused / Stopped
    ///   • Health Snapshots     — per-loop counters &amp; timestamps
    ///   • Adaptive Intervals   — backs off on failure, recovers on success
    ///   • Maintenance Windows  — silently pauses during configured UTC hours
    ///   • Payload Throttle     — caps API sends per rolling window
    ///   • Graceful Drain       — in-flight work gets GracePeriod extra time on shutdown
    /// </summary>
    public class Worker : BackgroundService
    {
        // ── Dependencies ──────────────────────────────────────────
        private readonly ILogger<Worker> _logger;
        private readonly MetricService _metricService;
        private readonly QueryAnalysisManager _queryManager;
        private readonly IndexAnalysisManager _indexManager;
        private readonly EventMonitorManager _eventManager;
        private readonly AlertManager _alertManager;
        private readonly ThresholdEvaluationService _thresholdService;
        private readonly IApiSender _apiSender;
        private readonly IDiskTestQueue _diskTestQueue;
        private readonly IDiskSpdService _diskSpdService;
        private readonly MonitoringConfig _config;
        private readonly IDatabaseProviderFactory _providerFactory;

        // ── Advanced controls ─────────────────────────────────────
        private readonly CircuitBreaker _metricsBreaker;
        private readonly CircuitBreaker _indexBreaker;
        private readonly CircuitBreaker _eventsBreaker;

        private readonly AdaptiveInterval _metricsInterval;
        private readonly AdaptiveInterval _indexInterval;
        private readonly AdaptiveInterval _eventsInterval;

        private readonly PayloadThrottle _throttle;

        // Thread-safe health map — keyed by loop name
        private readonly ConcurrentDictionary<string, LoopHealth> _health = new();

        // Extra time given to in-flight operations after stoppingToken fires
        private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(15);

        // ─────────────────────────────────────────────────────────
        public Worker(
            ILogger<Worker> logger,
            MetricService metricService,
            QueryAnalysisManager queryManager,
            IndexAnalysisManager indexManager,
            EventMonitorManager eventManager,
            AlertManager alertManager,
            ThresholdEvaluationService thresholdService,
            IApiSender apiSender,
            IDiskTestQueue diskTestQueue,
            IDiskSpdService diskSpdService,
            IServiceProvider serviceProvider,
            IOptions<MonitoringConfig> options)
        {
            _logger = logger;
            _metricService = metricService;
            _queryManager = queryManager;
            _indexManager = indexManager;
            _eventManager = eventManager;
            _alertManager = alertManager;
            _thresholdService = thresholdService;
            _apiSender = apiSender;
            _diskTestQueue = diskTestQueue;
            _diskSpdService = diskSpdService;
            _config = options.Value;
            _providerFactory = serviceProvider.GetRequiredService<IDatabaseProviderFactory>();

            var adv = _config.AdvancedControls;

            // ── Circuit breakers ──────────────────────────────────
            _metricsBreaker = new CircuitBreaker("MetricsLoop", logger,
                adv.CircuitBreakerThreshold, adv.CircuitBreakerBackoffSeconds);
            _indexBreaker = new CircuitBreaker("IndexLoop", logger,
                adv.CircuitBreakerThreshold, adv.CircuitBreakerBackoffSeconds * 5);
            _eventsBreaker = new CircuitBreaker("EventsLoop", logger,
                adv.CircuitBreakerThreshold, adv.CircuitBreakerBackoffSeconds);

            // ── Adaptive intervals ────────────────────────────────
            _metricsInterval = new AdaptiveInterval(
                TimeSpan.FromSeconds(_config.CollectionIntervals.MetricsSeconds));
            _indexInterval = new AdaptiveInterval(
                TimeSpan.FromMinutes(_config.CollectionIntervals.IndexesMinutes), maxFactor: 2.0);
            _eventsInterval = new AdaptiveInterval(
                TimeSpan.FromSeconds(_config.CollectionIntervals.EventsSeconds));

            // ── Payload throttle ──────────────────────────────────
            _throttle = new PayloadThrottle(
                adv.ThrottleMaxSendsPerWindow,
                TimeSpan.FromSeconds(adv.ThrottleWindowSeconds));

            // ── Initial health entries ────────────────────────────
            foreach (var name in new[] { "MetricsLoop", "IndexLoop", "EventsLoop", "DiskSpdLoop" })
                _health[name] = new LoopHealth { Name = name };
        }

        // ─────────────────────────────────────────────────────────
        //  ENTRY POINT
        // ─────────────────────────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var adv = _config.AdvancedControls;

            _logger.LogInformation("╔══════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║   MonitoringAgent.Worker  —  Starting up                 ║");
            _logger.LogInformation("╠══════════════════════════════════════════════════════════╣");
            _logger.LogInformation("║ Metrics interval   : {Secs}s", _config.CollectionIntervals.MetricsSeconds);
            _logger.LogInformation("║ Query interval     : {Secs}s", _config.CollectionIntervals.QueriesSeconds);
            _logger.LogInformation("║ Index interval     : {Min}min", _config.CollectionIntervals.IndexesMinutes);
            _logger.LogInformation("║ Events interval    : {Secs}s", _config.CollectionIntervals.EventsSeconds);
            _logger.LogInformation("║ Circuit threshold  : {N} failures", adv.CircuitBreakerThreshold);
            _logger.LogInformation("║ Payload throttle   : {N} sends / {W}s window",
                adv.ThrottleMaxSendsPerWindow, adv.ThrottleWindowSeconds);
            _logger.LogInformation("║ Maintenance windows: {W}",
                adv.MaintenanceWindowsUtc.Length > 0
                    ? string.Join(", ", adv.MaintenanceWindowsUtc)
                    : "none");
            _logger.LogInformation("╚══════════════════════════════════════════════════════════╝");

            // Graceful drain: give in-flight work GracePeriod extra time after host signals stop
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Shutdown signalled — {Grace}s grace period started.",
                    GracePeriod.TotalSeconds);
                linkedCts.CancelAfter(GracePeriod);
            });

            var databases = ResolveDatabases();
            _logger.LogInformation("Monitoring {Count} database(s): {Dbs}",
                databases.Count, string.Join(", ", databases));

            // ── Baseline Disk Test ─────────────────────────────
            if (_config.FeatureToggles.EnableDiskSpd)
            {
                _logger.LogInformation("Enqueuing baseline disk test on startup.");
                try
                {
                    var baselineRequest = new DiskTestRequest
                    {
                        ServerName = Environment.MachineName,
                        Path = _config.DiskSpd.TestPath,
                        FileSizeGB = _config.DiskSpd.FileSizeGB,
                        DurationSec = _config.DiskSpd.DurationSec,
                        WritePercent = _config.DiskSpd.WritePercent,
                        BlockSizeKB = _config.DiskSpd.BlockSizeKB,
                        Threads = _config.DiskSpd.Threads,
                        QueueDepth = _config.DiskSpd.QueueDepth
                    };
                    await _diskTestQueue.EnqueueAsync(baselineRequest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to enqueue baseline disk test: {Msg}", ex.Message);
                }
            }

            await Task.WhenAll(
                RunMetricsLoopAsync(databases, linkedCts.Token),
                RunIndexLoopAsync(databases, linkedCts.Token),
                RunEventsLoopAsync(linkedCts.Token),
                RunDiskTestLoopAsync(linkedCts.Token),
                RunHealthReporterAsync(linkedCts.Token)
            );

            _logger.LogInformation("MonitoringAgent.Worker stopped gracefully.");
        }

        // ─────────────────────────────────────────────────────────
        //  METRICS + QUERIES LOOP
        // ─────────────────────────────────────────────────────────

        private async Task RunMetricsLoopAsync(List<string> databases, CancellationToken ct)
        {
            const string LoopName = "MetricsLoop";
            var health = _health[LoopName];
            health.State = LoopState.Running;
            health.CurrentInterval = _metricsInterval.Current;

            _logger.LogInformation("[{Loop}] Started", LoopName);

            while (!ct.IsCancellationRequested)
            {
                // ── Maintenance window ────────────────────────────
                if (IsInMaintenanceWindow())
                {
                    health.State = LoopState.Paused;
                    health.SkippedCycles++;
                    _logger.LogInformation("[{Loop}] Maintenance window active — sleeping 60s", LoopName);
                    await SafeDelayAsync(TimeSpan.FromSeconds(60), ct);
                    continue;
                }

                // ── Circuit breaker ───────────────────────────────
                if (!_metricsBreaker.CanExecute())
                {
                    health.State = LoopState.Degraded;
                    health.CircuitOpen = true;
                    health.SkippedCycles++;
                    _logger.LogWarning("[{Loop}] Circuit OPEN — skipping cycle", LoopName);
                    await SafeDelayAsync(_metricsInterval.Current, ct);
                    continue;
                }

                health.State = _metricsBreaker.IsClosed ? LoopState.Running : LoopState.Degraded;
                health.CircuitOpen = false;
                health.LastAttemptUtc = DateTime.UtcNow;
                health.TotalCycles++;

                var cycleStart = DateTime.UtcNow;
                try
                {
                    // 1. Primary Connection Metrics (Current logic)
                    if (_config.FeatureToggles.EnableMetrics)
                    {
                        var payload = new MonitoringPayload();
                        payload.InstanceMetrics = await _metricService.CollectInstanceAsync(ct);
                        payload.DatabaseMetrics = await _metricService.CollectDatabasesAsync(databases, ct);

                        if (_config.FeatureToggles.EnableQueries)
                            payload.Queries = await _queryManager.AnalyzeAllAsync(databases, ct);

                        if (_config.FeatureToggles.EnableAlerts)
                        {
                            payload.Alerts = _thresholdService.EvaluateAll(
                                payload.InstanceMetrics, payload.DatabaseMetrics,
                                payload.Queries, new List<IndexAnalysisDto>(),
                                new List<BlockingEventDto>(), new List<DeadlockEventDto>());

                            foreach (var alert in payload.Alerts) { alert.ProviderType = "SqlServer"; }
                            foreach (var q in payload.Queries) { q.ProviderType = "SqlServer"; }
                            foreach (var dbm in payload.DatabaseMetrics) { dbm.ProviderType = "SqlServer"; }
                            if (payload.InstanceMetrics != null) payload.InstanceMetrics.ProviderType = "SqlServer";

                            var highAlerts = payload.Alerts.Where(a => a.Severity == "High").ToList();
                            if (highAlerts.Count > 0)
                                await ThrottledSendAlertsAsync(highAlerts, LoopName, ct);
                        }

                        // 4. Plan-based issues (Implicit conversions, Key lookups) — SQL Server only
                        if (_config.FeatureToggles.EnableQueries)
                        {
                            try
                            {
                                var planIssues = await _queryManager.GetPlanIssuesAsync(ct);
                                foreach (var pi in planIssues)
                                {
                                    if (!payload.TopQueries.Any(q => q.QueryHash == pi.QueryHash))
                                        payload.TopQueries.Add(pi);
                                }
                            }
                            catch { }
                        }

                        await ThrottledSendPayloadAsync(payload, LoopName, ct);
                    }

                    // 2. Additional Target Servers (Postgres, MySQL, Oracle, Azure)
                    foreach (var target in _config.TargetServers.Where(t => t.IsEnabled))
                    {
                        // Check provider-specific feature toggle
                        bool isEnabled = target.ProviderType.ToLower() switch
                        {
                            "postgres" => _config.FeatureToggles.EnablePostgres,
                            "mysql"    => _config.FeatureToggles.EnableMySql,
                            "oracle"   => _config.FeatureToggles.EnableOracle,
                            "azuresql" => _config.FeatureToggles.EnableAzureSql,
                            _          => true
                        };

                        if (!isEnabled) continue;

                        _logger.LogInformation("[{Loop}] Collecting from {Alias} ({Provider})", LoopName, target.Alias, target.ProviderType);
                        
                        var collector = _providerFactory.GetCollector(target);
                        var targetPayload = new MonitoringPayload();
                        
                        // 2. Instance Metrics
                        targetPayload.InstanceMetrics = await collector.CollectInstanceMetricsAsync(ct);
                        if (targetPayload.InstanceMetrics != null)
                        {
                            targetPayload.InstanceMetrics.ServerName = target.Alias;
                            targetPayload.InstanceMetrics.ProviderType = target.ProviderType;
                        }
                        
                        var targetDbs = ResolveTargetDatabases(target);
                        targetPayload.DatabaseMetrics = await collector.CollectDatabaseMetricsAsync(targetDbs, ct);
                        foreach (var dbm in targetPayload.DatabaseMetrics)
                        {
                             dbm.ProviderType = target.ProviderType;
                        }

                        // 3. Query Analysis for Target
                        if (_config.FeatureToggles.EnableQueries)
                        {
                             try
                             {
                                 var analyzer = _providerFactory.GetQueryAnalyzer(target);
                                 targetPayload.TopQueries = await analyzer.GetLongRunningQueriesAsync(targetDbs, ct);
                                 var cpuQueries = await analyzer.GetCpuIntensiveQueriesAsync(targetDbs, ct);
                                 
                                 // Union and deduplicate
                                 foreach (var q in cpuQueries)
                                 {
                                     if (!targetPayload.TopQueries.Any(x => x.QueryHash == q.QueryHash))
                                         targetPayload.TopQueries.Add(q);
                                 }

                                 foreach (var q in targetPayload.TopQueries) q.ProviderType = target.ProviderType;
                             }
                             catch (Exception ex)
                             {
                                 _logger.LogWarning("Failed to analyze queries for {Alias}: {Msg}", target.Alias, ex.Message);
                             }
                        }
                        
                        if (_config.FeatureToggles.EnableAlerts)
                        {
                            targetPayload.Alerts = _thresholdService.EvaluateAll(
                                targetPayload.InstanceMetrics, targetPayload.DatabaseMetrics,
                                targetPayload.TopQueries, new(), new(), new());
                            
                            foreach (var alert in targetPayload.Alerts) { alert.ProviderType = target.ProviderType; }
                            foreach (var dbm in targetPayload.DatabaseMetrics) { dbm.ProviderType = target.ProviderType; }
                        }

                        await ThrottledSendPayloadAsync(targetPayload, LoopName, ct);
                    }

                    _metricsBreaker.RecordSuccess();
                    _metricsInterval.RecordSuccess();
                    health.LastSuccessUtc = DateTime.UtcNow;
                    health.LastErrorMessage = null;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _metricsBreaker.RecordFailure();
                    _metricsInterval.RecordFailure();
                    health.FailedCycles++;
                    health.LastErrorMessage = ex.Message;
                    _logger.LogError(ex, "[{Loop}] Unhandled error — next attempt in {Interval}",
                        LoopName, _metricsInterval.Current);
                }

                health.CurrentInterval = _metricsInterval.Current;

                // Subtract elapsed so the interval stays wall-clock consistent
                var elapsed = DateTime.UtcNow - cycleStart;
                var delay = _metricsInterval.Current - elapsed;
                if (delay > TimeSpan.Zero)
                    await SafeDelayAsync(delay, ct);
            }

            health.State = LoopState.Stopped;
            _logger.LogInformation("[{Loop}] Stopped", LoopName);
        }

        // ─────────────────────────────────────────────────────────
        //  INDEX ANALYSIS LOOP  (infrequent — default once per day)
        // ─────────────────────────────────────────────────────────

        private async Task RunIndexLoopAsync(List<string> databases, CancellationToken ct)
        {
            const string LoopName = "IndexLoop";
            var health = _health[LoopName];
            health.State = LoopState.Running;
            health.CurrentInterval = _indexInterval.Current;

            _logger.LogInformation("[{Loop}] Started — first run in {Min}min",
                LoopName, _config.CollectionIntervals.IndexesMinutes);

            // Intentional first-run delay — index analysis is expensive
            await SafeDelayAsync(TimeSpan.FromMinutes(_config.CollectionIntervals.IndexesMinutes), ct);

            while (!ct.IsCancellationRequested)
            {
                // ── Feature toggle ────────────────────────────────
                if (!_config.FeatureToggles.EnableIndexAnalysis)
                {
                    health.State = LoopState.Paused;
                    health.SkippedCycles++;
                    await SafeDelayAsync(TimeSpan.FromMinutes(10), ct);
                    continue;
                }

                // ── Maintenance window ────────────────────────────
                if (IsInMaintenanceWindow())
                {
                    health.State = LoopState.Paused;
                    health.SkippedCycles++;
                    _logger.LogInformation("[{Loop}] Maintenance window active — sleeping 5min", LoopName);
                    await SafeDelayAsync(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                // ── Circuit breaker ───────────────────────────────
                if (!_indexBreaker.CanExecute())
                {
                    health.State = LoopState.Degraded;
                    health.CircuitOpen = true;
                    health.SkippedCycles++;
                    _logger.LogWarning("[{Loop}] Circuit OPEN — skipping cycle", LoopName);
                    await SafeDelayAsync(_indexInterval.Current, ct);
                    continue;
                }

                health.State = _indexBreaker.IsClosed ? LoopState.Running : LoopState.Degraded;
                health.CircuitOpen = false;
                health.LastAttemptUtc = DateTime.UtcNow;
                health.TotalCycles++;

                try
                {
                    _logger.LogInformation("[{Loop}] Starting full index analysis...", LoopName);
                    var indexes = await _indexManager.AnalyzeAllAsync(databases, ct);

                    // ── NEW: Stale Stats Check ──
                    foreach (var db in databases)
                    {
                        try
                        {
                            var staleStats = await _indexManager.GetStaleStatisticsAsync(db, ct);
                            if (staleStats != null) indexes.AddRange(staleStats);
                        }
                        catch { }
                    }

                    var payload = new MonitoringPayload { IndexAnalysis = indexes };

                    if (_config.FeatureToggles.EnableAlerts)
                    {
                        payload.Alerts = _thresholdService.EvaluateAll(
                            null,
                            new List<DatabaseMetricDto>(),
                            new List<QueryMetricDto>(),
                            indexes,
                            new List<BlockingEventDto>(),
                            new List<DeadlockEventDto>());
                            
                        foreach (var alert in payload.Alerts) { alert.ProviderType = "SqlServer"; }
                        foreach (var idx in indexes) { idx.ProviderType = "SqlServer"; }
                    }

                    await ThrottledSendPayloadAsync(payload, LoopName, ct);

                    _logger.LogInformation("[{Loop}] Complete — {Count} issue(s) found",
                        LoopName, indexes.Count);

                    // 2. Extra Targets Index Analysis
                    foreach (var target in _config.TargetServers.Where(t => t.IsEnabled))
                    {
                        try
                        {
                            _logger.LogInformation("[{Loop}] Analyzing indexes for {Alias} ({Provider})...", LoopName, target.Alias, target.ProviderType);
                            var analyzer = _providerFactory.GetIndexAnalyzer(target);
                            var targetDbs = ResolveTargetDatabases(target);
                            
                            var allIssues = new List<IndexAnalysisDto>();
                            foreach (var db in targetDbs)
                            {
                                var frag = await analyzer.GetFragmentedIndexesAsync(db, ct);
                                var miss = await analyzer.GetMissingIndexesAsync(db, ct);
                                var unused = await analyzer.GetUnusedIndexesAsync(db, ct);
                                
                                allIssues.AddRange(frag);
                                allIssues.AddRange(miss);
                                allIssues.AddRange(unused);
                            }

                            if (allIssues.Count > 0)
                            {
                                foreach (var issue in allIssues) { issue.ProviderType = target.ProviderType; }
                                
                                var targetPayload = new MonitoringPayload { IndexAnalysis = allIssues };
                                if (_config.FeatureToggles.EnableAlerts)
                                {
                                    targetPayload.Alerts = _thresholdService.EvaluateAll(
                                        null, new(), new(), allIssues, new(), new());
                                    foreach (var alert in targetPayload.Alerts) alert.ProviderType = target.ProviderType;
                                }

                                await ThrottledSendPayloadAsync(targetPayload, LoopName, ct);
                                _logger.LogInformation("[{Loop}] {Alias} — {Count} issue(s) found", LoopName, target.Alias, allIssues.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Index analysis failed for {Alias}: {Msg}", target.Alias, ex.Message);
                        }
                    }

                    _indexBreaker.RecordSuccess();
                    _indexInterval.RecordSuccess();
                    health.LastSuccessUtc = DateTime.UtcNow;
                    health.LastErrorMessage = null;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _indexBreaker.RecordFailure();
                    _indexInterval.RecordFailure();
                    health.FailedCycles++;
                    health.LastErrorMessage = ex.Message;
                    _logger.LogError(ex, "[{Loop}] Unhandled error", LoopName);
                }

                health.CurrentInterval = _indexInterval.Current;
                await SafeDelayAsync(_indexInterval.Current, ct);
            }

            health.State = LoopState.Stopped;
            _logger.LogInformation("[{Loop}] Stopped", LoopName);
        }

        // ─────────────────────────────────────────────────────────
        //  EVENTS LOOP  — blocking & deadlocks (very frequent)
        // ─────────────────────────────────────────────────────────

        private async Task RunEventsLoopAsync(CancellationToken ct)
        {
            const string LoopName = "EventsLoop";
            var health = _health[LoopName];
            health.State = LoopState.Running;
            health.CurrentInterval = _eventsInterval.Current;

            _logger.LogInformation("[{Loop}] Started", LoopName);

            while (!ct.IsCancellationRequested)
            {
                // ── Maintenance window ────────────────────────────
                if (IsInMaintenanceWindow())
                {
                    health.State = LoopState.Paused;
                    health.SkippedCycles++;
                    await SafeDelayAsync(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // ── Circuit breaker ───────────────────────────────
                if (!_eventsBreaker.CanExecute())
                {
                    health.State = LoopState.Degraded;
                    health.CircuitOpen = true;
                    health.SkippedCycles++;
                    await SafeDelayAsync(_eventsInterval.Current, ct);
                    continue;
                }

                health.State = _eventsBreaker.IsClosed ? LoopState.Running : LoopState.Degraded;
                health.CircuitOpen = false;
                health.LastAttemptUtc = DateTime.UtcNow;
                health.TotalCycles++;

                try
                {
                    bool detectBlocking = _config.FeatureToggles.EnableBlockingDetection;
                    bool detectDeadlocks = _config.FeatureToggles.EnableDeadlockDetection;

                    if (detectBlocking || detectDeadlocks)
                    {
                        var (blocking, deadlocks) = await _eventManager.DetectEventsAsync(ct);

                        if (blocking.Count > 0 || deadlocks.Count > 0)
                        {
                            var payload = new MonitoringPayload
                            {
                                BlockingEvents = detectBlocking ? blocking : new(),
                                DeadlockEvents = detectDeadlocks ? deadlocks : new()
                            };

                            if (_config.FeatureToggles.EnableAlerts)
                            {
                                payload.Alerts = _thresholdService.EvaluateAll(
                                    null,
                                    new List<DatabaseMetricDto>(),
                                    new List<QueryMetricDto>(),
                                    new List<IndexAnalysisDto>(),
                                    payload.BlockingEvents,
                                    payload.DeadlockEvents);

                                foreach (var alert in payload.Alerts) { alert.ProviderType = "SqlServer"; }
                                foreach (var b in payload.BlockingEvents) { b.ProviderType = "SqlServer"; }
                                foreach (var d in payload.DeadlockEvents) { d.ProviderType = "SqlServer"; }

                                var urgent = payload.Alerts.Where(a => a.Severity == "High").ToList();
                                if (urgent.Count > 0)
                                    await ThrottledSendAlertsAsync(urgent, LoopName, ct);
                            }

                            await ThrottledSendPayloadAsync(payload, LoopName, ct);
                        }
                    }

                    _eventsBreaker.RecordSuccess();
                    _eventsInterval.RecordSuccess();
                    health.LastSuccessUtc = DateTime.UtcNow;
                    health.LastErrorMessage = null;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _eventsBreaker.RecordFailure();
                    _eventsInterval.RecordFailure();
                    health.FailedCycles++;
                    health.LastErrorMessage = ex.Message;
                    _logger.LogError(ex, "[{Loop}] Unhandled error", LoopName);
                }

                health.CurrentInterval = _eventsInterval.Current;
                await SafeDelayAsync(_eventsInterval.Current, ct);
            }

            health.State = LoopState.Stopped;
            _logger.LogInformation("[{Loop}] Stopped", LoopName);
        }

        // ─────────────────────────────────────────────────────────
        //  HEALTH REPORTER LOOP
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Periodically emits a structured health summary for all loops.
        /// Purely observational — does not affect any collection logic.
        /// </summary>
        private async Task RunHealthReporterAsync(CancellationToken ct)
        {
            var interval = TimeSpan.FromMinutes(_config.AdvancedControls.HealthReportIntervalMinutes);

            while (!ct.IsCancellationRequested)
            {
                await SafeDelayAsync(interval, ct);

                _logger.LogInformation("──── Health Report ──────────────────────────────────");
                foreach (var h in _health.Values)
                {
                    var successAgo = h.LastSuccessUtc == DateTime.MinValue
                        ? "never"
                        : $"{(DateTime.UtcNow - h.LastSuccessUtc).TotalSeconds:F0}s ago";

                    _logger.LogInformation(
                        "[{Loop}] State={State} | Total={Total} OK={OK} Failed={Failed} Skipped={Skip} | " +
                        "Interval={Interval} | CircuitOpen={CB} | LastSuccess={LastOk} | Error={Err}",
                        h.Name,
                        h.State,
                        h.TotalCycles,
                        h.TotalCycles - h.FailedCycles,
                        h.FailedCycles,
                        h.SkippedCycles,
                        h.CurrentInterval,
                        h.CircuitOpen,
                        successAgo,
                        h.LastErrorMessage ?? "—");
                }
                _logger.LogInformation("─────────────────────────────────────────────────────");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  DISKSPD TEST LOOP (on-demand via queue)
        // ─────────────────────────────────────────────────────────
        private async Task RunDiskTestLoopAsync(CancellationToken ct)
        {
            const string LoopName = "DiskSpdLoop";
            var health = _health[LoopName];
            health.State = LoopState.Running;
            
            _logger.LogInformation("[{Loop}] Started — waiting for jobs", LoopName);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for a job to arrive in the queue
                    var request = await _diskTestQueue.DequeueAsync(ct);

                    // ── Feature toggle check ──────────────────────────
                    if (!_config.FeatureToggles.EnableDiskSpd)
                    {
                        _logger.LogInformation("[{Loop}] Feature disabled — skipping job {Id}", 
                            LoopName, request.RequestId);
                        continue;
                    }
                    
                    health.LastAttemptUtc = DateTime.UtcNow;
                    health.TotalCycles++;

                    _logger.LogInformation("[{Loop}] Picked up job {Id} for path {Path}", 
                        LoopName, request.RequestId, request.Path);

                    var result = await _diskSpdService.RunAsync(request, ct);

                    if (result.Success)
                    {
                        health.LastSuccessUtc = DateTime.UtcNow;
                        health.LastErrorMessage = null;
                        
                        _logger.LogInformation("[{Loop}] Job {Id} finished: {IOPS} IOPS, {MBps} MB/s", 
                            LoopName, request.RequestId, result.IOPS, result.MBps);

                        // ── Save Detailed Result Locally ──────────────
                        try
                        {
                            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiskSpdLogs");
                            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

                            string fileName = $"DiskTest_{DateTime.Now:yyyyMMdd_HHmmss}_{request.RequestId}.json";
                            string filePath = Path.Combine(logDir, fileName);
                            
                            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                            await File.WriteAllTextAsync(filePath, json, ct);
                            
                            _logger.LogInformation("[{Loop}] Detailed result saved to: {Path}", LoopName, filePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[{Loop}] Failed to save local result file: {Msg}", LoopName, ex.Message);
                        }

                        // ── Send Summary to API ────────────────────────
                        if (_config.FeatureToggles.EnableApiSending)
                        {
                            // We only send the summary metrics to keep payload light
                            var summaryResult = new DiskTestResult
                            {
                                RequestId = result.RequestId,
                                ServerName = Environment.MachineName,
                                TestPath = result.TestPath,
                                IOPS = result.IOPS,
                                MBps = result.MBps,
                                LatencyMs = result.LatencyMs,
                                CpuUsagePercent = result.CpuUsagePercent,
                                Success = true,
                                CompletedAt = result.CompletedAt
                            };

                            var payload = new MonitoringPayload 
                            { 
                                DiskTestResults = new List<DiskTestResult> { summaryResult } 
                            };
                            await ThrottledSendPayloadAsync(payload, LoopName, ct);
                        }
                    }
                    else
                    {
                        health.FailedCycles++;
                        health.LastErrorMessage = result.ErrorMessage;
                        _logger.LogWarning("[{Loop}] Job {Id} failed: {Err}", 
                            LoopName, request.RequestId, result.ErrorMessage);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    health.FailedCycles++;
                    health.LastErrorMessage = ex.Message;
                    _logger.LogError(ex, "[{Loop}] Unexpected error processing queue", LoopName);
                    await SafeDelayAsync(TimeSpan.FromSeconds(5), ct);
                }
            }

            health.State = LoopState.Stopped;
            _logger.LogInformation("[{Loop}] Stopped", LoopName);
        }

        // ─────────────────────────────────────────────────────────
        //  THROTTLED SEND HELPERS
        // ─────────────────────────────────────────────────────────

        private async Task ThrottledSendPayloadAsync(
            MonitoringPayload payload, string loopName, CancellationToken ct)
        {
            if (!_throttle.TryAcquire())
            {
                _logger.LogWarning("[{Loop}] Payload throttled — dropping send.", loopName);
                return;
            }
            await _apiSender.SendPayloadAsync(payload, ct);
        }

        private async Task ThrottledSendAlertsAsync(
            List<AlertDto> alerts, string loopName, CancellationToken ct)
        {
            if (!_throttle.TryAcquire())
            {
                _logger.LogWarning("[{Loop}] Alert send throttled — dropping.", loopName);
                return;
            }
            await _apiSender.SendAlertsAsync(alerts, ct);
        }

        // ─────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────

        private bool IsInMaintenanceWindow()
        {
            var windows = _config.AdvancedControls.MaintenanceWindowsUtc;
            return windows.Length > 0 && MaintenanceWindow.IsActive(windows);
        }

        private List<string> ResolveDatabases()
        {
            if (_config.IncludeDBs != null && _config.IncludeDBs.Length > 0)
            {
                return _config.IncludeDBs
                    .Where(db => !_config.ExcludeDBs.Contains(db, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            return new List<string>();
        }

        private string[] ResolveTargetDatabases(DatabaseTarget target)
        {
            if (target.IncludeDBs != null && target.IncludeDBs.Length > 0)
                return target.IncludeDBs;

            // For SQL Server / Azure SQL, try to extract from connection string
            if (target.ProviderType.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) || 
                target.ProviderType.Equals("AzureSql", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(target.ConnectionString);
                    if (!string.IsNullOrEmpty(builder.InitialCatalog))
                    {
                        return new[] { builder.InitialCatalog };
                    }
                }
                catch { /* fallback */ }
            }
            else if (target.ProviderType.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            {
                // Host=localhost;Database=mydb;...
                var parts = target.ConnectionString.Split(';');
                var dbPart = parts.FirstOrDefault(p => p.Trim().StartsWith("Database=", StringComparison.OrdinalIgnoreCase));
                if (dbPart != null) return [dbPart.Split('=')[1].Trim()];
            }
            else if (target.ProviderType.Equals("MySql", StringComparison.OrdinalIgnoreCase))
            {
                // Server=...;Database=...;
                var parts = target.ConnectionString.Split(';');
                var dbPart = parts.FirstOrDefault(p => p.Trim().StartsWith("Database=", StringComparison.OrdinalIgnoreCase));
                if (dbPart != null) return [dbPart.Split('=')[1].Trim()];
            }
            else if (target.ProviderType.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                // Data Source=...;
                var parts = target.ConnectionString.Split(';');
                var dsPart = parts.FirstOrDefault(p => p.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase));
                if (dsPart != null) return [dsPart.Split('=')[1].Trim()];
            }

            return new[] { "default" };
        }

        private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); }
            catch (TaskCanceledException) { /* expected on shutdown */ }
        }

        // ─────────────────────────────────────────────────────────
        //  PUBLIC HEALTH API
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a point-in-time snapshot of all loop health states.
        /// Thread-safe; suitable for health-check endpoints.
        /// </summary>
        public IReadOnlyDictionary<string, LoopHealth> GetHealthSnapshot() => _health;
    }
}