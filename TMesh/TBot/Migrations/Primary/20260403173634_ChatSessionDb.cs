using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Primary
{
    /// <inheritdoc />
    public partial class ChatSessionDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TgChatApprovedChannels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TgChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TgChatApprovedChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TgChatApprovedDevices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TgChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TgChatApprovedDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TgChats",
                columns: table => new
                {
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChatName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TgChats", x => x.ChatId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TgChatApprovedChannels_ChannelId",
                table: "TgChatApprovedChannels",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_TgChatApprovedChannels_TgChatId_ChannelId",
                table: "TgChatApprovedChannels",
                columns: new[] { "TgChatId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TgChatApprovedDevices_DeviceId",
                table: "TgChatApprovedDevices",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_TgChatApprovedDevices_TgChatId_DeviceId",
                table: "TgChatApprovedDevices",
                columns: new[] { "TgChatId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TgChats_ChatName",
                table: "TgChats",
                column: "ChatName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TgChatApprovedChannels");

            migrationBuilder.DropTable(
                name: "TgChatApprovedDevices");

            migrationBuilder.DropTable(
                name: "TgChats");
        }
    }
}
