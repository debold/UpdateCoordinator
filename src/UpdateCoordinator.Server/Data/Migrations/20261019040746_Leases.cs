using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UpdateCoordinator.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Leases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveLeaseFailures",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Coordinated",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LeaseMaximum",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "LeasePercent",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<bool>(
                name: "LeasesHalted",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresLease",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Leases_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Leases_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_ClientId_State",
                table: "Leases",
                columns: new[] { "ClientId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_GroupId_State",
                table: "Leases",
                columns: new[] { "GroupId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Leases");

            migrationBuilder.DropColumn(
                name: "ConsecutiveLeaseFailures",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "Coordinated",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LeaseMaximum",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LeasePercent",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LeasesHalted",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "RequiresLease",
                table: "Clients");
        }
    }
}
