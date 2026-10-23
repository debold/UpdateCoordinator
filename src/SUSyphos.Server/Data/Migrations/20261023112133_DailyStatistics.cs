using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FullyPatched",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenHeld",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenReleased",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "ComplianceSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusFailed",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusOffline",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusOk",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusRebootPending",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusUpdating",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusWarning",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullyPatched",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "OpenHeld",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "OpenReleased",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusFailed",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusOffline",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusOk",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusRebootPending",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusUpdating",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "StatusWarning",
                table: "ComplianceSnapshots");
        }
    }
}
