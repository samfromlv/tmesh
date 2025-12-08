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

            migrationBuilder.Sql(@"
        UPDATE Devices
        SET HasRegistrations = CASE
            WHEN EXISTS (
                SELECT 1 
                FROM Registrations r 
                WHERE r.DeviceId = Devices.DeviceId
            )
            THEN 1
            ELSE 0
        END;
    ");

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
