using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiveStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityPercent",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActivityPhase",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityReportedAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivityStartedAtUtc",
                table: "Clients",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityUpdateIds",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "RebootWait",
                table: "Clients",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityPercent",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ActivityPhase",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ActivityReportedAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ActivityStartedAtUtc",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ActivityUpdateIds",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RebootWait",
                table: "Clients");
        }
    }
}
