using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateError",
                table: "Clients",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentPackages",
                columns: table => new
                {
                    Version = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedThroughRingOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPackages", x => x.Version);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPackages");

            migrationBuilder.DropColumn(
                name: "AgentUpdateError",
                table: "Clients");
        }
    }
}
