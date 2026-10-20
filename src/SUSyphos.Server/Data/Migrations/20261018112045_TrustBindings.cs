using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrustBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrustBindings",
                columns: table => new
                {
                    Serial = table.Column<long>(type: "INTEGER", nullable: false),
                    CertificateSpkiSha256 = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustBindings", x => x.Serial);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrustBindings");
        }
    }
}
