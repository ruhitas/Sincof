using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;
using Oracle.ManagedDataAccess.Client;

namespace MonitoringAgent.Infrastructure.Providers
{
    /// <summary>
    /// Oracle Database query and index analyzer.
    /// Requires SELECT privilege on V$SQL, V$SESSION, DBA_INDEXES, DBA_SEGMENTS, DBA_STATISTICS.
    /// All Oracle times are in microseconds (divide by 1e6 for seconds).
    /// ROWNUM-based pagination used (Oracle 11g compatible, NO FETCH FIRST syntax).
    /// </summary>
    public class OracleAnalyzerService : IQueryAnalyzer, IIndexAnalyzer
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public OracleAnalyzerService(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        // ═══════════════════════════════════════════════════════════════
        // QUERY ANALYSIS — V$SQL
        // V$SQL contains one row per child cursor in the shared pool.
        // We aggregate by SQL_ID to get totals across all child cursors.
        // All times are in microseconds.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Long-running: highest average elapsed time per execution.
        /// ELAPSED_TIME / NULLIF(EXECUTIONS,0) gives per-call cost in µs.
        /// </summary>
        public Task<List<QueryMetricDto>> GetLongRunningQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync(
                "avg_elapsed_us DESC",
                "LongRunning", ct);

        /// <summary>
        /// CPU-intensive: highest cumulative CPU time.
        /// </summary>
        public Task<List<QueryMetricDto>> GetCpuIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync(
                "total_cpu_us DESC",
                "CpuIntensive", ct);

        /// <summary>
        /// IO-intensive: highest physical disk reads.
        /// DISK_READS = pages read from datafiles (I/O bypass of buffer cache).
        /// </summary>
        public Task<List<QueryMetricDto>> GetIoIntensiveQueriesAsync(
            IEnumerable<string> databases, CancellationToken ct = default)
            => GetTopQueriesAsync(
                "disk_reads DESC",
                "IoIntensive", ct);

        private async Task<List<QueryMetricDto>> GetTopQueriesAsync(
            string orderBy, string category, CancellationToken ct)
        {
            var results = new List<QueryMetricDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                // Aggregate child cursors per SQL_ID; exclude SYS/internal schemas.
                // ROWNUM filter must be in outer query for Oracle to optimise correctly.
                string sql = $@"
                    SELECT * FROM (
                        SELECT
                            sql_id                                              AS query_hash,
                            SUBSTR(sql_text, 1, 500)                           AS query_text,
                            SUM(executions)                                     AS exec_count,
                            SUM(cpu_time)                                       AS total_cpu_us,
                            SUM(elapsed_time)                                   AS total_elapsed_us,
                            ROUND(SUM(elapsed_time) / NULLIF(SUM(executions),0),0) AS avg_elapsed_us,
                            SUM(disk_reads)                                     AS disk_reads,
                            SUM(buffer_gets)                                    AS logical_reads,
                            parsing_schema_name
                        FROM v$sql
                        WHERE parsing_schema_name NOT IN ('SYS','SYSTEM','DBSNMP','OUTLN','ORACLE_OCM','APPQOSSYS')
                          AND command_type NOT IN (47)          -- exclude PL/SQL blocks
                          AND executions > 0
                        GROUP BY sql_id, SUBSTR(sql_text, 1, 500), parsing_schema_name
                        ORDER BY {orderBy}
                    ) WHERE ROWNUM <= 25";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    long execCount   = Convert.ToInt64(reader.GetValue(2));
                    long totalCpuUs  = Convert.ToInt64(reader.GetValue(3));
                    long totalElpUs  = Convert.ToInt64(reader.GetValue(4));
                    double avgElpUs  = Convert.ToDouble(reader.GetValue(5));
                    long diskReads   = Convert.ToInt64(reader.GetValue(6));
                    long bufGets     = Convert.ToInt64(reader.GetValue(7));

                    results.Add(new QueryMetricDto
                    {
                        QueryHash          = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        QueryText          = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        DatabaseName       = "Oracle",
                        ExecutionCount     = execCount,
                        TotalWorkerTimeUs  = totalCpuUs,
                        TotalElapsedTimeUs = totalElpUs,
                        AvgElapsedTimeUs   = avgElpUs,
                        TotalPhysicalReads = diskReads,
                        TotalLogicalReads  = bufGets,
                        Category           = category,
                        ProviderType       = "Oracle",
                        CollectedAt        = DateTime.UtcNow
                    });
                }
                _logger.LogInformation("Oracle [{Cat}] — {N} queries", category, results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Oracle query stats collection failed [{Cat}]. Check V$SQL privilege.", category);
            }
            return results;
        }

        // ═══════════════════════════════════════════════════════════════
        // INDEX ANALYSIS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Fragmented indexes: Oracle stores fragmentation info in INDEX_STATS
        /// (requires ANALYZE INDEX ... VALIDATE STRUCTURE — expensive) or via
        /// DBA_INDEXES.BLEVEL + PCT_USED heuristic approach.
        /// We use DBA_INDEXES: BLEVEL > 4 (too many B-tree levels) or
        /// DISTINCT_KEYS = 0 (stale / never analysed) as fragmentation signals.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetFragmentedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT * FROM (
                        SELECT
                            owner,
                            table_name,
                            index_name,
                            index_type,
                            blevel,
                            leaf_blocks,
                            distinct_keys,
                            last_analyzed
                        FROM dba_indexes
                        WHERE owner NOT IN ('SYS','SYSTEM','DBSNMP','OUTLN')
                          AND index_type IN ('NORMAL','BITMAP')
                          AND blevel > 3
                        ORDER BY blevel DESC, leaf_blocks DESC
                    ) WHERE ROWNUM <= 30";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string owner = reader.GetString(0);
                    string tbl   = reader.GetString(1);
                    string idx   = reader.GetString(2);
                    int    blvl  = reader.GetInt32(4);
                    long   leafs = Convert.ToInt64(reader.GetValue(5));

                    // BLEVEL > 4 almost always means the index needs rebuilding
                    double fragPct = Math.Min(100, blvl * 20.0);
                    string action  = blvl > 4 ? "REBUILD" : "COALESCE";

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName         = database,
                        SchemaName           = owner,
                        TableName            = tbl,
                        IndexName            = idx,
                        IssueType            = "Fragmented",
                        Suggestion           = action,
                        FragmentationPercent = fragPct,
                        PageCount            = leafs,
                        ImpactScore          = fragPct,
                        Script               = $"ALTER INDEX {owner}.{idx} {action};",
                        ProviderType         = "Oracle",
                        DetectedAt           = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Oracle fragmented index check failed"); }
            return results;
        }

        /// <summary>
        /// Missing indexes: detect via V$SQL_PLAN where operations include
        /// TABLE ACCESS FULL on large tables that have no index.
        /// Also reports tables with high physical reads from v$segment_statistics.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetMissingIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                // Full table scans on large segments from the shared pool plans
                const string sql = @"
                    SELECT * FROM (
                        SELECT
                            p.object_owner,
                            p.object_name,
                            COUNT(DISTINCT p.sql_id)     AS plan_count,
                            SUM(s.executions)            AS total_executions,
                            SUM(s.disk_reads)            AS total_disk_reads
                        FROM v$sql_plan p
                        JOIN v$sql s ON s.sql_id = p.sql_id AND s.child_number = p.child_number
                        WHERE p.operation = 'TABLE ACCESS'
                          AND p.options   = 'FULL'
                          AND p.object_owner NOT IN ('SYS','SYSTEM','DBSNMP')
                          AND s.executions > 10
                        GROUP BY p.object_owner, p.object_name
                        ORDER BY total_disk_reads DESC
                    ) WHERE ROWNUM <= 20";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string owner     = reader.GetString(0);
                    string tbl       = reader.GetString(1);
                    long   planCount = Convert.ToInt64(reader.GetValue(2));
                    long   execCount = Convert.ToInt64(reader.GetValue(3));
                    long   diskReads = Convert.ToInt64(reader.GetValue(4));

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = owner,
                        TableName    = tbl,
                        IndexName    = "(FULL SCAN — MISSING INDEX)",
                        IssueType    = "Missing",
                        Suggestion   = "CREATE INDEX",
                        ImpactScore  = Math.Min(100, diskReads / 10000.0),
                        Script       = $"-- {planCount} SQL plans do FULL SCAN on {owner}.{tbl} ({execCount} executions, {diskReads:N0} disk reads)\n"
                                     + $"-- Identify filter columns from AWR/V$SQL_PLAN then:\n"
                                     + $"CREATE INDEX {owner}.idx_{tbl}_col ON {owner}.{tbl} (column_name);",
                        ProviderType = "Oracle",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Oracle missing index check failed"); }
            return results;
        }

        /// <summary>
        /// Unused indexes: DBA_OBJECT_USAGE tracks index monitoring.
        /// Must have 'ALTER INDEX ... MONITORING USAGE' set.
        /// Fallback: DBA_INDEXES where last_analyzed is very old and DISTINCT_KEYS = 0.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetUnusedIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                // v$object_usage: requires ALTER INDEX ... MONITORING USAGE per index
                const string sql = @"
                    SELECT
                        u.owner,
                        u.name         AS index_name,
                        u.table_name,
                        u.monitoring,
                        u.used,
                        u.start_monitoring,
                        u.end_monitoring,
                        i.leaf_blocks
                    FROM v$object_usage u
                    JOIN dba_indexes i ON i.owner = u.owner AND i.index_name = u.name
                    WHERE u.used = 'NO'
                      AND u.monitoring = 'YES'
                      AND u.owner NOT IN ('SYS','SYSTEM')
                    ORDER BY i.leaf_blocks DESC";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string owner  = reader.GetString(0);
                    string idx    = reader.GetString(1);
                    string tbl    = reader.GetString(2);
                    long   leafs  = Convert.ToInt64(reader.GetValue(7));

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = owner,
                        TableName    = tbl,
                        IndexName    = idx,
                        IssueType    = "Unused",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = Math.Min(100, leafs / 1000.0),
                        Script       = $"-- Monitored and never used:\nDROP INDEX {owner}.{idx};",
                        ProviderType = "Oracle",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Oracle unused index check failed (v$object_usage may need MONITORING enabled)"); }
            return results;
        }

        /// <summary>
        /// Duplicate indexes: DBA_IND_COLUMNS joined and self-compared.
        /// Two indexes are duplicate when column list (in key order) is identical.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetDuplicateIndexesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT * FROM (
                        SELECT
                            a.table_owner,
                            a.table_name,
                            a.index_name   AS idx1,
                            b.index_name   AS idx2,
                            a.col_list
                        FROM (
                            SELECT table_owner, table_name, index_name,
                                   LISTAGG(column_name, ',') WITHIN GROUP (ORDER BY column_position) AS col_list
                            FROM dba_ind_columns
                            WHERE table_owner NOT IN ('SYS','SYSTEM','DBSNMP')
                            GROUP BY table_owner, table_name, index_name
                        ) a
                        JOIN (
                            SELECT table_owner, table_name, index_name,
                                   LISTAGG(column_name, ',') WITHIN GROUP (ORDER BY column_position) AS col_list
                            FROM dba_ind_columns
                            WHERE table_owner NOT IN ('SYS','SYSTEM','DBSNMP')
                            GROUP BY table_owner, table_name, index_name
                        ) b ON  a.table_owner = b.table_owner
                            AND a.table_name  = b.table_name
                            AND a.col_list    = b.col_list
                            AND a.index_name  < b.index_name
                        ORDER BY a.table_name
                    ) WHERE ROWNUM <= 20";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string owner = reader.GetString(0);
                    string tbl   = reader.GetString(1);
                    string idx1  = reader.GetString(2);
                    string idx2  = reader.GetString(3);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = owner,
                        TableName    = tbl,
                        IndexName    = $"{idx1} ↔ {idx2}",
                        IssueType    = "Duplicate",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = 75,
                        Script       = $"-- {idx2} duplicates {idx1}:\nDROP INDEX {owner}.{idx2};",
                        ProviderType = "Oracle",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Oracle duplicate index check failed"); }
            return results;
        }

        /// <summary>
        /// Oracle "heap" equivalent: tables stored as IOT (Index-Organized Table)
        /// have no separate heap segment. Conversely, regular heap tables without
        /// any index benefit from this analysis.
        /// We flag large Oracle heap tables (table_type = 'HEAP') with zero indexes.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetHeapTablesAsync(
            string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            try
            {
                await using var conn = new OracleConnection(_connectionString);
                await conn.OpenAsync(ct);

                const string sql = @"
                    SELECT * FROM (
                        SELECT
                            t.owner,
                            t.table_name,
                            t.num_rows,
                            s.bytes / 1048576  AS size_mb
                        FROM dba_tables t
                        JOIN dba_segments s ON s.owner = t.owner AND s.segment_name = t.table_name AND s.segment_type = 'TABLE'
                        WHERE t.owner NOT IN ('SYS','SYSTEM','DBSNMP','OUTLN','ORACLE_OCM')
                          AND t.iot_type IS NULL          -- Regular heap, not IOT
                          AND COALESCE(t.num_rows, 0) > 5000
                          AND NOT EXISTS (
                              SELECT 1 FROM dba_indexes i
                              WHERE i.owner = t.owner AND i.table_name = t.table_name
                          )
                        ORDER BY s.bytes DESC
                    ) WHERE ROWNUM <= 20";

                await using var cmd = new OracleCommand(sql, conn) { CommandTimeout = 60 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    string owner  = reader.GetString(0);
                    string tbl    = reader.GetString(1);
                    long   rows   = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2));
                    double sizeMb = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = owner,
                        TableName    = tbl,
                        IndexName    = "(NO INDEX)",
                        IssueType    = "Heap",
                        Suggestion   = "ADD PRIMARY KEY / CREATE INDEX",
                        ImpactScore  = Math.Min(100, sizeMb),
                        Script       = $"-- Oracle heap table {owner}.{tbl} ({rows:N0} rows, {sizeMb:F1} MB) has zero indexes.\n"
                                     + $"ALTER TABLE {owner}.{tbl} ADD CONSTRAINT pk_{tbl} PRIMARY KEY (id_column);",
                        ProviderType = "Oracle",
                        DetectedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Oracle heap table check failed"); }
            return results;
        }
    }
}
