using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StartRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StartRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Classification = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MsrcSeverity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TitlePattern = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Delay = table.Column<TimeSpan>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartRules", x => x.Id);
                });

            // Replaced by start rules.
            migrationBuilder.Sql("DELETE FROM \"Settings\" WHERE \"Key\" = 'Rollout.AutoStartClassifications';");

            migrationBuilder.InsertData(
                table: "StartRules",
                columns: new[] { "Id", "Classification", "Delay", "Mode", "MsrcSeverity", "Priority", "TitlePattern" },
                values: new object[] { 1, null, new TimeSpan(0, 0, 0, 0, 0), 0, null, 1000, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StartRules");
        }
    }
}
