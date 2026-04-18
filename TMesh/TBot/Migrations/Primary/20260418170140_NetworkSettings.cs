using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Primary
{
    /// <inheritdoc />
    public partial class NetworkSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SendNodeInfoOnSecondary",
                table: "PublicChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CommunityUrl",
                table: "Networks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DisableWelcomeMessage",
                table: "Networks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SendNodeInfoOnSecondary",
                table: "PublicChannels");

            migrationBuilder.DropColumn(
                name: "CommunityUrl",
                table: "Networks");

            migrationBuilder.DropColumn(
                name: "DisableWelcomeMessage",
                table: "Networks");
        }
    }
}
