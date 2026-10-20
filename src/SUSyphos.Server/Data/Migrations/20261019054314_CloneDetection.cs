using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CloneDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastHeartbeatHostName",
                table: "Clients",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousHostName",
                table: "Clients",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousHostNameSeenAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastHeartbeatHostName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PreviousHostName",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PreviousHostNameSeenAtUtc",
                table: "Clients");
        }
    }
}
