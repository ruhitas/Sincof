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
    // EVENT LISTENER SERVICE — Blocking chains & Deadlock detection
    // ═══════════════════════════════════════════════════════════════
    public class EventListenerService : SqlServiceBase, IEventListener
    {
        private readonly AlertThresholds _thresholds;

        public EventListenerService(IOptions<MonitoringConfig> options, ILogger<EventListenerService> logger)
            : base(options, logger)
        {
            _thresholds = options.Value.AlertThresholds;
        }

        // ─────────────────────────────────────────────────────────
        // BLOCKING SESSIONS
        //   Uses sys.dm_exec_requests to find sessions that are blocked
        //   (blocking_session_id > 0) and have been waiting longer than threshold.
        //   We join to sys.dm_exec_connections + sys.dm_exec_sql_text
        //   to get the actual SQL text of both blocker and blocked.
        // ─────────────────────────────────────────────────────────
        public async Task<List<BlockingEventDto>> GetBlockingSessionsAsync(CancellationToken ct = default)
        {
            var results = new List<BlockingEventDto>();

            const string sql = @"
                SELECT 
                    DB_NAME(r.database_id)           AS DatabaseName,
                    r.session_id                     AS BlockedSpid,
                    r.blocking_session_id            AS BlockingSpid,
                    r.wait_time                      AS WaitTimeMs,
                    r.wait_type                      AS WaitType,
                    r.wait_resource                  AS BlockedResource,
                    -- Get the SQL text of the BLOCKED session
                    SUBSTRING(txt_blocked.text, 
                        (r.statement_start_offset / 2) + 1,
                        ((CASE r.statement_end_offset 
                            WHEN -1 THEN DATALENGTH(txt_blocked.text) 
                            ELSE r.statement_end_offset 
                          END - r.statement_start_offset) / 2) + 1
                    ) AS BlockedQuery,
                    -- Get the SQL text of the BLOCKING session (head of chain)
                    txt_blocking.text                AS BlockingQuery
                FROM sys.dm_exec_requests r
                -- CROSS APPLY to get the blocked session's current SQL statement
                CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) txt_blocked
                -- JOIN to the blocking session's connection to get its most recent SQL
                LEFT JOIN sys.dm_exec_connections c 
                    ON r.blocking_session_id = c.session_id
                OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) txt_blocking
                WHERE r.blocking_session_id > 0
                  AND r.wait_time >= @thresholdMs
                ORDER BY r.wait_time DESC;
            ";

            try
            {
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
                cmd.Parameters.AddWithValue("@thresholdMs", _thresholds.BlockingWaitTimeSeconds * 1000);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    results.Add(new BlockingEventDto
                    {
                        DatabaseName    = SafeGet<string>(reader, "DatabaseName", ""),
                        BlockedSpid     = SafeGet<int>(reader, "BlockedSpid"),
                        BlockingSpid    = SafeGet<int>(reader, "BlockingSpid"),
                        WaitTimeMs      = SafeGet<int>(reader, "WaitTimeMs"),
                        WaitType        = SafeGet<string>(reader, "WaitType", ""),
                        BlockedResource = SafeGet<string>(reader, "BlockedResource", ""),
                        BlockedQuery    = SafeGet<string>(reader, "BlockedQuery", ""),
                        BlockingQuery   = SafeGet<string>(reader, "BlockingQuery", ""),
                        EventTime       = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect blocking sessions");
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────
        // DEADLOCK DETECTION
        //   Uses the system_health Extended Events session (always-on in SQL 2008+).
        //   The deadlock graph is stored as XML in the ring buffer.
        //   We parse the XML to extract victim SPIDs and their queries.
        //
        //   Alternative approach: sys.dm_tran_locks can detect potential
        //   deadlocks by finding circular wait chains, but Extended Events
        //   provides the actual deadlock graph after the fact.
        // ─────────────────────────────────────────────────────────
        public async Task<List<DeadlockEventDto>> GetDeadlockEventsAsync(CancellationToken ct = default)
        {
            var results = new List<DeadlockEventDto>();

            // Read deadlock graphs from the system_health XE session ring buffer.
            // The ring buffer stores the last ~5 MB of events.
            // We filter for 'xml_deadlock_report' events from the last hour.
            const string sql = @"
                ;WITH DeadlockEvents AS (
                    SELECT 
                        CAST(xet.target_data AS XML) AS TargetData
                    FROM sys.dm_xe_session_targets xet
                    INNER JOIN sys.dm_xe_sessions xes 
                        ON xes.address = xet.event_session_address
                    WHERE xes.name = 'system_health'
                      AND xet.target_name = 'ring_buffer'
                )
                SELECT 
                    xevents.event_data.value('(@timestamp)[1]', 'DATETIME2') AS EventTime,
                    xevents.event_data.query('(data[@name=""xml_report""]/value/deadlock)[1]').value('.', 'NVARCHAR(MAX)') AS DeadlockGraph
                FROM DeadlockEvents
                CROSS APPLY TargetData.nodes('RingBufferTarget/event[@name=""xml_deadlock_report""]') AS xevents(event_data)
                WHERE xevents.event_data.value('(@timestamp)[1]', 'DATETIME2') >= DATEADD(HOUR, -1, GETUTCDATE())
                ORDER BY EventTime DESC;
            ";

            try
            {
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var deadlockGraph = SafeGet<string>(reader, "DeadlockGraph", "");
                    var eventTime = reader.IsDBNull(reader.GetOrdinal("EventTime"))
                        ? DateTime.UtcNow
                        : reader.GetDateTime(reader.GetOrdinal("EventTime"));

                    results.Add(new DeadlockEventDto
                    {
                        DeadlockGraphXml = deadlockGraph,
                        EventTime = eventTime
                    });
                }
            }
            catch (Exception ex)
            {
                // Extended Events query may fail on older SQL versions or limited permissions.
                // Fall back to sys.dm_tran_locks based detection.
                _logger.LogWarning(ex, "Extended Events deadlock query failed, attempting fallback");
                results.AddRange(await GetDeadlocksFallbackAsync(ct));
            }

            return results;
        }

        /// <summary>
        /// Fallback deadlock detection using sys.dm_tran_locks.
        /// Detects POTENTIAL deadlocks by finding sessions that hold locks
        /// and are simultaneously waiting for locks held by other sessions (circular wait).
        /// Note: This is less reliable than Extended Events but works without XE permissions.
        /// </summary>
        private async Task<List<DeadlockEventDto>> GetDeadlocksFallbackAsync(CancellationToken ct)
        {
            var results = new List<DeadlockEventDto>();

            const string sql = @"
                SELECT 
                    DB_NAME(l1.resource_database_id) AS DatabaseName,
                    l1.request_session_id AS Spid1,
                    l2.request_session_id AS Spid2,
                    l1.resource_type      AS ResourceType,
                    l1.request_mode       AS LockMode1,
                    l2.request_mode       AS LockMode2
                FROM sys.dm_tran_locks l1
                INNER JOIN sys.dm_tran_locks l2
                    ON l1.resource_associated_entity_id = l2.resource_associated_entity_id
                   AND l1.request_session_id <> l2.request_session_id
                WHERE l1.request_status = 'WAIT'
                  AND l2.request_status = 'GRANT'
                ORDER BY l1.resource_database_id;
            ";

            try
            {
                await using var conn = await OpenConnectionAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    results.Add(new DeadlockEventDto
                    {
                        DatabaseName = SafeGet<string>(reader, "DatabaseName", ""),
                        DeadlockGraphXml = $"<lock_conflict spid1='{SafeGet<int>(reader, "Spid1")}' spid2='{SafeGet<int>(reader, "Spid2")}' mode1='{SafeGet<string>(reader, "LockMode1", "")}' mode2='{SafeGet<string>(reader, "LockMode2", "")}' />",
                        EventTime = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallback deadlock detection also failed");
            }

            return results;
        }
    }
}
