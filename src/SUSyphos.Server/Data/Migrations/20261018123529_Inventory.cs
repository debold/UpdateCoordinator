using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InventoryAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsDisplayVersion",
                table: "Clients",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsEditionId",
                table: "Clients",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OsProductName",
                table: "Clients",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Roles",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "UsesWsus",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WsusServer",
                table: "Clients",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Updates",
                columns: table => new
                {
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    KbArticle = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Classification = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Updates", x => new { x.UpdateId, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdates",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdates", x => new { x.ClientId, x.UpdateId, x.Revision });
                    table.ForeignKey(
                        name: "FK_ClientUpdates_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientUpdates_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdates_UpdateId_Revision",
                table: "ClientUpdates",
                columns: new[] { "UpdateId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_Updates_KbArticle",
                table: "Updates",
                column: "KbArticle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientUpdates");

            migrationBuilder.DropTable(
                name: "Updates");

            migrationBuilder.DropColumn(
                name: "InventoryAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OsDisplayVersion",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OsEditionId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OsProductName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "Roles",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UsesWsus",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "WsusServer",
                table: "Clients");
        }
    }
}
