using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RolloutStartRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StartRuleId",
                table: "Rollouts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartRuleSummary",
                table: "Rollouts",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartRuleId",
                table: "Rollouts");

            migrationBuilder.DropColumn(
                name: "StartRuleSummary",
                table: "Rollouts");
        }
    }
}
