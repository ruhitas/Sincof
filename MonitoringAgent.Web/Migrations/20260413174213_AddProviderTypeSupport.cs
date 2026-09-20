using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitoringAgent.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderTypeSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "Queries",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "DatabaseIoPercent",
                table: "InstanceMetrics",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "LogWritePercent",
                table: "InstanceMetrics",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "InstanceMetrics",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "IndexIssues",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "DeadlockEvents",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "DatabaseMetrics",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "BlockingEvents",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "Alerts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "DatabaseIoPercent",
                table: "InstanceMetrics");

            migrationBuilder.DropColumn(
                name: "LogWritePercent",
                table: "InstanceMetrics");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "InstanceMetrics");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "IndexIssues");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "DeadlockEvents");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "DatabaseMetrics");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "BlockingEvents");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "Alerts");
        }
    }
}
