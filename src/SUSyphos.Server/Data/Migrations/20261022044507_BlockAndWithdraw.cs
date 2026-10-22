using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlockAndWithdraw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAtUtc",
                table: "Updates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockedBy",
                table: "Updates",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WithdrawnAtUtc",
                table: "Updates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NotOfferedAtUtc",
                table: "Releases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KbArticle",
                table: "AgentActions",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "AgentActions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdateId",
                table: "AgentActions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedAtUtc",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "BlockedBy",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "NotOfferedAtUtc",
                table: "Releases");

            migrationBuilder.DropColumn(
                name: "KbArticle",
                table: "AgentActions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AgentActions");

            migrationBuilder.DropColumn(
                name: "UpdateId",
                table: "AgentActions");
        }
    }
}
