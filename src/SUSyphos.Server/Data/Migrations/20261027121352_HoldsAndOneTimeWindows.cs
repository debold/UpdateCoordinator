using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldsAndOneTimeWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OneTimeWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RingId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneTimeWindows", x => x.Id);
                    table.CheckConstraint("CK_OneTimeWindows_AtMostOneTarget", "(\"RingId\" IS NOT NULL) + (\"GroupId\" IS NOT NULL) + (\"ClientId\" IS NOT NULL) <= 1");
                    table.CheckConstraint("CK_OneTimeWindows_EndAfterStart", "\"EndUtc\" > \"StartUtc\"");
                    table.ForeignKey(
                        name: "FK_OneTimeWindows_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OneTimeWindows_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OneTimeWindows_Rings_RingId",
                        column: x => x.RingId,
                        principalTable: "Rings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UpdateHolds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    RingId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateHolds", x => x.Id);
                    table.CheckConstraint("CK_UpdateHolds_OneTarget", "(\"RingId\" IS NOT NULL) + (\"GroupId\" IS NOT NULL) + (\"ClientId\" IS NOT NULL) = 1");
                    table.ForeignKey(
                        name: "FK_UpdateHolds_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpdateHolds_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UpdateHolds_Rings_RingId",
                        column: x => x.RingId,
                        principalTable: "Rings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeWindows_ClientId",
                table: "OneTimeWindows",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeWindows_EndUtc",
                table: "OneTimeWindows",
                column: "EndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeWindows_GroupId",
                table: "OneTimeWindows",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_OneTimeWindows_RingId",
                table: "OneTimeWindows",
                column: "RingId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateHolds_ClientId",
                table: "UpdateHolds",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateHolds_GroupId",
                table: "UpdateHolds",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateHolds_RingId",
                table: "UpdateHolds",
                column: "RingId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateHolds_UpdateId_Revision",
                table: "UpdateHolds",
                columns: new[] { "UpdateId", "Revision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OneTimeWindows");

            migrationBuilder.DropTable(
                name: "UpdateHolds");
        }
    }
}
