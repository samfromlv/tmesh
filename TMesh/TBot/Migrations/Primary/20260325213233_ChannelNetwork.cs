using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Primary
{
    /// <inheritdoc />
    public partial class ChannelNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_Name_Key",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_NetworkId",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_XorHash",
                table: "Channels");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_NetworkId_Name_Key",
                table: "Channels",
                columns: new[] { "NetworkId", "Name", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_NetworkId_XorHash",
                table: "Channels",
                columns: new[] { "NetworkId", "XorHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_NetworkId_Name_Key",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_Channels_NetworkId_XorHash",
                table: "Channels");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Name_Key",
                table: "Channels",
                columns: new[] { "Name", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_NetworkId",
                table: "Channels",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_XorHash",
                table: "Channels",
                column: "XorHash");
        }
    }
}
