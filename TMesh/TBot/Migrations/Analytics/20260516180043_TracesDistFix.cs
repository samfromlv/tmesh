using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TBot.Migrations.Analytics
{
    /// <inheritdoc />
    public partial class TracesDistFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WithDistanceCount",
                table: "TraceRoutePairStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WithLinkLengthCount",
                table: "TraceRoutePairStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
        CREATE OR REPLACE PROCEDURE aggregate_trace_route_pairs()
        LANGUAGE plpgsql
        AS $$
        BEGIN
            PERFORM pg_advisory_xact_lock(987654321);

            WITH deleted_rows AS (
                DELETE FROM "TraceRoutePairs"
                RETURNING
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId",
                    "Hops",
                    "DirectSnr",
                    "DistanceBetweenDevices",
                    "LinkLengthMeters"
            ),
            aggregated AS (
                SELECT
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId",
                    COUNT(*)::int AS "Count",
                    COUNT("DirectSnr")::int AS "DirectCount", 
                    COUNT("DistanceBetweenDevices")::int AS "WithDistanceCount",
                    COUNT("LinkLengthMeters")::int AS "WithLinkLengthCount",
                    AVG("Hops")::real AS "AvgHops",
                    AVG("DirectSnr")::real AS "AvgDirectSnr",
                    AVG("DistanceBetweenDevices")::real AS "AvgDirectDistance",
                    AVG("LinkLengthMeters")::real AS "AvgLinkLength"
                FROM deleted_rows
                GROUP BY
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId"
            )
            INSERT INTO "TraceRoutePairStats" (
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId",
                "Count",
                "DirectCount",
                "WithDistanceCount",
                "WithLinkLengthCount",
                "AvgHops",
                "AvgDirectSnr",
                "AvgDirectDistance",
                "AvgLinkLength"
            )
            SELECT
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId",
                "Count",
                "DirectCount",
                "WithDistanceCount",
                "WithLinkLengthCount",
                "AvgHops",
                "AvgDirectSnr",
                "AvgDirectDistance",
                "AvgLinkLength"
            FROM aggregated
            ON CONFLICT (
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId"
            )
            DO UPDATE SET
                "AvgHops" =
                    (
                        "TraceRoutePairStats"."AvgHops" * "TraceRoutePairStats"."Count"
                        +
                        EXCLUDED."AvgHops" * EXCLUDED."Count"
                    )
                    /
                    ("TraceRoutePairStats"."Count" + EXCLUDED."Count"),

                "AvgDirectSnr" =
                    CASE
                        WHEN ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgDirectSnr", 0)
                                * "TraceRoutePairStats"."DirectCount"
                                +
                                COALESCE(EXCLUDED."AvgDirectSnr", 0)
                                * EXCLUDED."DirectCount"
                            )
                            /
                            ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount")
                    END,

                "AvgDirectDistance" =
                    CASE
                        WHEN ("TraceRoutePairStats"."WithDistanceCount" + EXCLUDED."WithDistanceCount") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgDirectDistance", 0)
                                * "TraceRoutePairStats"."WithDistanceCount"
                                +
                                COALESCE(EXCLUDED."AvgDirectDistance", 0)
                                * EXCLUDED."WithDistanceCount"
                            )
                            /
                            ("TraceRoutePairStats"."WithDistanceCount" + EXCLUDED."WithDistanceCount")
                    END,

                "AvgLinkLength" =
                    CASE
                        WHEN ("TraceRoutePairStats"."WithLinkLengthCount" + EXCLUDED."WithLinkLengthCount") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgLinkLength", 0)
                                * "TraceRoutePairStats"."WithLinkLengthCount"
                                +
                                COALESCE(EXCLUDED."AvgLinkLength", 0)
                                * EXCLUDED."WithLinkLengthCount"
                            )
                            /
                            ("TraceRoutePairStats"."WithLinkLengthCount" + EXCLUDED."WithLinkLengthCount")
                    END,

                "Count" =
                    "TraceRoutePairStats"."Count" + EXCLUDED."Count",

                "DirectCount" =
                    "TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount";
        END;
        $$;
    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
        CREATE OR REPLACE PROCEDURE aggregate_trace_route_pairs()
        LANGUAGE plpgsql
        AS $$
        BEGIN
            PERFORM pg_advisory_xact_lock(987654321);

            WITH deleted_rows AS (
                DELETE FROM "TraceRoutePairs"
                RETURNING
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId",
                    "Hops",
                    "DirectSnr",
                    "DistanceBetweenDevices",
                    "LinkLengthMeters"
            ),
            aggregated AS (
                SELECT
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId",
                    COUNT(*)::int AS "Count",
                    COUNT("DirectSnr")::int AS "DirectCount",
                    AVG("Hops")::real AS "AvgHops",
                    AVG("DirectSnr")::real AS "AvgDirectSnr",
                    AVG("DistanceBetweenDevices")::real AS "AvgDirectDistance",
                    AVG("LinkLengthMeters")::real AS "AvgLinkLength"
                FROM deleted_rows
                GROUP BY
                    "RecDate",
                    "NetworkId",
                    "ToDeviceId",
                    "FromDeviceId"
            )
            INSERT INTO "TraceRoutePairStats" (
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId",
                "Count",
                "DirectCount",
                "AvgHops",
                "AvgDirectSnr",
                "AvgDirectDistance",
                "AvgLinkLength"
            )
            SELECT
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId",
                "Count",
                "DirectCount",
                "AvgHops",
                "AvgDirectSnr",
                "AvgDirectDistance",
                "AvgLinkLength"
            FROM aggregated
            ON CONFLICT (
                "RecDate",
                "NetworkId",
                "ToDeviceId",
                "FromDeviceId"
            )
            DO UPDATE SET
                "AvgHops" =
                    (
                        "TraceRoutePairStats"."AvgHops" * "TraceRoutePairStats"."Count"
                        +
                        EXCLUDED."AvgHops" * EXCLUDED."Count"
                    )
                    /
                    ("TraceRoutePairStats"."Count" + EXCLUDED."Count"),

                "AvgDirectSnr" =
                    CASE
                        WHEN ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgDirectSnr", 0)
                                * "TraceRoutePairStats"."DirectCount"
                                +
                                COALESCE(EXCLUDED."AvgDirectSnr", 0)
                                * EXCLUDED."DirectCount"
                            )
                            /
                            ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount")
                    END,

                "AvgDirectDistance" =
                    CASE
                        WHEN ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgDirectDistance", 0)
                                * "TraceRoutePairStats"."DirectCount"
                                +
                                COALESCE(EXCLUDED."AvgDirectDistance", 0)
                                * EXCLUDED."DirectCount"
                            )
                            /
                            ("TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount")
                    END,

                "AvgLinkLength" =
                    CASE
                        WHEN ("TraceRoutePairStats"."Count" + EXCLUDED."Count") = 0
                            THEN NULL
                        ELSE
                            (
                                COALESCE("TraceRoutePairStats"."AvgLinkLength", 0)
                                * "TraceRoutePairStats"."Count"
                                +
                                COALESCE(EXCLUDED."AvgLinkLength", 0)
                                * EXCLUDED."Count"
                            )
                            /
                            ("TraceRoutePairStats"."Count" + EXCLUDED."Count")
                    END,

                "Count" =
                    "TraceRoutePairStats"."Count" + EXCLUDED."Count",

                "DirectCount" =
                    "TraceRoutePairStats"."DirectCount" + EXCLUDED."DirectCount";
        END;
        $$;
    """);

            migrationBuilder.DropColumn(
                name: "WithDistanceCount",
                table: "TraceRoutePairStats");

            migrationBuilder.DropColumn(
                name: "WithLinkLengthCount",
                table: "TraceRoutePairStats");
        }
    }
}
