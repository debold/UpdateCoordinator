using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SUSyphos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallationSourceSUSyphos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Installations recorded before the rename carried the working title.
            migrationBuilder.Sql("UPDATE Installations SET Source = 'SUSyphos' WHERE Source = 'Coordinator';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Installations SET Source = 'Coordinator' WHERE Source = 'SUSyphos';");
        }
    }
}
