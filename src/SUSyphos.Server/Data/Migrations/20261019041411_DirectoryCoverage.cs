using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DirectoryCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectoryComputers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    DnsHostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Build = table.Column<int>(type: "INTEGER", nullable: true),
                    Supported = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryComputers", x => x.Name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectoryComputers");
        }
    }
}
