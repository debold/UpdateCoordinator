using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UpdateCoordinator.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LogFileSize",
                table: "AgentActions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogFileSize",
                table: "AgentActions");
        }
    }
}
