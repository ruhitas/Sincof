using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using MySqlConnector;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// MySQL/MariaDB query and index analyzer.
    /// Requires: performance_schema = ON (default since MySQL 5.6)
    /// </summary>
    public class MySqlAnalyzerService : IQueryAnalyzer, IIndexAnalyzer
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public MySqlAnalyzerService(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        // ═══════════════════════════════════════════════════════════════
        // QUERY ANALYSIS — MySQL Performance Schema
        // events_statements_summary_by_digest aggregates all executions
        // of the same normalized query (DIGEST = normalized fingerprint)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Long-running: highest average execution time (AVG_TIMER_WAIT).
        /// Catches single slow queries rather than high-frequency cheapones.
        /// </summary>
        public Task<List<QueryMetricDto>> GetLongRunningQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync("avg_timer_wait DESC", "LongRunning", ct);

        /// <summary>
        /// CPU-intensive: highest cumulative timer wait (SUM_TIMER_WAIT).
        /// In MySQL, timer includes CPU + IO + lock wait — closest proxy to CPU.
        /// Also surfaces SUM_ROWS_EXAMINED as logical-read equivalent.
        /// </summary>
        public Task<List<QueryMetricDto>> GetCpuIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync("sum_timer_wait DESC", "CpuIntensive", ct);

        /// <summary>
        /// IO-intensive: highest sequential table scans + no-index usage.
        /// SUM_SELECT_FULL_JOIN = full join scans (very expensive)
        /// SUM_NO_INDEX_USED   = queries that did a full table scan
        /// </summary>
        public Task<List<QueryMetricDto>> GetIoIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync("(sum_select_scan + sum_no_index_used) DESC", "IoIntensive", ct);

        private async Task<List<QueryMetricDto>> GetTopQueriesAsync(
            string orderBy, string category, CancellationToken ct)
        {
            var results = new List<QueryMetricDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                string sql = $@"
                    SELECT
                        schema_name                             AS DatabaseName,
                        digest                                  AS QueryHash,
                        LEFT(digest_text, 500)                  AS QueryText,
                        count_star                              AS ExecutionCount,
                        SUM_TIMER_WAIT      / 1000000000        AS TotalElapsedTimeMs,
                        AVG_TIMER_WAIT      / 1000000000        AS AvgElapsedTimeMs,
                        sum_rows_examined                       AS TotalLogicalReads,
                        sum_rows_sent                          AS RowsSent,
                        sum_no_index_used                      AS NoIndexUsed,
                        sum_select_scan                        AS FullScans
                    FROM performance_schema.events_statements_summary_by_digest
                    WHERE schema_name IS NOT NULL
                      AND schema_name NOT IN ('information_schema','mysql','performance_schema','sys')
                    ORDER BY {orderBy}
                    LIMIT 25";

                await using var cmd = new MySqlCommand(sql, conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    long execCount = reader.IsDBNull(2) ? 1 : reader.GetInt64(2);
                    double totalMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                    double avgMs   = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
                    long noIdx   = reader.IsDBNull(8) ? 0 : reader.GetInt64(8);

                    results.Add(new QueryMetricDto
                    {
                        DatabaseName       = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        QueryHash          = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        QueryText          = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ExecutionCount     = execCount,
                        TotalElapsedTimeUs = (long)(totalMs * 1000.0),
                        AvgElapsedTimeUs   = avgMs * 1000.0,    // settable now
                        TotalLogicalReads  = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                        // Tag IO-free queries that never use an index with a sub-category
                        Category           = noIdx > 0 ? "NoIndexUsed" : category,
                        ProviderType       = "MySql",
                        CollectedAt        = DateTime.UtcNow
                    });
                }
                _logger.LogInformation("MySQL [{Cat}] — {N} queries collected", category, results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MySQL query stats unavailable (performance_schema disabled?)");
            }
            return results;
        }

        // ═══════════════════════════════════════════════════════════════
        // INDEX ANALYSIS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// InnoDB "fragmentation": Data_Free / (Data_Length + Index_Length).
        /// High ratio → OPTIMIZE TABLE is recommended.
        /// Only flagged when table is ≥ 10 MB and frag > 20%.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetFragmentedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                string sql = @"
                    SELECT
                        table_name,
                        engine,
                        ROUND((data_free  / (data_length + index_length + 1)) * 100, 1) AS frag_pct,
                        ROUND((data_length + index_length)  / 1048576.0, 2)             AS total_mb,
                        ROUND(data_free                      / 1048576.0, 2)             AS free_mb
                    FROM information_schema.tables
                    WHERE table_schema = @db
                      AND table_type   = 'BASE TABLE'
                      AND (data_length + index_length) > 10485760    -- > 10 MB
                      AND (data_free  / (data_length + index_length + 1)) > 0.20  -- > 20 %
                    ORDER BY frag_pct DESC
                    LIMIT 30";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@db", database);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    double frag = reader.GetDouble(2);
                    double szMb = reader.GetDouble(3);
                    string tbl  = reader.GetString(0);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName         = database,
                        TableName            = tbl,
                        IndexName            = "(TABLE)",
                        IssueType            = "Fragmented",
                        Suggestion           = "OPTIMIZE TABLE",
                        FragmentationPercent = frag,
                        ImpactScore          = Math.Min(100, frag * (szMb / 100.0)),
                        Script               = $"OPTIMIZE TABLE `{database}`.`{tbl}`;",
                        ProviderType         = "MySql",
                        DetectedAt           = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MySQL frag check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Missing index detection via performance_schema:
        /// Tables with high SUM_NO_INDEX_USED or SUM_SELECT_SCAN are candidates.
        /// Also checks information_schema for tables with no indexes at all.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetMissingIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // 1. Queries with no index used — group by object_name from file summary
                const string noIdxSql = @"
                    SELECT
                        object_schema, object_name,
                        SUM(count_star)       AS total_scans,
                        SUM(sum_timer_wait)/1e9 AS total_wait_ms
                    FROM performance_schema.table_io_waits_summary_by_index_usage
                    WHERE index_name IS NULL              -- NULL = no index used
                      AND object_schema = @db
                      AND count_star > 50                 -- only meaningful tables
                    GROUP BY object_schema, object_name
                    ORDER BY total_scans DESC
                    LIMIT 20";

                await using var cmd1 = new MySqlCommand(noIdxSql, conn);
                cmd1.Parameters.AddWithValue("@db", database);
                await using var r1 = await cmd1.ExecuteReaderAsync(ct);
                while (await r1.ReadAsync(ct))
                {
                    string tbl   = r1.GetString(1);
                    long   scans = r1.GetInt64(2);
                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        TableName    = tbl,
                        IndexName    = "(NONE USED)",
                        IssueType    = "Missing",
                        Suggestion   = "CREATE INDEX",
                        ImpactScore  = Math.Min(100, scans / 100.0),
                        Script       = $"-- Analyze query patterns then:\n-- CREATE INDEX idx_{tbl}_col ON `{database}`.`{tbl}` (column_name);",
                        ProviderType = "MySql",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
                await r1.CloseAsync();

                // 2. Tables with zero indexes (except PRIMARY)
                const string noIndexTableSql = @"
                    SELECT t.table_name
                    FROM information_schema.tables t
                    WHERE t.table_schema = @db
                      AND t.table_type = 'BASE TABLE'
                      AND t.table_rows > 1000
                      AND NOT EXISTS (
                          SELECT 1 FROM information_schema.statistics s
                          WHERE s.table_schema = t.table_schema
                            AND s.table_name  = t.table_name
                            AND s.index_name  != 'PRIMARY'
                      )";

                await using var cmd2 = new MySqlCommand(noIndexTableSql, conn);
                cmd2.Parameters.AddWithValue("@db", database);
                await using var r2 = await cmd2.ExecuteReaderAsync(ct);
                while (await r2.ReadAsync(ct))
                {
                    string tbl = r2.GetString(0);
                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        TableName    = tbl,
                        IndexName    = "(NO SECONDARY INDEX)",
                        IssueType    = "Missing",
                        Suggestion   = "CREATE INDEX",
                        ImpactScore  = 70,
                        Script       = $"-- Table has only PRIMARY KEY. Add indexes for frequently queried columns:\n-- CREATE INDEX idx_{tbl}_col ON `{database}`.`{tbl}` (column_name);",
                        ProviderType = "MySql",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MySQL missing index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Unused indexes: performance_schema.table_io_waits_summary_by_index_usage
        /// where count_star (total IO waits) = 0 and is not PRIMARY KEY.
        /// Zero IO waits since last flush means nobody reads via this index.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetUnusedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT
                        w.object_schema, w.object_name, w.index_name,
                        s.cardinality,
                        ROUND(s.sub_part , 0) AS prefix_len
                    FROM performance_schema.table_io_waits_summary_by_index_usage w
                    JOIN information_schema.statistics s
                        ON s.table_schema = w.object_schema
                       AND s.table_name   = w.object_name
                       AND s.index_name   = w.index_name
                    WHERE w.index_name  IS NOT NULL
                      AND w.index_name  != 'PRIMARY'
                      AND w.count_star   = 0
                      AND w.object_schema = @db
                    ORDER BY s.cardinality DESC
                    LIMIT 30";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@db", database);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string tbl = reader.GetString(1);
                    string idx = reader.GetString(2);
                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        TableName    = tbl,
                        IndexName    = idx,
                        IssueType    = "Unused",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = 40,  // Saves write overhead and disk space
                        Script       = $"DROP INDEX `{idx}` ON `{database}`.`{tbl}`;",
                        ProviderType = "MySql",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MySQL unused index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Duplicate indexes: two indexes that start with the same leading columns.
        /// Uses information_schema.statistics grouped by column order.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetDuplicateIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // Build column list per index then self-join to find redundant pairs
                const string sql = @"
                    SELECT
                        a.table_name,
                        a.index_name AS idx1,
                        b.index_name AS idx2,
                        a.cols
                    FROM (
                        SELECT table_schema, table_name, index_name,
                               GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') AS cols
                        FROM information_schema.statistics
                        WHERE table_schema = @db AND index_name != 'PRIMARY'
                        GROUP BY table_schema, table_name, index_name
                    ) a
                    JOIN (
                        SELECT table_schema, table_name, index_name,
                               GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') AS cols
                        FROM information_schema.statistics
                        WHERE table_schema = @db AND index_name != 'PRIMARY'
                        GROUP BY table_schema, table_name, index_name
                    ) b ON a.table_schema = b.table_schema
                       AND a.table_name   = b.table_name
                       AND a.index_name  != b.index_name
                       AND (a.cols = b.cols OR LOCATE(CONCAT(a.cols,','), b.cols) = 1)
                    WHERE a.index_name < b.index_name   -- avoid symmetric duplicates
                    LIMIT 20";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@db", database);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string tbl  = reader.GetString(0);
                    string idx1 = reader.GetString(1);
                    string idx2 = reader.GetString(2);
                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        TableName    = tbl,
                        IndexName    = $"{idx1} ↔ {idx2}",
                        IssueType    = "Duplicate",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = 65,
                        Script       = $"-- {idx2} is redundant, covered by {idx1}:\nDROP INDEX `{idx2}` ON `{database}`.`{tbl}`;",
                        ProviderType = "MySql",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MySQL duplicate index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// MySQL has no "heap" concept — all InnoDB tables without a PK effectively
        /// use a hidden 6-byte rowid. We surface tables with missing explicit PKs.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetHeapTablesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT t.table_name, t.table_rows
                    FROM information_schema.tables t
                    WHERE t.table_schema = @db
                      AND t.table_type  = 'BASE TABLE'
                      AND t.table_rows  > 500
                      AND NOT EXISTS (
                          SELECT 1 FROM information_schema.table_constraints tc
                          WHERE tc.table_schema = t.table_schema
                            AND tc.table_name   = t.table_name
                            AND tc.constraint_type = 'PRIMARY KEY'
                      )";

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@db", database);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string tbl = reader.GetString(0);
                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        TableName    = tbl,
                        IndexName    = "(NO PRIMARY KEY)",
                        IssueType    = "Heap",
                        Suggestion   = "ADD PRIMARY KEY",
                        ImpactScore  = 80,
                        Script       = $"-- Add a surrogate PK:\nALTER TABLE `{database}`.`{tbl}` ADD COLUMN id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY FIRST;",
                        ProviderType = "MySql",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MySQL heap table check failed for {Db}", database); }
            return results;
        }
    }
}
