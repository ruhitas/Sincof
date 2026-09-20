using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Sql
{
    // ═══════════════════════════════════════════════════════════════
    // QUERY ANALYZER SERVICE — discovers problematic queries from DMVs
    // ═══════════════════════════════════════════════════════════════
    public class QueryAnalyzerService : SqlServiceBase, IQueryAnalyzer
    {
        public QueryAnalyzerService(IOptions<MonitoringConfig> options, ILogger<QueryAnalyzerService> logger)
            : base(options, logger) { }

        /// <summary>
        /// Long-running queries: sorted by total_elapsed_time DESC.
        /// Uses sys.dm_exec_query_stats joined with sys.dm_exec_sql_text to get the actual SQL text.
        /// total_elapsed_time = wall-clock time across all executions (microseconds).
        /// We filter to user databases only (dbid > 4) to exclude system queries.
        /// </summary>
        public async Task<List<QueryMetricDto>> GetLongRunningQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT TOP 25
                    DB_NAME(t.dbid)       AS DatabaseName,
                    CONVERT(VARCHAR(64), qs.query_hash, 1) AS QueryHash,
                    SUBSTRING(t.text, 
                        (qs.statement_start_offset / 2) + 1,
                        ((CASE qs.statement_end_offset 
                            WHEN -1 THEN DATALENGTH(t.text) 
                            ELSE qs.statement_end_offset 
                          END - qs.statement_start_offset) / 2) + 1
                    ) AS QueryText,
                    qs.total_worker_time      AS TotalWorkerTimeUs,
                    qs.total_logical_reads    AS TotalLogicalReads,
                    qs.total_physical_reads   AS TotalPhysicalReads,
                    qs.total_logical_writes   AS TotalLogicalWrites,
                    qs.total_elapsed_time     AS TotalElapsedTimeUs,
                    qs.execution_count        AS ExecutionCount
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
                WHERE t.dbid IS NOT NULL AND t.dbid > 4
                ORDER BY qs.total_elapsed_time DESC;
            ";

            return await ExecuteQueryAsync(sql, "LongRunning", ct);
        }

        /// <summary>
        /// CPU-intensive queries: sorted by total_worker_time DESC.
        /// total_worker_time = cumulative CPU time across all executions (microseconds).
        /// High values indicate queries that consume significant CPU schedulers.
        /// </summary>
        public async Task<List<QueryMetricDto>> GetCpuIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT TOP 25
                    DB_NAME(t.dbid)       AS DatabaseName,
                    CONVERT(VARCHAR(64), qs.query_hash, 1) AS QueryHash,
                    SUBSTRING(t.text, 
                        (qs.statement_start_offset / 2) + 1,
                        ((CASE qs.statement_end_offset 
                            WHEN -1 THEN DATALENGTH(t.text) 
                            ELSE qs.statement_end_offset 
                          END - qs.statement_start_offset) / 2) + 1
                    ) AS QueryText,
                    qs.total_worker_time      AS TotalWorkerTimeUs,
                    qs.total_logical_reads    AS TotalLogicalReads,
                    qs.total_physical_reads   AS TotalPhysicalReads,
                    qs.total_logical_writes   AS TotalLogicalWrites,
                    qs.total_elapsed_time     AS TotalElapsedTimeUs,
                    qs.execution_count        AS ExecutionCount
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
                WHERE t.dbid IS NOT NULL AND t.dbid > 4
                ORDER BY qs.total_worker_time DESC;
            ";

            return await ExecuteQueryAsync(sql, "CpuIntensive", ct);
        }

        /// <summary>
        /// IO-intensive queries: sorted by (total_logical_reads + total_physical_reads) DESC.
        /// logical_reads = pages read from buffer cache (each page = 8 KB).
        /// physical_reads = pages read from disk (cache misses).
        /// High IO queries cause buffer pool pressure and increase disk latency.
        /// </summary>
        public async Task<List<QueryMetricDto>> GetIoIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
        {
            const string sql = @"
                SELECT TOP 25
                    DB_NAME(t.dbid)       AS DatabaseName,
                    CONVERT(VARCHAR(64), qs.query_hash, 1) AS QueryHash,
                    SUBSTRING(t.text, 
                        (qs.statement_start_offset / 2) + 1,
                        ((CASE qs.statement_end_offset 
                            WHEN -1 THEN DATALENGTH(t.text) 
                            ELSE qs.statement_end_offset 
                          END - qs.statement_start_offset) / 2) + 1
                    ) AS QueryText,
                    qs.total_worker_time      AS TotalWorkerTimeUs,
                    qs.total_logical_reads    AS TotalLogicalReads,
                    qs.total_physical_reads   AS TotalPhysicalReads,
                    qs.total_logical_writes   AS TotalLogicalWrites,
                    qs.total_elapsed_time     AS TotalElapsedTimeUs,
                    qs.execution_count        AS ExecutionCount
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
                WHERE t.dbid IS NOT NULL AND t.dbid > 4
                ORDER BY (qs.total_logical_reads + qs.total_physical_reads) DESC;";

            return await ExecuteQueryAsync(sql, "IoIntensive", ct);
        }

        /// <summary>
        /// Plan-based analysis: finding implicit conversions and key lookups.
        /// Warning: Querying XML plans can be expensive on high-load servers.
        /// </summary>
        public async Task<List<QueryMetricDto>> GetPlanIssuesAsync(CancellationToken ct = default)
        {
            var results = new List<QueryMetricDto>();
            const string sql = @"
                SELECT TOP 10
                    DB_NAME(st.dbid) AS DatabaseName,
                    CONVERT(VARCHAR(64), qs.query_hash, 1) AS QueryHash,
                    SUBSTRING(st.text, (qs.statement_start_offset/2) + 1,
                    ((CASE statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE qs.statement_end_offset END 
                        - qs.statement_start_offset)/2) + 1) AS QueryText,
                    CAST(qp.query_plan AS NVARCHAR(MAX)) as PlanXml,
                    qs.total_worker_time AS TotalWorkerTimeUs,
                    qs.total_logical_reads AS TotalLogicalReads,
                    qs.total_physical_reads AS TotalPhysicalReads,
                    qs.total_logical_writes AS TotalLogicalWrites,
                    qs.total_elapsed_time AS TotalElapsedTimeUs,
                    qs.execution_count AS ExecutionCount
                FROM sys.dm_exec_query_stats AS qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
                CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
                WHERE CAST(qp.query_plan AS NVARCHAR(MAX)) LIKE '%CONVERT_IMPLICIT%'
                   OR CAST(qp.query_plan AS NVARCHAR(MAX)) LIKE '%Lookup=""1""%'
                ORDER BY qs.total_worker_time DESC;";

            try
            {
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    var planXml = SafeGet<string>(reader, "PlanXml", "");
                    string category = "PlanIssue";
                    if (planXml.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase))
                        category = "ImplicitConversion";
                    else if (planXml.Contains("Lookup=\"1\"", StringComparison.OrdinalIgnoreCase) || planXml.Contains("Lookup=\"true\"", StringComparison.OrdinalIgnoreCase))
                        category = "KeyLookupExpensive";

                    var totalUs = SafeGet<long>(reader, "TotalElapsedTimeUs");
                    var execCount = SafeGet<long>(reader, "ExecutionCount");

                    results.Add(new QueryMetricDto
                    {
                        DatabaseName       = SafeGet<string>(reader, "DatabaseName", ""),
                        QueryHash          = SafeGet<string>(reader, "QueryHash", ""),
                        QueryText          = SafeGet<string>(reader, "QueryText", ""),
                        TotalWorkerTimeUs  = SafeGet<long>(reader, "TotalWorkerTimeUs"),
                        TotalLogicalReads  = SafeGet<long>(reader, "TotalLogicalReads"),
                        TotalPhysicalReads = SafeGet<long>(reader, "TotalPhysicalReads"),
                        TotalLogicalWrites = SafeGet<long>(reader, "TotalLogicalWrites"),
                        TotalElapsedTimeUs = totalUs,
                        ExecutionCount     = execCount,
                        AvgElapsedTimeUs   = execCount > 0 ? (double)totalUs / execCount : 0,
                        Category           = category,
                        ProviderType       = "SqlServer",
                        CollectedAt        = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze PlanIssue queries");
            }
            return results;
        }

        // ── Shared execution helper ──
        private async Task<List<QueryMetricDto>> ExecuteQueryAsync(string sql, string category, CancellationToken ct)
        {
            var results = new List<QueryMetricDto>();
            try
            {
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    results.Add(new QueryMetricDto
                    {
                        DatabaseName     = SafeGet<string>(reader, "DatabaseName", ""),
                        QueryHash        = SafeGet<string>(reader, "QueryHash", ""),
                        QueryText        = SafeGet<string>(reader, "QueryText", ""),
                        TotalWorkerTimeUs  = SafeGet<long>(reader, "TotalWorkerTimeUs"),
                        TotalLogicalReads  = SafeGet<long>(reader, "TotalLogicalReads"),
                        TotalPhysicalReads = SafeGet<long>(reader, "TotalPhysicalReads"),
                        TotalLogicalWrites = SafeGet<long>(reader, "TotalLogicalWrites"),
                        TotalElapsedTimeUs = SafeGet<long>(reader, "TotalElapsedTimeUs"),
                        ExecutionCount     = SafeGet<long>(reader, "ExecutionCount"),
                        Category           = category,
                        CollectedAt        = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze {Category} queries", category);
            }
            return results;
        }
    }
}
