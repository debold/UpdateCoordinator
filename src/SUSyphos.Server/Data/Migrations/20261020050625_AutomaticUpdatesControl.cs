using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticUpdatesControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManageAutomaticUpdates",
                table: "Groups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutomaticUpdatesManagedByAgent",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EffectiveManageAutomaticUpdates",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManageAutomaticUpdates",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "AutomaticUpdatesManagedByAgent",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "EffectiveManageAutomaticUpdates",
                table: "Clients");
        }
    }
}
