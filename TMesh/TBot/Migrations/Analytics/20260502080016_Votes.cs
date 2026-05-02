using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class Votes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoteLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VoteId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<byte>(type: "smallint", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    LogCreated = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangeMade = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    NewLongName = table.Column<string>(type: "text", nullable: true),
                    OldOptionId = table.Column<byte>(type: "smallint", nullable: false),
                    NewOptionId = table.Column<byte>(type: "smallint", nullable: false),
                    MeshPacketId = table.Column<long>(type: "bigint", nullable: true),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoteParticipants",
                columns: table => new
                {
                    VoteId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    LongName = table.Column<string>(type: "text", nullable: false),
                    FirstVote = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastVote = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastVoteChange = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    NodeRegistered = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CurrentOptionId = table.Column<byte>(type: "smallint", nullable: false),
                    PreviousOptionId = table.Column<byte>(type: "smallint", nullable: false),
                    IsNoVote = table.Column<bool>(type: "boolean", nullable: false),
                    VoteCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteParticipants", x => new { x.VoteId, x.DeviceId });
                });

            migrationBuilder.CreateTable(
                name: "Votes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NetworkId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NodeActiveHoursLimit = table.Column<int>(type: "integer", nullable: false),
                    UpdateIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    LastUpdate = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSnapshotId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Votes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoteSnapshotRecords",
                columns: table => new
                {
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    LongName = table.Column<string>(type: "text", nullable: false),
                    OptionId = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteSnapshotRecords", x => new { x.SnapshotId, x.DeviceId });
                });

            migrationBuilder.CreateTable(
                name: "VoteSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VoteId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    PreviousSnapshotId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoteStats",
                columns: table => new
                {
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    OptionId = table.Column<byte>(type: "smallint", nullable: false),
                    ActiveCount = table.Column<int>(type: "integer", nullable: false),
                    DeltaFromLastSnapshot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteStats", x => new { x.SnapshotId, x.OptionId });
                });

            migrationBuilder.CreateTable(
                name: "VoteOptions",
                columns: table => new
                {
                    VoteId = table.Column<int>(type: "integer", nullable: false),
                    OptionId = table.Column<byte>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteOptions", x => new { x.VoteId, x.OptionId });
                    table.ForeignKey(
                        name: "FK_VoteOptions_Votes_VoteId",
                        column: x => x.VoteId,
                        principalTable: "Votes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoteLogs_SnapshotId",
                table: "VoteLogs",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_VoteLogs_VoteId_DeviceId_ChangeMade",
                table: "VoteLogs",
                columns: new[] { "VoteId", "DeviceId", "ChangeMade" });

            migrationBuilder.CreateIndex(
                name: "IX_Votes_IsActive",
                table: "Votes",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_VoteSnapshots_VoteId_Timestamp",
                table: "VoteSnapshots",
                columns: new[] { "VoteId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoteLogs");

            migrationBuilder.DropTable(
                name: "VoteOptions");

            migrationBuilder.DropTable(
                name: "VoteParticipants");

            migrationBuilder.DropTable(
                name: "VoteSnapshotRecords");

            migrationBuilder.DropTable(
                name: "VoteSnapshots");

            migrationBuilder.DropTable(
                name: "VoteStats");

            migrationBuilder.DropTable(
                name: "Votes");
        }
    }
}
