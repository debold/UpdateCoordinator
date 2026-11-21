using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc7DebugAcknowledgeAllServersPowerShell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PackageAssignments_Target",
                table: "PackageAssignments");

            migrationBuilder.AddColumn<int>(
                name: "DraftPowerShell",
                table: "PackageScripts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PowerShell",
                table: "PackageScriptRevisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DebugBy",
                table: "Clients",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DebugUntilUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttentionAcknowledgements",
                columns: table => new
                {
                    Topic = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AffectedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttentionAcknowledgements", x => x.Topic);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PackageAssignments_Target",
                table: "PackageAssignments",
                sql: "(\"TargetKind\" = 0 AND \"GroupId\" IS NOT NULL AND \"ClientId\" IS NULL) OR (\"TargetKind\" = 1 AND \"ClientId\" IS NOT NULL AND \"GroupId\" IS NULL) OR (\"TargetKind\" = 2 AND \"GroupId\" IS NULL AND \"ClientId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttentionAcknowledgements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PackageAssignments_Target",
                table: "PackageAssignments");

            migrationBuilder.DropColumn(
                name: "DraftPowerShell",
                table: "PackageScripts");

            migrationBuilder.DropColumn(
                name: "PowerShell",
                table: "PackageScriptRevisions");

            migrationBuilder.DropColumn(
                name: "DebugBy",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "DebugUntilUtc",
                table: "Clients");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PackageAssignments_Target",
                table: "PackageAssignments",
                sql: "(\"TargetKind\" = 0 AND \"GroupId\" IS NOT NULL AND \"ClientId\" IS NULL) OR (\"TargetKind\" = 1 AND \"ClientId\" IS NOT NULL AND \"GroupId\" IS NULL)");
        }
    }
}
