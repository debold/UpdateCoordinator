using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UpdateCoordinator.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeDefaultRingIntoProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Servers and ring rules of the former Default ring move to Production, the final and fallback ring.
            migrationBuilder.Sql("UPDATE \"Clients\" SET \"RingId\" = 3 WHERE \"RingId\" = 4;");
            migrationBuilder.Sql("UPDATE \"Clients\" SET \"ManualRingId\" = 3 WHERE \"ManualRingId\" = 4;");
            migrationBuilder.Sql("UPDATE \"AssignmentRules\" SET \"TargetId\" = 3 WHERE \"Target\" = 0 AND \"TargetId\" = 4;");

            migrationBuilder.DeleteData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.UpdateData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 3,
                column: "IsDefault",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Rings",
                keyColumn: "Id",
                keyValue: 3,
                column: "IsDefault",
                value: false);

            migrationBuilder.InsertData(
                table: "Rings",
                columns: new[] { "Id", "IsDefault", "Name", "Order" },
                values: new object[] { 4, true, "Default", 4 });
        }
    }
}
