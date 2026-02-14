using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Primary
{
    /// <inheritdoc />
    public partial class AddChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Registrations",
                table: "Registrations");

            migrationBuilder.RenameTable(
                name: "Registrations",
                newName: "DeviceRegistrations");

            migrationBuilder.RenameIndex(
                name: "IX_Registrations_TelegramUserId_ChatId_DeviceId",
                table: "DeviceRegistrations",
                newName: "IX_DeviceRegistrations_TelegramUserId_ChatId_DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_Registrations_TelegramUserId",
                table: "DeviceRegistrations",
                newName: "IX_DeviceRegistrations_TelegramUserId");

            migrationBuilder.RenameIndex(
                name: "IX_Registrations_DeviceId",
                table: "DeviceRegistrations",
                newName: "IX_DeviceRegistrations_DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_Registrations_ChatId",
                table: "DeviceRegistrations",
                newName: "IX_DeviceRegistrations_ChatId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DeviceRegistrations",
                table: "DeviceRegistrations",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ChannelRegistrations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                    Key = table.Column<byte[]>(type: "BLOB", nullable: false),
                    XorHash = table.Column<byte>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRegistrations_ChannelId",
                table: "ChannelRegistrations",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRegistrations_ChatId",
                table: "ChannelRegistrations",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRegistrations_TelegramUserId",
                table: "ChannelRegistrations",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Name_Key",
                table: "Channels",
                columns: new[] { "Name", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_XorHash",
                table: "Channels",
                column: "XorHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelRegistrations");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DeviceRegistrations",
                table: "DeviceRegistrations");

            migrationBuilder.RenameTable(
                name: "DeviceRegistrations",
                newName: "Registrations");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceRegistrations_TelegramUserId_ChatId_DeviceId",
                table: "Registrations",
                newName: "IX_Registrations_TelegramUserId_ChatId_DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceRegistrations_TelegramUserId",
                table: "Registrations",
                newName: "IX_Registrations_TelegramUserId");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceRegistrations_DeviceId",
                table: "Registrations",
                newName: "IX_Registrations_DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceRegistrations_ChatId",
                table: "Registrations",
                newName: "IX_Registrations_ChatId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Registrations",
                table: "Registrations",
                column: "Id");
        }
    }
}
