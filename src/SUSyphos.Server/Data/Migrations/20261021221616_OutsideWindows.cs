using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutsideWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OutsideWindows",
                table: "StartRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RebootBehavior",
                table: "StartRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OutsideWindows",
                table: "Rollouts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "StartRules",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "OutsideWindows", "RebootBehavior" },
                values: new object[] { false, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutsideWindows",
                table: "StartRules");

            migrationBuilder.DropColumn(
                name: "RebootBehavior",
                table: "StartRules");

            migrationBuilder.DropColumn(
                name: "OutsideWindows",
                table: "Rollouts");
        }
    }
}
