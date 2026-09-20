using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringAgent.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddDiskTestLogTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiskTestLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ServerName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TestPath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IOPS = table.Column<double>(type: "float", nullable: false),
                    MBps = table.Column<double>(type: "float", nullable: false),
                    LatencyMs = table.Column<double>(type: "float", nullable: false),
                    CpuUsagePercent = table.Column<double>(type: "float", nullable: false),
                    FileSizeGB = table.Column<int>(type: "int", nullable: false),
                    DurationSec = table.Column<int>(type: "int", nullable: false),
                    WritePercent = table.Column<int>(type: "int", nullable: false),
                    BlockSizeKB = table.Column<int>(type: "int", nullable: false),
                    Threads = table.Column<int>(type: "int", nullable: false),
                    QueueDepth = table.Column<int>(type: "int", nullable: false),
                    RawOutput = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TestTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiskTestLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiskTestLogs");
        }
    }
}
