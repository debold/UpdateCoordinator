using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RingSpread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpreadHours",
                table: "Rings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 1,
                column: "SpreadHours",
                value: 0);

            migrationBuilder.UpdateData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 2,
                column: "SpreadHours",
                value: 0);

            migrationBuilder.UpdateData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 3,
                column: "SpreadHours",
                value: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpreadHours",
                table: "Rings");
        }
    }
}
