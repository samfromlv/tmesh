using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceMetrics",
                columns: table => new
                {
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    LocationUpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AccuracyMeters = table.Column<int>(type: "integer", nullable: true),
                    ChannelUtil = table.Column<float>(type: "real", nullable: true),
                    AirUtil = table.Column<float>(type: "real", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceMetrics", x => new { x.DeviceId, x.Timestamp });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceMetrics");
        }
    }
}
