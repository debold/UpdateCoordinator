using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignedScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackageScripts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftContent = table.Column<string>(type: "TEXT", nullable: true),
                    DraftParametersJson = table.Column<string>(type: "TEXT", nullable: true),
                    DraftRunOn = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftOnFailure = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DraftChangedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ApprovedRevision = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageScripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageScripts_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "PackageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScriptKeys",
                columns: table => new
                {
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Certificate = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptKeys", x => x.KeyId);
                });

            migrationBuilder.CreateTable(
                name: "ScriptRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    ScriptId = table.Column<int>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: true),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Result = table.Column<int>(type: "INTEGER", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Lines = table.Column<int>(type: "INTEGER", nullable: false),
                    Errors = table.Column<int>(type: "INTEGER", nullable: false),
                    Warnings = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScriptRuns_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageAssignmentScriptValues",
                columns: table => new
                {
                    AssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ScriptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ProtectedValue = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageAssignmentScriptValues", x => new { x.AssignmentId, x.ScriptId, x.Name });
                    table.ForeignKey(
                        name: "FK_PackageAssignmentScriptValues_PackageAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "PackageAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageAssignmentScriptValues_PackageScripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "PackageScripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageScriptRevisions",
                columns: table => new
                {
                    ScriptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RunOn = table.Column<int>(type: "INTEGER", nullable: false),
                    OnFailure = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ScanState = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanDetail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageScriptRevisions", x => new { x.ScriptId, x.Revision });
                    table.ForeignKey(
                        name: "FK_PackageScriptRevisions_PackageScripts_ScriptId",
                        column: x => x.ScriptId,
                        principalTable: "PackageScripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageScriptRevisions_ScriptKeys_KeyId",
                        column: x => x.KeyId,
                        principalTable: "ScriptKeys",
                        principalColumn: "KeyId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageAssignmentScriptValues_ScriptId",
                table: "PackageAssignmentScriptValues",
                column: "ScriptId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageScriptRevisions_KeyId",
                table: "PackageScriptRevisions",
                column: "KeyId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageScripts_PackageId_Phase",
                table: "PackageScripts",
                columns: new[] { "PackageId", "Phase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptRuns_ClientId_FinishedAtUtc",
                table: "ScriptRuns",
                columns: new[] { "ClientId", "FinishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptRuns_PackageId_Phase",
                table: "ScriptRuns",
                columns: new[] { "PackageId", "Phase" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackageAssignmentScriptValues");

            migrationBuilder.DropTable(
                name: "PackageScriptRevisions");

            migrationBuilder.DropTable(
                name: "ScriptRuns");

            migrationBuilder.DropTable(
                name: "PackageScripts");

            migrationBuilder.DropTable(
                name: "ScriptKeys");
        }
    }
}
