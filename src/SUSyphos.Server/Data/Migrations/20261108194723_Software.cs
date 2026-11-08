using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Software : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Updates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PackageId",
                table: "Updates",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                collation: "NOCASE");

            migrationBuilder.AddColumn<string>(
                name: "PackageVersion",
                table: "Updates",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "StartRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageIdPattern",
                table: "StartRules",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                table: "StartRules",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientSoftware",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    View = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DisplayVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, collation: "NOCASE"),
                    InstalledVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MatchKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSoftware", x => new { x.ClientId, x.View, x.KeyName });
                    table.ForeignKey(
                        name: "FK_ClientSoftware_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ShortDescription = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    Moniker = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LicenseUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    PackageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    PublisherUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ReleaseNotesUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    LogoFile = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    HasAgreements = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgreementsAcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgreementsAcceptedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AddedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.PackageId);
                });

            migrationBuilder.CreateTable(
                name: "PackageAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    TargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    FixedVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UninstallOnRemoval = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageAssignments", x => x.Id);
                    table.CheckConstraint("CK_PackageAssignments_Target", "(\"TargetKind\" = 0 AND \"GroupId\" IS NOT NULL AND \"ClientId\" IS NULL) OR (\"TargetKind\" = 1 AND \"ClientId\" IS NOT NULL AND \"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PackageAssignments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageAssignments_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageAssignments_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "PackageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Manifest = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: true),
                    InstallerFile = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    InstallerSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CachedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScanState = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanDetail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ScannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => new { x.PackageId, x.Version });
                    table.ForeignKey(
                        name: "FK_PackageVersions_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "PackageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "StartRules",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "Kind", "PackageIdPattern", "Publisher" },
                values: new object[] { 0, null, null });

            // Existing start rules were written for Windows updates; software gets its own default rule (manual start).
            migrationBuilder.Sql("UPDATE \"StartRules\" SET \"Kind\" = 0 WHERE \"Kind\" IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Updates_PackageId_PackageVersion",
                table: "Updates",
                columns: new[] { "PackageId", "PackageVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSoftware_PackageId",
                table: "ClientSoftware",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageAssignments_ClientId",
                table: "PackageAssignments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageAssignments_GroupId",
                table: "PackageAssignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageAssignments_PackageId",
                table: "PackageAssignments",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_UpdateId",
                table: "PackageVersions",
                column: "UpdateId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientSoftware");

            migrationBuilder.DropTable(
                name: "PackageAssignments");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "Packages");

            migrationBuilder.DropIndex(
                name: "IX_Updates_PackageId_PackageVersion",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "PackageVersion",
                table: "Updates");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StartRules");

            migrationBuilder.DropColumn(
                name: "PackageIdPattern",
                table: "StartRules");

            migrationBuilder.DropColumn(
                name: "Publisher",
                table: "StartRules");
        }
    }
}
