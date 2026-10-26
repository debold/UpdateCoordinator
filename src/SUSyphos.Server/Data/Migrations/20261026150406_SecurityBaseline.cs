using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaselineCurrent",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BaselineKnown",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenExploited",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenVulnerabilities",
                table: "ComplianceSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Azure",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AzureArc",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsuKey",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SecurityBaselines",
                columns: table => new
                {
                    Build = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Kb = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityBaselines", x => new { x.Build, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "SecurityFixes",
                columns: table => new
                {
                    Cve = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Month = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Build = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Kb = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityFixes", x => new { x.Cve, x.Build, x.Month });
                });

            migrationBuilder.CreateTable(
                name: "SecurityVulnerabilities",
                columns: table => new
                {
                    Cve = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Month = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Impact = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CvssScore = table.Column<double>(type: "REAL", nullable: true),
                    Exploited = table.Column<bool>(type: "INTEGER", nullable: false),
                    PubliclyDisclosed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityVulnerabilities", x => new { x.Cve, x.Month });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFixes_Build_Revision",
                table: "SecurityFixes",
                columns: new[] { "Build", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityFixes_Kb",
                table: "SecurityFixes",
                column: "Kb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityBaselines");

            migrationBuilder.DropTable(
                name: "SecurityFixes");

            migrationBuilder.DropTable(
                name: "SecurityVulnerabilities");

            migrationBuilder.DropColumn(
                name: "BaselineCurrent",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "BaselineKnown",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "OpenExploited",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "OpenVulnerabilities",
                table: "ComplianceSnapshots");

            migrationBuilder.DropColumn(
                name: "Azure",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "AzureArc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "EsuKey",
                table: "Clients");
        }
    }
}
