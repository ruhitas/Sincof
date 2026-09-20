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
    // INDEX ANALYSIS SERVICE — 5 types of index health checks
    // ═══════════════════════════════════════════════════════════════
    public class IndexAnalysisService : SqlServiceBase, IIndexAnalyzer
    {
        private readonly IndexAnalysisConfig _indexConfig;

        public IndexAnalysisService(IOptions<MonitoringConfig> options, ILogger<IndexAnalysisService> logger)
            : base(options, logger)
        {
            _indexConfig = options.Value.IndexAnalysis;
        }

        // ─────────────────────────────────────────────────────────
        // 1. FRAGMENTED INDEXES
        //    Uses sys.dm_db_index_physical_stats to check avg_fragmentation_in_percent.
        //    - >30% → REBUILD (ALTER INDEX ... REBUILD)
        //    - 10-30% → REORGANIZE (ALTER INDEX ... REORGANIZE)
        //    Only checks indexes with page count ≥ MinPageCount (small indexes are trivial).
        // ─────────────────────────────────────────────────────────
        public async Task<List<IndexAnalysisDto>> GetFragmentedIndexesAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();

            // sys.dm_db_index_physical_stats parameters:
            //   database_id, object_id, index_id, partition_number, mode
            //   NULL = all, 'LIMITED' mode is fast (samples leaf pages only)
            const string sql = @"
                SELECT 
                    s.name                          AS SchemaName,
                    o.name                          AS TableName,
                    i.name                          AS IndexName,
                    ps.avg_fragmentation_in_percent AS FragPct,
                    ps.page_count                   AS PageCount,
                    i.type_desc                     AS IndexType
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ps
                INNER JOIN sys.objects o  ON ps.object_id = o.object_id
                INNER JOIN sys.indexes i  ON ps.object_id = i.object_id AND ps.index_id = i.index_id
                INNER JOIN sys.schemas s  ON o.schema_id = s.schema_id
                WHERE ps.index_id > 0                           -- Skip heaps (handled separately)
                  AND ps.page_count >= @minPages                -- Skip tiny indexes
                  AND ps.avg_fragmentation_in_percent >= @reorgThreshold  -- At least worth reorganizing
                  AND o.type = 'U'                              -- User tables only
                ORDER BY ps.avg_fragmentation_in_percent DESC;
            ";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
                cmd.Parameters.AddWithValue("@minPages", _indexConfig.MinPageCount);
                cmd.Parameters.AddWithValue("@reorgThreshold", _indexConfig.ReorganizeThreshold);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var fragPct = SafeGet<double>(reader, "FragPct");
                    var schemaName = SafeGet<string>(reader, "SchemaName", "dbo");
                    var tableName = SafeGet<string>(reader, "TableName", "");
                    var indexName = SafeGet<string>(reader, "IndexName", "");
                    bool isRebuild = fragPct >= _indexConfig.FragmentationThreshold;

                    // Generate the appropriate maintenance script
                    string action = isRebuild ? "REBUILD" : "REORGANIZE";
                    string script = $"ALTER INDEX [{indexName}] ON [{schemaName}].[{tableName}] {action};";

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName        = database,
                        SchemaName          = schemaName,
                        TableName           = tableName,
                        IndexName           = indexName,
                        IssueType           = "Fragmented",
                        Suggestion          = action,
                        FragmentationPercent = fragPct,
                        PageCount           = SafeGet<long>(reader, "PageCount"),
                        ImpactScore         = fragPct,  // Higher fragmentation = higher impact
                        Script              = script,
                        AnalyzedAt          = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze fragmented indexes for {Database}", database);
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────
        // 2. MISSING INDEXES
        //    Uses the "missing index" DMVs:
        //    - sys.dm_db_missing_index_group_stats  (usage statistics)
        //    - sys.dm_db_missing_index_groups        (group-to-detail mapping)
        //    - sys.dm_db_missing_index_details       (equality, inequality, included columns)
        //    
        //    ImpactScore formula (official Microsoft formula):
        //      avg_total_user_cost * avg_user_impact * (user_seeks + user_scans)
        //    This approximates the cumulative benefit of creating the index.
        // ─────────────────────────────────────────────────────────
        public async Task<List<IndexAnalysisDto>> GetMissingIndexesAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();

            const string sql = @"
                SELECT 
                    s.name                          AS SchemaName,
                    o.name                          AS TableName,
                    d.equality_columns              AS EqualityColumns,
                    d.inequality_columns            AS InequalityColumns,
                    d.included_columns              AS IncludedColumns,
                    -- Microsoft's recommended impact formula
                    gs.avg_total_user_cost 
                        * gs.avg_user_impact 
                        * (gs.user_seeks + gs.user_scans) AS ImpactScore,
                    gs.user_seeks                   AS UserSeeks,
                    gs.user_scans                   AS UserScans,
                    gs.last_user_seek               AS LastSeek
                FROM sys.dm_db_missing_index_group_stats gs
                INNER JOIN sys.dm_db_missing_index_groups g 
                    ON gs.group_handle = g.index_group_handle
                INNER JOIN sys.dm_db_missing_index_details d 
                    ON g.index_handle = d.index_handle
                INNER JOIN sys.objects o  ON d.object_id = o.object_id
                INNER JOIN sys.schemas s  ON o.schema_id = s.schema_id
                WHERE d.database_id = DB_ID()
                  AND gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans) >= @minImpact
                ORDER BY ImpactScore DESC;
            ";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@minImpact", _indexConfig.MissingIndexMinImpact);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var schema    = SafeGet<string>(reader, "SchemaName", "dbo");
                    var table     = SafeGet<string>(reader, "TableName", "");
                    var eqCols    = SafeGet<string>(reader, "EqualityColumns", "");
                    var ineqCols  = SafeGet<string>(reader, "InequalityColumns", "");
                    var inclCols  = SafeGet<string>(reader, "IncludedColumns", "");
                    var impact    = SafeGet<double>(reader, "ImpactScore");

                    // Build CREATE INDEX script
                    var keyCols = string.Join(", ",
                        new[] { eqCols, ineqCols }.Where(c => !string.IsNullOrWhiteSpace(c)));
                    var indexName = $"IX_Missing_{table}_{Guid.NewGuid().ToString("N")[..8]}";
                    var script = $"CREATE NONCLUSTERED INDEX [{indexName}] ON [{schema}].[{table}] ({keyCols})";
                    if (!string.IsNullOrWhiteSpace(inclCols))
                        script += $" INCLUDE ({inclCols})";
                    script += ";";

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = table,
                        IndexName    = indexName,
                        IssueType    = "Missing",
                        Suggestion   = "CREATE INDEX",
                        ImpactScore  = impact,
                        Script       = script,
                        AnalyzedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze missing indexes for {Database}", database);
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────
        // 3. UNUSED INDEXES
        //    Uses sys.dm_db_index_usage_stats to find indexes with
        //    zero seeks + scans + lookups since SQL restart.
        //    These indexes consume disk space and slow down INSERT/UPDATE/DELETE
        //    without providing any read benefit.
        //    We exclude primary keys and unique constraints (business logic).
        // ─────────────────────────────────────────────────────────
        public async Task<List<IndexAnalysisDto>> GetUnusedIndexesAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();

            // Check SQL Server uptime to ensure stats are meaningful
            const string sql = @"
                -- Only flag indexes if SQL has been running long enough
                DECLARE @uptimeDays INT = DATEDIFF(DAY, 
                    (SELECT sqlserver_start_time FROM sys.dm_os_sys_info), GETDATE());

                IF @uptimeDays >= @minDays
                BEGIN
                    SELECT 
                        s.name                       AS SchemaName,
                        o.name                       AS TableName,
                        i.name                       AS IndexName,
                        i.type_desc                  AS IndexType,
                        ISNULL(u.user_seeks, 0)      AS UserSeeks,
                        ISNULL(u.user_scans, 0)      AS UserScans,
                        ISNULL(u.user_lookups, 0)    AS UserLookups,
                        ISNULL(u.user_updates, 0)    AS UserUpdates,
                        p.rows                       AS RowCount,
                        -- Calculate total space used by this index
                        (SUM(a.total_pages) * 8 / 1024.0) AS SizeMb
                    FROM sys.indexes i
                    INNER JOIN sys.objects o    ON i.object_id = o.object_id
                    INNER JOIN sys.schemas s    ON o.schema_id = s.schema_id
                    LEFT  JOIN sys.dm_db_index_usage_stats u 
                        ON i.object_id = u.object_id AND i.index_id = u.index_id AND u.database_id = DB_ID()
                    INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
                    INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
                    WHERE o.type = 'U'                          -- User tables
                      AND i.index_id > 1                        -- Skip clustered index (index_id=1) and heap (0)
                      AND i.is_primary_key = 0                  -- Don't suggest dropping PKs
                      AND i.is_unique_constraint = 0            -- Don't drop unique constraints
                      AND i.is_unique = 0                       -- Don't drop unique indexes
                      AND ISNULL(u.user_seeks, 0) + ISNULL(u.user_scans, 0) + ISNULL(u.user_lookups, 0) = 0
                    GROUP BY s.name, o.name, i.name, i.type_desc, u.user_seeks, u.user_scans, u.user_lookups, u.user_updates, p.rows
                    HAVING SUM(a.total_pages) >= @minPages
                    ORDER BY SizeMb DESC;
                END
            ";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@minDays", _indexConfig.UnusedIndexDays);
                cmd.Parameters.AddWithValue("@minPages", _indexConfig.MinPageCount);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var schema = SafeGet<string>(reader, "SchemaName", "dbo");
                    var table  = SafeGet<string>(reader, "TableName", "");
                    var index  = SafeGet<string>(reader, "IndexName", "");

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = table,
                        IndexName    = index,
                        IssueType    = "Unused",
                        Suggestion   = "DROP INDEX",
                        ImpactScore  = SafeGet<double>(reader, "SizeMb"), // Larger = more waste
                        Script       = $"DROP INDEX [{index}] ON [{schema}].[{table}];",
                        AnalyzedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze unused indexes for {Database}", database);
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────
        // 4. DUPLICATE INDEXES
        //    Two indexes are considered duplicates if they have the exact
        //    same set of key columns in the same order on the same table.
        //    We join sys.indexes → sys.index_columns to build a column list,
        //    then GROUP BY to find duplicates.
        //    The smaller/newer index should be dropped.
        // ─────────────────────────────────────────────────────────
        public async Task<List<IndexAnalysisDto>> GetDuplicateIndexesAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();

            const string sql = @"
                ;WITH IndexColumns AS (
                    SELECT 
                        i.object_id,
                        i.index_id,
                        i.name AS IndexName,
                        i.type_desc,
                        i.is_primary_key,
                        -- Build a deterministic column list string for comparison
                        STRING_AGG(
                            CAST(COL_NAME(ic.object_id, ic.column_id) AS NVARCHAR(128)), ','
                        ) WITHIN GROUP (ORDER BY ic.key_ordinal) AS KeyColumns,
                        STRING_AGG(
                            CASE WHEN ic.is_included_column = 1 
                                 THEN CAST(COL_NAME(ic.object_id, ic.column_id) AS NVARCHAR(128))
                            END, ','
                        ) WITHIN GROUP (ORDER BY ic.key_ordinal) AS IncludedColumns
                    FROM sys.indexes i
                    INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    INNER JOIN sys.objects o ON i.object_id = o.object_id
                    WHERE o.type = 'U' AND i.index_id > 0
                    GROUP BY i.object_id, i.index_id, i.name, i.type_desc, i.is_primary_key
                )
                SELECT 
                    s.name           AS SchemaName,
                    o.name           AS TableName,
                    a.IndexName      AS IndexName1,
                    b.IndexName      AS IndexName2,
                    a.KeyColumns     AS KeyColumns,
                    a.type_desc      AS IndexType
                FROM IndexColumns a
                INNER JOIN IndexColumns b 
                    ON a.object_id = b.object_id 
                   AND a.index_id < b.index_id 
                   AND a.KeyColumns = b.KeyColumns
                   AND ISNULL(a.IncludedColumns, '') = ISNULL(b.IncludedColumns, '')
                INNER JOIN sys.objects o ON a.object_id = o.object_id
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                ORDER BY o.name, a.IndexName;
            ";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var schema = SafeGet<string>(reader, "SchemaName", "dbo");
                    var table  = SafeGet<string>(reader, "TableName", "");
                    var idx1   = SafeGet<string>(reader, "IndexName1", "");
                    var idx2   = SafeGet<string>(reader, "IndexName2", "");

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = table,
                        IndexName    = $"{idx1} ↔ {idx2}",
                        IssueType    = "Duplicate",
                        Suggestion   = "REMOVE DUPLICATE",
                        ImpactScore  = 75, // Duplicates always waste space and slow writes
                        Script       = $"-- Review and drop the redundant index:\nDROP INDEX [{idx2}] ON [{schema}].[{table}];",
                        AnalyzedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze duplicate indexes for {Database}", database);
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────
        // 5. HEAP TABLES (no clustered index)
        //    Heaps have index_id = 0 in sys.indexes. They cause:
        //    - Forwarded records (fragmentation without an index to fragment)
        //    - Full table scans for every query
        //    - Poor page locality
        //    We suggest adding a clustered index on the identity/PK column.
        // ─────────────────────────────────────────────────────────
        public async Task<List<IndexAnalysisDto>> GetHeapTablesAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();

            const string sql = @"
                SELECT 
                    s.name            AS SchemaName,
                    t.name            AS TableName,
                    p.rows            AS RowCount,
                    -- Calculate heap size from partition stats
                    SUM(a.total_pages) * 8 / 1024.0 AS SizeMb,
                    -- Try to find an identity or first column to suggest for clustering
                    (SELECT TOP 1 c.name 
                     FROM sys.columns c 
                     WHERE c.object_id = t.object_id 
                       AND c.is_identity = 1) AS IdentityColumn,
                    (SELECT TOP 1 c.name 
                     FROM sys.columns c 
                     WHERE c.object_id = t.object_id 
                     ORDER BY c.column_id) AS FirstColumn
                FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                INNER JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id = 0  -- Heap
                INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
                WHERE t.type = 'U'
                  AND p.rows > 0    -- Skip empty tables
                GROUP BY s.name, t.name, t.object_id, p.rows
                HAVING SUM(a.total_pages) >= @minPages
                ORDER BY SizeMb DESC;
            ";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@minPages", _indexConfig.MinPageCount);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var schema       = SafeGet<string>(reader, "SchemaName", "dbo");
                    var table        = SafeGet<string>(reader, "TableName", "");
                    var identityCol  = SafeGet<string>(reader, "IdentityColumn", "");
                    var firstCol     = SafeGet<string>(reader, "FirstColumn", "Id");

                    // Prefer identity column; fall back to first column
                    var clusterCol = !string.IsNullOrWhiteSpace(identityCol) ? identityCol : firstCol;

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName   = schema,
                        TableName    = table,
                        IndexName    = "(HEAP)",
                        IssueType    = "Heap",
                        Suggestion   = "ADD CLUSTERED INDEX",
                        ImpactScore  = SafeGet<double>(reader, "SizeMb"),
                        Script       = $"CREATE CLUSTERED INDEX [CIX_{table}] ON [{schema}].[{table}] ([{clusterCol}]);",
                        AnalyzedAt   = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect heap tables for {Database}", database);
            }

            return results;
        }

        /// <summary>
        /// Detects stale statistics on indexes and columns.
        /// Stats are stale if rows have changed significantly since last update.
        /// </summary>
        public async Task<List<IndexAnalysisDto>> GetStaleStatisticsAsync(string database, CancellationToken ct = default)
        {
            var results = new List<IndexAnalysisDto>();
            const string sql = @"
                SELECT 
                    s.name AS SchemaName,
                    o.name AS TableName,
                    st.name AS StatName,
                    sp.last_updated AS LastUpdated,
                    sp.rows_sampled AS RowsSampled,
                    sp.modification_counter AS ModCount,
                    p.rows AS TotalRows
                FROM sys.stats AS st
                CROSS APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) AS sp
                JOIN sys.objects AS o ON st.object_id = o.object_id
                JOIN sys.schemas AS s ON o.schema_id = s.schema_id
                JOIN (SELECT object_id, SUM(rows) AS rows FROM sys.partitions WHERE index_id <= 1 GROUP BY object_id) p ON o.object_id = p.object_id
                WHERE o.type = 'U'
                  AND (sp.modification_counter > (p.rows * 0.1) OR sp.last_updated < DATEADD(DAY, -7, GETDATE()))
                ORDER BY sp.modification_counter DESC;";

            try
            {
                await using var conn = await OpenConnectionToDbAsync(database, ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var table = SafeGet<string>(reader, "TableName");
                    var schema = SafeGet<string>(reader, "SchemaName");
                    var stat = SafeGet<string>(reader, "StatName");

                    results.Add(new IndexAnalysisDto
                    {
                        DatabaseName = database,
                        SchemaName = schema,
                        TableName = table,
                        IndexName = stat,
                        IssueType = "StaleStats",
                        Suggestion = "UPDATE STATISTICS",
                        ImpactScore = SafeGet<long>(reader, "ModCount") / 1000.0,
                        Script = $"UPDATE STATISTICS [{schema}].[{table}] ([{stat}]);",
                        AnalyzedAt = DateTime.UtcNow
                    });
                }
            }
            catch { }
            return results;
        }
    }
}
