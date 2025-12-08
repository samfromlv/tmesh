using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasRegistrations",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasRegistrations",
                table: "Devices");
        }
    }
}
