using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using Npgsql;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// PostgreSQL query and index analyzer.
    /// Requires: CREATE EXTENSION pg_stat_statements; (for query stats)
    /// pg_stat_user_tables / pg_stat_user_indexes are always available.
    /// </summary>
    public class PostgresAnalyzerService : IQueryAnalyzer, IIndexAnalyzer
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public PostgresAnalyzerService(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        // ═══════════════════════════════════════════════════════════════
        // QUERY ANALYSIS — pg_stat_statements
        // All times in pg_stat_statements are in milliseconds (PG 13+).
        // Earlier versions use microseconds — we normalise to microseconds.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Long-running: highest MEAN execution time.
        /// Catches single queries that are individually slow.
        /// </summary>
        public Task<List<QueryMetricDto>> GetLongRunningQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync("mean_exec_time DESC", "LongRunning", ct);

        /// <summary>
        /// CPU-intensive: highest cumulative execution time (total_exec_time).
        /// Captures queries that burn the most aggregate CPU/time across all calls.
        /// </summary>
        public Task<List<QueryMetricDto>> GetCpuIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync("total_exec_time DESC", "CpuIntensive", ct);

        /// <summary>
        /// IO-intensive: highest block read/write time.
        /// blk_read_time + blk_write_time (requires track_io_timing = on).
        /// Falls back to shared_blks_hit + shared_blks_read if IO timing not enabled.
        /// </summary>
        public Task<List<QueryMetricDto>> GetIoIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync(
                "(blk_read_time + blk_write_time + shared_blks_read) DESC",
                "IoIntensive", ct);

        private async Task<List<QueryMetricDto>> GetTopQueriesAsync(
            string orderBy, string category, CancellationToken ct)
        {
            var results = new List<QueryMetricDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // Detect if pg_stat_statements is available
                await using var chk = new NpgsqlCommand(
                    "SELECT 1 FROM pg_extension WHERE extname='pg_stat_statements'", conn);
                var exists = await chk.ExecuteScalarAsync(ct);
                if (exists == null)
                {
                    _logger.LogWarning("pg_stat_statements extension not installed on this Postgres server. Skipping query analysis.");
                    return results;
                }

                string sql = $@"
                    SELECT
                        d.datname                           AS DatabaseName,
                        s.queryid::text                     AS QueryHash,
                        LEFT(s.query, 500)                  AS QueryText,
                        s.calls                             AS ExecutionCount,
                        s.total_exec_time                   AS TotalTimeMs,
                        s.mean_exec_time                    AS AvgTimeMs,
                        s.shared_blks_hit + s.shared_blks_read AS TotalLogicalReads,
                        s.shared_blks_read                  AS TotalPhysicalReads,
                        s.blk_read_time  + s.blk_write_time AS IoTimeMs,
                        s.rows                              AS TotalRows
                    FROM pg_stat_statements s
                    JOIN pg_database d ON s.dbid = d.oid
                    WHERE s.calls > 5
                    ORDER BY {orderBy}
                    LIMIT 25";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    long calls    = reader.IsDBNull(3) ? 1 : reader.GetInt64(3);
                    double totMs  = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
                    double avgMs  = reader.IsDBNull(5) ? 0 : reader.GetDouble(5);
                    long logReads = reader.IsDBNull(6) ? 0 : Convert.ToInt64(reader.GetValue(6));
                    long physRead = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7));
                    double ioMs   = reader.IsDBNull(8) ? 0 : reader.GetDouble(8);

                    // Determine sub-category
                    string cat = category;
                    if (ioMs > totMs * 0.5 && ioMs > 100) cat = "IoIntensive";

                    results.Add(new QueryMetricDto
                    {
                        DatabaseName       = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        QueryHash          = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        QueryText          = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ExecutionCount     = calls,
                        TotalElapsedTimeUs = (long)(totMs * 1000.0),
                        AvgElapsedTimeUs   = avgMs * 1000.0,
                        TotalLogicalReads  = logReads,
                        TotalPhysicalReads = physRead,
                        Category           = cat,
                        ProviderType       = "Postgres",
                        CollectedAt        = DateTime.UtcNow
                    });
                }
                _logger.LogInformation("Postgres [{Cat}] — {N} queries", category, results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Postgres query stats collection failed for category {Cat}", category);
            }
            return results;
        }

        // ═══════════════════════════════════════════════════════════════
        // INDEX ANALYSIS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Table/index bloat estimation via pgstattuple-compatible formula.
        /// Uses pg_relation_size + pg_stat_user_tables dead tuple ratio without
        /// requiring the pgstattuple extension (which needs superuser).
        /// Tables with dead_tup_ratio > 20% or dead_tup > 100k are flagged.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetFragmentedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT
                        schemaname,
                        relname,
                        n_dead_tup,
                        n_live_tup,
                        CASE WHEN (n_live_tup + n_dead_tup) > 0
                             THEN ROUND(n_dead_tup * 100.0 / (n_live_tup + n_dead_tup), 1)
                             ELSE 0 END                         AS dead_pct,
                        pg_size_pretty(pg_total_relation_size(schemaname||'.'||relname)) AS table_size,
                        pg_total_relation_size(schemaname||'.'||relname) / 1048576.0   AS size_mb,
                        last_autovacuum,
                        last_vacuum
                    FROM pg_stat_user_tables
                    WHERE (n_live_tup + n_dead_tup) > 1000
                      AND (n_dead_tup > 100000
                           OR (n_live_tup + n_dead_tup) > 0
                              AND n_dead_tup * 100.0 / (n_live_tup + n_dead_tup) > 20)
                    ORDER BY dead_pct DESC
                    LIMIT 30";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string schema   = reader.GetString(0);
                    string tbl      = reader.GetString(1);
                    long   deadTup  = reader.GetInt64(2);
                    double deadPct  = reader.GetDouble(4);
                    double sizeMb   = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
                    bool   noVacuum = reader.IsDBNull(7) && reader.IsDBNull(8);

                    string note = noVacuum ? " (VACUUM has never run!)" : "";

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName         = database,
                        SchemaName           = schema,
                        TableName            = tbl,
                        IndexName            = "(TABLE BLOAT)",
                        IssueType            = "Fragmented",
                        Suggestion           = "VACUUM ANALYZE",
                        FragmentationPercent = deadPct,
                        ImpactScore          = Math.Min(100, deadPct * (sizeMb / 50.0)),
                        Script               = $"VACUUM ANALYZE \"{schema}\".\"{tbl}\";{note}",
                        ProviderType         = "Postgres",
                        DetectedAt           = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Postgres bloat check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Missing indexes: tables with high sequential scan ratio.
        /// seq_scan / (seq_scan + idx_scan) > 70% AND n_live_tup > 10k is a strong signal.
        /// Also surfaces tables where seq_tup_read >> idx_tup_fetch.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetMissingIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT
                        schemaname,
                        relname,
                        seq_scan,
                        idx_scan,
                        n_live_tup,
                        seq_tup_read,
                        idx_tup_fetch,
                        CASE WHEN (seq_scan + COALESCE(idx_scan,0)) > 0
                             THEN ROUND(seq_scan * 100.0 / (seq_scan + COALESCE(idx_scan,0)), 1)
                             ELSE 100 END AS seq_ratio_pct,
                        pg_size_pretty(pg_total_relation_size(schemaname||'.'||relname)) AS table_size
                    FROM pg_stat_user_tables
                    WHERE n_live_tup > 10000
                      AND seq_scan > 100
                      AND (COALESCE(idx_scan, 0) = 0
                           OR seq_scan * 1.0 / NULLIF(idx_scan, 0) > 3.0)
                    ORDER BY seq_tup_read DESC
                    LIMIT 20";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string schema   = reader.GetString(0);
                    string tbl      = reader.GetString(1);
                    long   seqScans = reader.GetInt64(2);
                    long   idxScans = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    long   liveTup  = reader.GetInt64(4);
                    double seqRatio = reader.GetDouble(7);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = tbl,
                        IndexName    = "(MISSING — SEQ SCAN)",
                        IssueType    = "Missing",
                        Suggestion   = "CREATE INDEX",
                        ImpactScore  = Math.Min(100, seqRatio),
                        Script       = $"-- {seqScans} seq scans vs {idxScans} idx scans on {liveTup:N0} rows\n"
                                     + $"-- Identify the filter columns then:\n"
                                     + $"CREATE INDEX CONCURRENTLY idx_{tbl}_col ON \"{schema}\".\"{tbl}\" (column_name);",
                        ProviderType = "Postgres",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Postgres missing index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Unused indexes: idx_scan = 0 since last stats reset.
        /// Excludes primary keys and unique constraints (they enforce data integrity).
        /// Only reports indexes on sizable tables (> 1 MB).
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetUnusedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT
                        ui.schemaname,
                        ui.relname,
                        ui.indexrelname,
                        pg_size_pretty(pg_relation_size(ui.indexrelid)) AS idx_size,
                        pg_relation_size(ui.indexrelid) / 1048576.0    AS idx_mb,
                        i.indisunique,
                        i.indisprimary
                    FROM pg_stat_user_indexes ui
                    JOIN pg_index i ON i.indexrelid = ui.indexrelid
                    WHERE ui.idx_scan = 0
                      AND NOT i.indisunique
                      AND NOT i.indisprimary
                      AND pg_relation_size(ui.indexrelid) > 1048576      -- > 1 MB
                    ORDER BY pg_relation_size(ui.indexrelid) DESC
                    LIMIT 30";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string schema  = reader.GetString(0);
                    string tbl     = reader.GetString(1);
                    string idx     = reader.GetString(2);
                    double idxMb   = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = tbl,
                        IndexName    = idx,
                        IssueType    = "Unused",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = Math.Min(100, idxMb),
                        Script       = $"DROP INDEX CONCURRENTLY \"{schema}\".\"{idx}\";",
                        ProviderType = "Postgres",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Postgres unused index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// Duplicate indexes detected by comparing pg_indexes definition columns.
        /// Two indexes are duplicates when they cover identical column sets in same order.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetDuplicateIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                // pg_index stores columns as array of attribute numbers; compare directly
                const string sql = @"
                    SELECT
                        ta.relname                          AS table_name,
                        ia.relname                          AS idx1,
                        ib.relname                          AS idx2,
                        array_to_string(a.indkey,' ')       AS col_list
                    FROM pg_index a
                    JOIN pg_index b
                        ON  a.indrelid  = b.indrelid
                       AND  a.indexrelid < b.indexrelid
                       AND  a.indkey    = b.indkey          -- same column set
                       AND  a.indclass  = b.indclass        -- same operator class
                    JOIN pg_class ta ON ta.oid = a.indrelid
                    JOIN pg_class ia ON ia.oid = a.indexrelid
                    JOIN pg_class ib ON ib.oid = b.indexrelid
                    JOIN pg_namespace ns ON ns.oid = ta.relnamespace
                    WHERE ns.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
                    LIMIT 20";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
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
                        ImpactScore  = 70,
                        Script       = $"-- {idx2} duplicates {idx1}:\nDROP INDEX CONCURRENTLY \"{idx2}\";",
                        ProviderType = "Postgres",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Postgres duplicate index check failed for {Db}", database); }
            return results;
        }

        /// <summary>
        /// PostgreSQL equivalent of "heap tables": tables where all accesses are seq scans
        /// and no meaningful index exists. We surface it as a special missing-index case.
        /// Also detects tables without any index at all beyond the implicit ctid scan.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetHeapTablesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT
                        t.schemaname,
                        t.relname,
                        t.n_live_tup,
                        pg_size_pretty(pg_total_relation_size(t.schemaname||'.'||t.relname)) AS tbl_size
                    FROM pg_stat_user_tables t
                    WHERE t.n_live_tup > 5000
                      AND NOT EXISTS (
                          SELECT 1 FROM pg_indexes i
                          WHERE i.schemaname = t.schemaname
                            AND i.tablename  = t.relname
                      )
                    ORDER BY t.n_live_tup DESC
                    LIMIT 20";

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string schema = reader.GetString(0);
                    string tbl    = reader.GetString(1);
                    long   rows   = reader.GetInt64(2);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = tbl,
                        IndexName    = "(NO INDEX AT ALL)",
                        IssueType    = "Heap",
                        Suggestion   = "CREATE INDEX / ADD PRIMARY KEY",
                        ImpactScore  = Math.Min(100, rows / 10000.0),
                        Script       = $"-- {rows:N0} rows with zero indexes:\n"
                                     + $"ALTER TABLE \"{schema}\".\"{tbl}\" ADD COLUMN id BIGSERIAL PRIMARY KEY;\n"
                                     + $"-- Then add query-specific indexes as needed.",
                        ProviderType = "Postgres",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Postgres heap table check failed for {Db}", database); }
            return results;
        }
    }
}
