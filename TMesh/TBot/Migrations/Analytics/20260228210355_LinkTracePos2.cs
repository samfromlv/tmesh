using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class LinkTracePos2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traces_RecDate",
                table: "Traces");

            migrationBuilder.AddColumn<double>(
                name: "FromLatitude",
                table: "Traces",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "FromLongitude",
                table: "Traces",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateIndex(
                name: "IX_Traces_RecDate",
                table: "Traces",
                column: "RecDate")
                .Annotation("Npgsql:IndexInclude", new[] { "PacketId", "FromGatewayId", "ToGatewayId", "Step", "ToLatitude", "ToLongitude", "FromLatitude", "FromLongitude", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traces_RecDate",
                table: "Traces");

            migrationBuilder.DropColumn(
                name: "FromLatitude",
                table: "Traces");

            migrationBuilder.DropColumn(
                name: "FromLongitude",
                table: "Traces");

            migrationBuilder.CreateIndex(
                name: "IX_Traces_RecDate",
                table: "Traces",
                column: "RecDate")
                .Annotation("Npgsql:IndexInclude", new[] { "PacketId", "FromGatewayId", "ToGatewayId", "Step" });
        }
    }
}
