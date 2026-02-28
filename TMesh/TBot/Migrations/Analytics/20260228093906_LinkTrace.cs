using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class LinkTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Traces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PacketId = table.Column<long>(type: "bigint", nullable: false),
                    FromGatewayId = table.Column<long>(type: "bigint", nullable: false),
                    ToGatewayId = table.Column<long>(type: "bigint", nullable: false),
                    Step = table.Column<byte>(type: "smallint", nullable: true),
                    Timestamp = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RecDate = table.Column<LocalDate>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Traces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Traces_RecDate",
                table: "Traces",
                column: "RecDate")
                .Annotation("Npgsql:IndexInclude", new[] { "PacketId", "FromGatewayId", "ToGatewayId", "Step" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Traces");
        }
    }
}
