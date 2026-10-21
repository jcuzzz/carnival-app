using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0015 phase 1: the physical unit of capture. Adds <c>Races</c> (the set
    /// of lanes that goes off together, §3), the <c>Entries.RaceId</c> link, and
    /// the constraint that gives the race its purpose — ONE ENTRY PER LANE PER
    /// RACE — and drops <c>CarnivalEvents.HeatLaneSettingsJson</c> (§6).
    ///
    /// Everything added is additive and optional. <c>Races.ScheduleSlotId</c> and
    /// <c>Entries.RaceId</c> are both nullable because the schedule is advisory
    /// and never gating (ADR-0014 §1): a race must be usable with no program
    /// built, and an entry with no race is the ordinary state of every entry
    /// until the morning of the carnival. No existing row changes meaning.
    ///
    /// DESTRUCTIVE, once: <c>CarnivalEvents.HeatLaneSettingsJson</c> is dropped
    /// and its contents are NOT recoverable by running Down (which re-creates the
    /// column empty). The column was read by nothing — only written and shape-
    /// validated by the event create/update endpoints — so no behaviour is lost:
    /// it advertised "lanes count, heat seeding rules", the first is now
    /// <c>laneCount</c> in <c>Carnival.SettingsJson</c> (venue configuration, one
    /// value per venue — §6) and the second is not being built, since lanes fill
    /// first-in-best-dressed at marshalling (§5). PRE-FLIGHT BEFORE DEPLOYING,
    /// same discipline as the ADR-0014 migration — check, do not assume:
    ///     SELECT count(*) FROM "CarnivalEvents" WHERE "HeatLaneSettingsJson" IS NOT NULL;
    /// Expect zero. A non-zero count means somebody found a use for a column
    /// nothing reads, and that is a conversation before it is a deployment.
    ///
    /// THIS FILE IS ENTIRELY SCAFFOLDED — do not hand-edit it. Two things in it
    /// look like omissions and are not:
    ///
    /// 1. <c>IX_Entries_RaceId_Lane</c> carries NO filter. A filter written here
    ///    (or via <c>HasFilter</c> in the model) would be a relational annotation
    ///    handed verbatim to every provider, and the providers do not share a
    ///    quoting dialect — double-quoted identifiers are identifiers on SQL
    ///    Server only while QUOTED_IDENTIFIER is ON, which sqlcmd defaults OFF.
    ///    None is needed: PostgreSQL and SQLite treat NULLs in a unique index as
    ///    DISTINCT, so the (null, null) rows that are every entry before carnival
    ///    morning never collide; and the SQL Server provider adds its own
    ///    correctly-bracketed <c>WHERE [RaceId] IS NOT NULL AND [Lane] IS NOT
    ///    NULL</c> to any unique index over nullable columns. The existing
    ///    <c>IX_Entries_CarnivalEventId_StudentId</c> has relied on exactly this
    ///    since InitialSchema. See EntryConfiguration for the full reasoning.
    ///
    /// 2. Neither new foreign key names an <c>onDelete</c>, which means NO ACTION.
    ///    That is what EF Core 10 emits for <c>DeleteBehavior.ClientSetNull</c>,
    ///    and ClientSetNull is what the model asks for: SET NULL would give both
    ///    Races and Entries a second cascade path from Carnival, which PostgreSQL
    ///    accepts and SQL Server refuses outright at schema creation (ADR-0001
    ///    review note 5). The detaching is done in application code — deleting a
    ///    program line or a race must never delete competition (ADR-0014 §1) — so
    ///    the database only has to refuse an orphan, which NO ACTION does.
    ///    <c>EnsureCreated</c> (tests, SQLite dev loop) builds from the same model
    ///    through the same differ, so dev, test and production agree.
    /// </summary>
    public partial class AddRacesLanesAndVenueLaneCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeatLaneSettingsJson",
                table: "CarnivalEvents");

            migrationBuilder.AddColumn<Guid>(
                name: "RaceId",
                table: "Entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Races",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleSlotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Races", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Races_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Races_ScheduleSlots_ScheduleSlotId",
                        column: x => x.ScheduleSlotId,
                        principalTable: "ScheduleSlots",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RaceId",
                table: "Entries",
                column: "RaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RaceId_Lane",
                table: "Entries",
                columns: new[] { "RaceId", "Lane" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Races_CarnivalId",
                table: "Races",
                column: "CarnivalId");

            migrationBuilder.CreateIndex(
                name: "IX_Races_ScheduleSlotId",
                table: "Races",
                column: "ScheduleSlotId");

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Races_RaceId",
                table: "Entries",
                column: "RaceId",
                principalTable: "Races",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Races_RaceId",
                table: "Entries");

            migrationBuilder.DropTable(
                name: "Races");

            migrationBuilder.DropIndex(
                name: "IX_Entries_RaceId",
                table: "Entries");

            migrationBuilder.DropIndex(
                name: "IX_Entries_RaceId_Lane",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "RaceId",
                table: "Entries");

            migrationBuilder.AddColumn<string>(
                name: "HeatLaneSettingsJson",
                table: "CarnivalEvents",
                type: "text",
                nullable: true);
        }
    }
}
