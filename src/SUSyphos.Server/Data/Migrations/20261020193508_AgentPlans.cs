using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextInventoryAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextSearchAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextInventoryAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "NextSearchAtUtc",
                table: "Clients");
        }
    }
}
