using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Infrastructure.Sql;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Providers
{
    public class AzureSqlMetricCollector : SqlMetricCollector
    {
        private readonly string _azureConnectionString;

        public AzureSqlMetricCollector(string connectionString, IOptions<MonitoringConfig> options, ILogger<SqlMetricCollector> logger)
            : base(options, logger)
        {
            _azureConnectionString = connectionString;
        }

        public override async Task<InstanceMetricDto> CollectInstanceMetricsAsync(CancellationToken ct = default)
        {
            // First get standard SQL metrics
            var metric = await base.CollectInstanceMetricsAsync(ct);
            metric.ServerName = $"AzureSQL: {metric.ServerName}";

            try
            {
                await using var conn = new SqlConnection(_azureConnectionString);
                await conn.OpenAsync(ct);

                // Azure SQL Specific: Resource Stats (CPU, Data IO, Log Write)
                const string azureSql = @"
                    SELECT TOP 1 
                        rs.avg_cpu_percent, 
                        rs.avg_data_io_percent, 
                        rs.avg_log_write_percent, 
                        rs.max_worker_percent,
                        rs.max_session_percent,
                        slo.service_objective
                    FROM sys.dm_db_resource_stats rs
                    CROSS JOIN sys.database_service_objectives slo
                    ORDER BY rs.end_time DESC";

                await using var cmd = new SqlCommand(azureSql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    metric.CpuUsagePercent = SafeGet<double>(reader, "avg_cpu_percent");
                    metric.DatabaseIoPercent = SafeGet<double>(reader, "avg_data_io_percent");
                    metric.LogWritePercent = SafeGet<double>(reader, "avg_log_write_percent");
                    metric.WorkerUtilizationPercent = SafeGet<double>(reader, "max_worker_percent");
                    metric.SessionUtilizationPercent = SafeGet<double>(reader, "max_session_percent");
                    metric.AzureServiceTier = SafeGet<string>(reader, "service_objective");
                    metric.ProviderType = "AzureSql";
                    
                    _logger.LogInformation("Azure SQL [{Tier}] — CPU: {Cpu}%, IO: {Io}%, Log: {Log}%, Workers: {Worker}%",
                        metric.AzureServiceTier,
                        metric.CpuUsagePercent,
                        metric.DatabaseIoPercent,
                        metric.LogWritePercent,
                        metric.WorkerUtilizationPercent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect Azure SQL specific resource stats");
            }

            // Azure Specific: Wait Stats for Governance
            if (_config.FeatureToggles.EnableWaitStats)
            {
                try
                {
                    await using var conn = new SqlConnection(_azureConnectionString);
                    await conn.OpenAsync(ct);

                    const string waitStatsSql = @"
                        SELECT TOP 5 
                            wait_type, wait_time_ms, waiting_tasks_count, max_wait_time_ms, signal_wait_time_ms
                        FROM sys.dm_db_wait_stats
                        WHERE wait_type IN ('INSTANCE_LOG_RATE_GOVERNOR', 'POOL_LOG_RATE_GOVERNOR', 'HADR_THROTTLE_LOG_RATE_GOVERNOR', 'RESOURCE_GOVERNANCE_IDLE', 'REQUEST_WAIT_FOR_PROFILER')
                          OR (wait_time_ms > 1000 AND wait_type NOT LIKE 'SLEEP%')
                        ORDER BY wait_time_ms DESC";

                    await using var cmd = new SqlCommand(waitStatsSql, conn);
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var wType = SafeGet<string>(reader, "wait_type");
                        // Only add if not already present from base collector
                        if (!metric.TopWaitStats.Exists(w => w.WaitType == wType))
                        {
                            metric.TopWaitStats.Add(new WaitStatDto
                            {
                                WaitType = wType,
                                WaitTimeMs = SafeGet<long>(reader, "wait_time_ms"),
                                WaitCount = SafeGet<long>(reader, "waiting_tasks_count"),
                                MaxWaitTimeMs = SafeGet<long>(reader, "max_wait_time_ms"),
                                SignalWaitTimeMs = SafeGet<long>(reader, "signal_wait_time_ms")
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to collect Azure SQL specific wait stats");
                }
            }

            return metric;
        }
    }
}
