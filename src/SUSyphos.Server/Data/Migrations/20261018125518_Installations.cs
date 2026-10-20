using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Installations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastInstallAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RebootPending",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCode = table.Column<int>(type: "INTEGER", nullable: false),
                    HResult = table.Column<int>(type: "INTEGER", nullable: false),
                    RebootRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Installations_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Installations_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_ClientId_RunId_UpdateId_Revision",
                table: "Installations",
                columns: new[] { "ClientId", "RunId", "UpdateId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Installations_UpdateId_Revision",
                table: "Installations",
                columns: new[] { "UpdateId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Installations");

            migrationBuilder.DropColumn(
                name: "LastInstallAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RebootPending",
                table: "Clients");
        }
    }
}
