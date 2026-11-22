using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Rc8ScriptFilesPowerShell7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftFilesSha256",
                table: "PackageScripts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilesSha256",
                table: "PackageScriptRevisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PowerShell7Version",
                table: "Clients",
                type: "TEXT",
                maxLength: 23,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScriptFiles",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UnpackedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ScanState = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanDetail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptFiles", x => x.Sha256);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageScripts_DraftFilesSha256",
                table: "PackageScripts",
                column: "DraftFilesSha256");

            migrationBuilder.CreateIndex(
                name: "IX_PackageScriptRevisions_FilesSha256",
                table: "PackageScriptRevisions",
                column: "FilesSha256");

            migrationBuilder.AddForeignKey(
                name: "FK_PackageScriptRevisions_ScriptFiles_FilesSha256",
                table: "PackageScriptRevisions",
                column: "FilesSha256",
                principalTable: "ScriptFiles",
                principalColumn: "Sha256",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PackageScripts_ScriptFiles_DraftFilesSha256",
                table: "PackageScripts",
                column: "DraftFilesSha256",
                principalTable: "ScriptFiles",
                principalColumn: "Sha256",
                onDelete: ReferentialAction.SetNull);

            // 0.9.0-rc.7 took an installer that vanished during the Windows check for downloaded files for a threat
            // (WebView2 158, lab 2026-11-22); such versions are scanned anew through AMSI with the next run of the cache.
            migrationBuilder.Sql("UPDATE PackageVersions SET ScanState = 0, ScanDetail = NULL, ScannedAtUtc = NULL WHERE ScanState = 2 AND ScanDetail LIKE '%removed the file while scanning%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PackageScriptRevisions_ScriptFiles_FilesSha256",
                table: "PackageScriptRevisions");

            migrationBuilder.DropForeignKey(
                name: "FK_PackageScripts_ScriptFiles_DraftFilesSha256",
                table: "PackageScripts");

            migrationBuilder.DropTable(
                name: "ScriptFiles");

            migrationBuilder.DropIndex(
                name: "IX_PackageScripts_DraftFilesSha256",
                table: "PackageScripts");

            migrationBuilder.DropIndex(
                name: "IX_PackageScriptRevisions_FilesSha256",
                table: "PackageScriptRevisions");

            migrationBuilder.DropColumn(
                name: "DraftFilesSha256",
                table: "PackageScripts");

            migrationBuilder.DropColumn(
                name: "FilesSha256",
                table: "PackageScriptRevisions");

            migrationBuilder.DropColumn(
                name: "PowerShell7Version",
                table: "Clients");
        }
    }
}
