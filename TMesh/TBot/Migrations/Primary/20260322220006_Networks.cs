using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Primary
{
    /// <inheritdoc />
    public partial class Networks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NetworkId",
                table: "GatewayRegistrations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "NetworkId",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "NetworkId",
                table: "Channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "Networks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SaveAnalytics = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShortName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Networks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NetworkId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    Key = table.Column<byte[]>(type: "BLOB", nullable: false),
                    XorHash = table.Column<byte>(type: "INTEGER", nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicChannels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayRegistrations_NetworkId",
                table: "GatewayRegistrations",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_NetworkId",
                table: "Devices",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_NetworkId",
                table: "Channels",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Networks_Name",
                table: "Networks",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicChannels_IsPrimary",
                table: "PublicChannels",
                column: "IsPrimary");

            migrationBuilder.CreateIndex(
                name: "IX_PublicChannels_NetworkId",
                table: "PublicChannels",
                column: "NetworkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Networks");

            migrationBuilder.DropTable(
                name: "PublicChannels");

            migrationBuilder.DropIndex(
                name: "IX_GatewayRegistrations_NetworkId",
                table: "GatewayRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_Devices_NetworkId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Channels_NetworkId",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "NetworkId",
                table: "GatewayRegistrations");

            migrationBuilder.DropColumn(
                name: "NetworkId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "NetworkId",
                table: "Channels");
        }
    }
}
