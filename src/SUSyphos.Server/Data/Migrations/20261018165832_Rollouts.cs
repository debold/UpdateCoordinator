using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rollouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RingTransitions",
                columns: table => new
                {
                    FromRingId = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumWait = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    MaximumWait = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    MinimumFeedbackPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumSuccessPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ManualGate = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RingTransitions", x => x.FromRingId);
                    table.ForeignKey(
                        name: "FK_RingTransitions_Rings_FromRingId",
                        column: x => x.FromRingId,
                        principalTable: "Rings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rollouts",
                columns: table => new
                {
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Override = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rollouts", x => new { x.UpdateId, x.Revision });
                    table.ForeignKey(
                        name: "FK_Rollouts_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolloutRings",
                columns: table => new
                {
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    RingId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StateChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutRings", x => new { x.UpdateId, x.Revision, x.RingId });
                    table.ForeignKey(
                        name: "FK_RolloutRings_Rings_RingId",
                        column: x => x.RingId,
                        principalTable: "Rings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolloutRings_Rollouts_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Rollouts",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RingTransitions",
                columns: new[] { "FromRingId", "ManualGate", "MaximumWait", "MinimumFeedbackPercent", "MinimumSuccessPercent", "MinimumWait" },
                values: new object[,]
                {
                    { 1, false, new TimeSpan(7, 0, 0, 0, 0), 80, 95, new TimeSpan(2, 0, 0, 0, 0) },
                    { 2, true, new TimeSpan(10, 0, 0, 0, 0), 80, 95, new TimeSpan(3, 0, 0, 0, 0) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolloutRings_RingId",
                table: "RolloutRings",
                column: "RingId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutRings_State",
                table: "RolloutRings",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RingTransitions");

            migrationBuilder.DropTable(
                name: "RolloutRings");

            migrationBuilder.DropTable(
                name: "Rollouts");
        }
    }
}
