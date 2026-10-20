using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Assignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EffectiveWindowsJson",
                table: "Clients",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceWindowsJson",
                table: "Clients",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManualRingId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RingId",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssignmentRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    HostNamePattern = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignmentRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MaintenanceWindowsJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ClientGroups",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Manual = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientGroups", x => new { x.ClientId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_ClientGroups_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientGroups_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Rings",
                columns: new[] { "Id", "IsDefault", "Name", "Order" },
                values: new object[,]
                {
                    { 1, false, "Pilot", 1 },
                    { 2, false, "Test", 2 },
                    { 3, false, "Production", 3 },
                    { 4, true, "Default", 4 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ManualRingId",
                table: "Clients",
                column: "ManualRingId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_RingId",
                table: "Clients",
                column: "RingId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientGroups_GroupId",
                table: "ClientGroups",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                table: "Groups",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Rings_ManualRingId",
                table: "Clients",
                column: "ManualRingId",
                principalTable: "Rings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Rings_RingId",
                table: "Clients",
                column: "RingId",
                principalTable: "Rings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Rings_ManualRingId",
                table: "Clients");

            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Rings_RingId",
                table: "Clients");

            migrationBuilder.DropTable(
                name: "AssignmentRules");

            migrationBuilder.DropTable(
                name: "ClientGroups");

            migrationBuilder.DropTable(
                name: "Rings");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_Clients_ManualRingId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_RingId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "EffectiveWindowsJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "MaintenanceWindowsJson",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ManualRingId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RingId",
                table: "Clients");
        }
    }
}
