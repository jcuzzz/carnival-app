using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0014 §1 schedule model: the advisory program as concurrent
    /// <c>ScheduleStreams</c> of ordered <c>ScheduleSlots</c>, plus the
    /// <c>ScheduleSlotId</c>/<c>SlotOrder</c> placement columns on
    /// <c>CarnivalEvents</c>. Everything added here is additive and optional —
    /// a carnival with no streams runs exactly as it did before, because nothing
    /// in entry, result, eligibility or lifecycle reads the schedule.
    ///
    /// Slot times are <c>time without time zone</c>, not instants: the carnival
    /// owns the date and the school owns the timezone, so "09:45" is 09:45 on the
    /// poolside clock whatever date the carnival ends up being held on.
    ///
    /// The event → slot foreign key is ON DELETE SET NULL, deliberately: deleting
    /// a slot deletes a line of the PROGRAM, and must never delete the
    /// competition. Its events become unscheduled and stay fully usable.
    ///
    /// DESTRUCTIVE, once: <c>CarnivalEvents.ScheduledAtUtc</c> is dropped. It was
    /// vestigial — nothing read it but the event DTO and the list's ordering,
    /// both re-based on the slot model here — and slots carry the times now. Any
    /// value stored in it is discarded and is not recoverable by re-running the
    /// Down migration.
    /// </summary>
    public partial class AddScheduleStreamsAndSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduledAtUtc",
                table: "CarnivalEvents");

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleSlotId",
                table: "CarnivalEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SlotOrder",
                table: "CarnivalEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduleStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleStreams_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AdjustmentCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleSlots_ScheduleStreams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "ScheduleStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_ScheduleSlotId",
                table: "CarnivalEvents",
                column: "ScheduleSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSlots_StreamId",
                table: "ScheduleSlots",
                column: "StreamId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleStreams_CarnivalId_Name",
                table: "ScheduleStreams",
                columns: new[] { "CarnivalId", "Name" },
                unique: true);

            // Restrict, not SET NULL: a database-level SET NULL here would give
            // CarnivalEvents two cascade paths from Carnival (direct, and via
            // ScheduleStreams → ScheduleSlots), which PostgreSQL allows but SQL
            // Server refuses at schema creation — and ADR-0001 review note 5
            // keeps that escape hatch open. The slot/stream delete endpoints
            // unschedule their events first, so nothing relies on the database
            // to break the link (ADR-0014 §1: deleting a program line never
            // deletes competition).
            migrationBuilder.AddForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents",
                column: "ScheduleSlotId",
                principalTable: "ScheduleSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents");

            migrationBuilder.DropTable(
                name: "ScheduleSlots");

            migrationBuilder.DropTable(
                name: "ScheduleStreams");

            migrationBuilder.DropIndex(
                name: "IX_CarnivalEvents_ScheduleSlotId",
                table: "CarnivalEvents");

            migrationBuilder.DropColumn(
                name: "ScheduleSlotId",
                table: "CarnivalEvents");

            migrationBuilder.DropColumn(
                name: "SlotOrder",
                table: "CarnivalEvents");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledAtUtc",
                table: "CarnivalEvents",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
