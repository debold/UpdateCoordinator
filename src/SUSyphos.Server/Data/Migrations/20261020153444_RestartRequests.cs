using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestartRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PlannedRestartAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RestartRequested",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlannedRestartAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RestartRequested",
                table: "Clients");
        }
    }
}
