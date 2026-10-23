using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoppedAsWithdrawn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StoppedAsWithdrawn",
                table: "Rollouts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Rollouts stopped by an earlier withdrawal mark resume by themselves if the mark turns out wrong.
            migrationBuilder.Sql("UPDATE Rollouts SET StoppedAsWithdrawn = 1 WHERE State = 1 AND EXISTS (SELECT 1 FROM Updates u WHERE u.UpdateId = Rollouts.UpdateId AND u.Revision = Rollouts.Revision AND u.WithdrawnAtUtc IS NOT NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoppedAsWithdrawn",
                table: "Rollouts");
        }
    }
}
