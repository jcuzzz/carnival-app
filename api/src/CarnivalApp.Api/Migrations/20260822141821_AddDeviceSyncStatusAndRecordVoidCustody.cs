using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSyncStatusAndRecordVoidCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "SchoolRecords",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAtUtc",
                table: "SchoolRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoidedBySubject",
                table: "SchoolRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceSyncStatuses",
                columns: table => new
                {
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastDrainUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingCount = table.Column<int>(type: "integer", nullable: false),
                    ParkedCount = table.Column<int>(type: "integer", nullable: false),
                    OldestPendingCapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastDisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSyncStatuses", x => new { x.CarnivalId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_DeviceSyncStatuses_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSyncStatuses");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "SchoolRecords");

            migrationBuilder.DropColumn(
                name: "VoidedAtUtc",
                table: "SchoolRecords");

            migrationBuilder.DropColumn(
                name: "VoidedBySubject",
                table: "SchoolRecords");
        }
    }
}
