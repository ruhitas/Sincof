using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringAgent.Web.Migrations
{
    /// <inheritdoc />
    public partial class UpdateSchemaForPayloadFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Recommendation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AlertId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RootCause = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImpactScore = table.Column<double>(type: "float", nullable: false),
                    CurrentValue = table.Column<double>(type: "float", nullable: false),
                    ThresholdValue = table.Column<double>(type: "float", nullable: false),
                    Metric = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlockingEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BlockedSpid = table.Column<int>(type: "int", nullable: false),
                    BlockingSpid = table.Column<int>(type: "int", nullable: false),
                    BlockedQuery = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BlockingQuery = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WaitType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WaitTimeMs = table.Column<int>(type: "int", nullable: false),
                    BlockedResource = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EventTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockingEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseSizeMb = table.Column<double>(type: "float", nullable: false),
                    LogSizeMb = table.Column<double>(type: "float", nullable: false),
                    AvgReadLatencyMs = table.Column<double>(type: "float", nullable: false),
                    AvgWriteLatencyMs = table.Column<double>(type: "float", nullable: false),
                    ActiveSessions = table.Column<int>(type: "int", nullable: false),
                    IoReadBytes = table.Column<long>(type: "bigint", nullable: false),
                    IoWriteBytes = table.Column<long>(type: "bigint", nullable: false),
                    IoReadLatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    IoWriteLatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    DataFileSpaceFreeMb = table.Column<double>(type: "float", nullable: false),
                    LogFileSpaceFreeMb = table.Column<double>(type: "float", nullable: false),
                    VlfCount = table.Column<int>(type: "int", nullable: false),
                    IsAutoShrinkOn = table.Column<bool>(type: "bit", nullable: false),
                    IsAutoCloseOn = table.Column<bool>(type: "bit", nullable: false),
                    IsTrustworthyOn = table.Column<bool>(type: "bit", nullable: false),
                    HasPercentGrowth = table.Column<bool>(type: "bit", nullable: false),
                    StateDesc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DaysSinceLastBackup = table.Column<int>(type: "int", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadlockEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeadlockGraphXml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VictimsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EventTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadlockEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexIssues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IndexName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Suggestion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FragmentationPercent = table.Column<double>(type: "float", nullable: false),
                    PageCount = table.Column<long>(type: "bigint", nullable: false),
                    IssueType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ImpactScore = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Script = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstanceMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CpuUsagePercent = table.Column<double>(type: "float", nullable: false),
                    SqlCpuUsagePercent = table.Column<double>(type: "float", nullable: false),
                    MemoryUsageMb = table.Column<double>(type: "float", nullable: false),
                    MemoryTargetMb = table.Column<double>(type: "float", nullable: false),
                    SqlMemoryUsageMb = table.Column<double>(type: "float", nullable: false),
                    PLE = table.Column<long>(type: "bigint", nullable: false),
                    BufferCacheHitRatio = table.Column<double>(type: "float", nullable: false),
                    BatchRequestsPerSec = table.Column<long>(type: "bigint", nullable: false),
                    TotalConnections = table.Column<long>(type: "bigint", nullable: false),
                    TempDbSizeMb = table.Column<double>(type: "float", nullable: false),
                    TempDbLogSizeMb = table.Column<double>(type: "float", nullable: false),
                    FailedLoginsLastMinute = table.Column<int>(type: "int", nullable: false),
                    MaxDop = table.Column<int>(type: "int", nullable: false),
                    CostThresholdForParallelism = table.Column<int>(type: "int", nullable: false),
                    SysAdminCount = table.Column<int>(type: "int", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Queries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DatabaseName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueryHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QueryPlan = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExecutionCount = table.Column<long>(type: "bigint", nullable: false),
                    AvgCpuTimeUs = table.Column<double>(type: "float", nullable: false),
                    TotalWorkerTimeUs = table.Column<long>(type: "bigint", nullable: false),
                    TotalLogicalReads = table.Column<long>(type: "bigint", nullable: false),
                    TotalPhysicalReads = table.Column<long>(type: "bigint", nullable: false),
                    TotalLogicalWrites = table.Column<long>(type: "bigint", nullable: false),
                    TotalElapsedTimeUs = table.Column<long>(type: "bigint", nullable: false),
                    AvgElapsedTimeUs = table.Column<double>(type: "float", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TargetServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerConfigId = table.Column<int>(type: "int", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProviderType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectionString = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IncludeDBs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkerId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IncludeDBs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExcludeDBs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IntervalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FeatureTogglesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThresholdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApiSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IndexAnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AdvancedControlsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LoggingJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WaitStats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstanceMetricRecordId = table.Column<int>(type: "int", nullable: false),
                    WaitType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WaitTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    WaitCount = table.Column<long>(type: "bigint", nullable: false),
                    MaxWaitTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    SignalWaitTimeMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaitStats_InstanceMetrics_InstanceMetricRecordId",
                        column: x => x.InstanceMetricRecordId,
                        principalTable: "InstanceMetrics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaitStats_InstanceMetricRecordId",
                table: "WaitStats",
                column: "InstanceMetricRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "BlockingEvents");

            migrationBuilder.DropTable(
                name: "DatabaseMetrics");

            migrationBuilder.DropTable(
                name: "DeadlockEvents");

            migrationBuilder.DropTable(
                name: "IndexIssues");

            migrationBuilder.DropTable(
                name: "Payloads");

            migrationBuilder.DropTable(
                name: "Queries");

            migrationBuilder.DropTable(
                name: "TargetServers");

            migrationBuilder.DropTable(
                name: "WaitStats");

            migrationBuilder.DropTable(
                name: "WorkerConfigs");

            migrationBuilder.DropTable(
                name: "InstanceMetrics");
        }
    }
}
