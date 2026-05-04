using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class CityDistrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<int>(
                name: "CityId",
                table: "Votes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CityDistrictId",
                table: "VoteParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Cities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CityDistricts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CityId = table.Column<int>(type: "integer", nullable: false),
                    Borders = table.Column<Geometry>(type: "geometry(Geometry, 4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CityDistricts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoteStatsByDistrict",
                columns: table => new
                {
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    CityDistrictId = table.Column<int>(type: "integer", nullable: false),
                    OptionId = table.Column<byte>(type: "smallint", nullable: false),
                    ActiveCount = table.Column<int>(type: "integer", nullable: false),
                    DeltaFromLastSnapshot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteStatsByDistrict", x => new { x.SnapshotId, x.CityDistrictId, x.OptionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CityDistricts_Borders",
                table: "CityDistricts",
                column: "Borders")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_CityDistricts_CityId",
                table: "CityDistricts",
                column: "CityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cities");

            migrationBuilder.DropTable(
                name: "CityDistricts");

            migrationBuilder.DropTable(
                name: "VoteStatsByDistrict");

            migrationBuilder.DropColumn(
                name: "CityId",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "CityDistrictId",
                table: "VoteParticipants");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
