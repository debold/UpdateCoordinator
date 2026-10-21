using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Supersedence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateSupersedence",
                columns: table => new
                {
                    UpdateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    SupersededUpdateId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateSupersedence", x => new { x.UpdateId, x.Revision, x.SupersededUpdateId });
                    table.ForeignKey(
                        name: "FK_UpdateSupersedence_Updates_UpdateId_Revision",
                        columns: x => new { x.UpdateId, x.Revision },
                        principalTable: "Updates",
                        principalColumns: new[] { "UpdateId", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateSupersedence_SupersededUpdateId",
                table: "UpdateSupersedence",
                column: "SupersededUpdateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateSupersedence");
        }
    }
}
