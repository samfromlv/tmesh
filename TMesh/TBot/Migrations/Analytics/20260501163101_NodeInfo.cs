using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class NodeInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NodeInfos",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    HardwareModel = table.Column<int>(type: "integer", nullable: true),
                    IsLicensed = table.Column<bool>(type: "boolean", nullable: false),
                    IsUnmessagable = table.Column<bool>(type: "boolean", nullable: false),
                    Role = table.Column<byte>(type: "smallint", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    LongName = table.Column<string>(type: "text", nullable: true),
                    MacAddr = table.Column<long>(type: "bigint", nullable: true),
                    ShortName = table.Column<string>(type: "text", nullable: true),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeInfos", x => x.RecordId);
                });

            migrationBuilder.CreateTable(
                name: "Packets",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    PacketId = table.Column<long>(type: "bigint", nullable: false),
                    From = table.Column<long>(type: "bigint", nullable: false),
                    To = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<byte>(type: "smallint", nullable: false),
                    NextHop = table.Column<byte>(type: "smallint", nullable: false),
                    HopLimit = table.Column<byte>(type: "smallint", nullable: false),
                    HopStart = table.Column<byte>(type: "smallint", nullable: false),
                    WantAck = table.Column<bool>(type: "boolean", nullable: false),
                    ViaMqtt = table.Column<bool>(type: "boolean", nullable: false),
                    RelayNode = table.Column<byte>(type: "smallint", nullable: false),
                    MqttChannel = table.Column<string>(type: "text", nullable: true),
                    GatewayId = table.Column<long>(type: "bigint", nullable: false),
                    IsTMeshGateway = table.Column<bool>(type: "boolean", nullable: false),
                    RxTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    RxSnr = table.Column<float>(type: "real", nullable: false),
                    RxRssi = table.Column<int>(type: "integer", nullable: false),
                    TxAfter = table.Column<long>(type: "bigint", nullable: false),
                    PkiEncrypted = table.Column<bool>(type: "boolean", nullable: false),
                    DecodedByPublicChannelId = table.Column<int>(type: "integer", nullable: true),
                    Transport = table.Column<byte>(type: "smallint", nullable: false),
                    Priority = table.Column<byte>(type: "smallint", nullable: false),
                    OkToMqttFlag = table.Column<bool>(type: "boolean", nullable: false),
                    NeedReplyFlag = table.Column<bool>(type: "boolean", nullable: false),
                    WantResponse = table.Column<bool>(type: "boolean", nullable: false),
                    Dest = table.Column<long>(type: "bigint", nullable: false),
                    IsEmoji = table.Column<bool>(type: "boolean", nullable: false),
                    PortNum = table.Column<int>(type: "integer", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ReplyId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<long>(type: "bigint", nullable: false),
                    Flagged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packets", x => x.RecordId);
                });

            migrationBuilder.CreateTable(
                name: "RawPackets",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    Body = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawPackets", x => x.RecordId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Packets_PacketId",
                table: "Packets",
                column: "PacketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeInfos");

            migrationBuilder.DropTable(
                name: "Packets");

            migrationBuilder.DropTable(
                name: "RawPackets");
        }
    }
}
