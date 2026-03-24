using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class Networks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traces_RecDate",
                table: "Traces");

            migrationBuilder.AddColumn<int>(
                name: "NetworkId",
                table: "Traces",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "NetworkId",
                table: "DeviceMetrics",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_Traces_NetworkId_RecDate",
                table: "Traces",
                columns: new[] { "NetworkId", "RecDate" })
                .Annotation("Npgsql:IndexInclude", new[] { "PacketId", "FromGatewayId", "ToGatewayId", "Step", "ToLatitude", "ToLongitude", "FromLatitude", "FromLongitude", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceMetrics_NetworkId_Timestamp",
                table: "DeviceMetrics",
                columns: new[] { "NetworkId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traces_NetworkId_RecDate",
                table: "Traces");

            migrationBuilder.DropIndex(
                name: "IX_DeviceMetrics_NetworkId_Timestamp",
                table: "DeviceMetrics");

            migrationBuilder.DropColumn(
                name: "NetworkId",
                table: "Traces");

            migrationBuilder.DropColumn(
                name: "NetworkId",
                table: "DeviceMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_Traces_RecDate",
                table: "Traces",
                column: "RecDate")
                .Annotation("Npgsql:IndexInclude", new[] { "PacketId", "FromGatewayId", "ToGatewayId", "Step", "ToLatitude", "ToLongitude", "FromLatitude", "FromLongitude", "Timestamp" });
        }
    }
}
