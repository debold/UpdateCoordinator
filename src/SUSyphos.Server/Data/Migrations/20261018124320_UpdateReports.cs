using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaxDownloadSizeBytes",
                table: "Updates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MsrcSeverity",
                table: "Updates",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RebootBehavior",
                table: "Updates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthChecks",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSearchAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastSearchHResult",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastSearchSucceeded",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReleasesVersion",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ClientAvailableUpdates",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAvailableUpdates", x => new { x.ClientId, x.UpdateId, x.Revision });
                    table.ForeignKey(
                        name: "FK_ClientAvailableUpdates_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientAvailableUpdates_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    EarliestInstallAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => new { x.ClientId, x.UpdateId, x.Revision });
                    table.ForeignKey(
                        name: "FK_Releases_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Releases_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAvailableUpdates_UpdateId_Revision",
                table: "ClientAvailableUpdates",
                columns: new[] { "UpdateId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_Releases_UpdateId_Revision",
                table: "Releases",
                columns: new[] { "UpdateId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientAvailableUpdates");

            migrationBuilder.DropTable(
                name: "Releases");

            migrationBuilder.DropColumn(
                name: "MaxDownloadSizeBytes",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "MsrcSeverity",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "RebootBehavior",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "HealthChecks",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "LastSearchAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "LastSearchHResult",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "LastSearchSucceeded",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ReleasesVersion",
                table: "Clients");
        }
    }
}
