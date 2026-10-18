using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UpdateCoordinator.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    DomainName = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 23, nullable: false),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RegisteredFromAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    RegisteredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_HostName",
                table: "Clients",
                column: "HostName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clients");
        }
    }
}
