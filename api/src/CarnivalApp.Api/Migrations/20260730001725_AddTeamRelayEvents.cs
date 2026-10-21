using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRelayEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "EventDefinitions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Existing definitions are all Individual (ADR-0008); "" would
                // fail the enum-as-string read.
                defaultValue: "Individual");

            migrationBuilder.AddColumn<string>(
                name: "LegsJson",
                table: "EventDefinitions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScoringWeight",
                table: "EventDefinitions",
                type: "integer",
                nullable: false,
                // Individual events score at weight 1 (relays override to 2).
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TeamSize",
                table: "EventDefinitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "StudentId",
                table: "Entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "RosterSnapshotId",
                table: "Entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "HouseCode",
                table: "Entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RelayLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegOrder = table.Column<int>(type: "integer", nullable: false),
                    Stroke = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RosterSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SnapshotYearLevel = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHouseCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotAgeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SnapshotCategory = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelayLegs_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RelayLegs_RosterSnapshots_RosterSnapshotId",
                        column: x => x.RosterSnapshotId,
                        principalTable: "RosterSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RelayLegs_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_CarnivalEventId_HouseCode",
                table: "Entries",
                columns: new[] { "CarnivalEventId", "HouseCode" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Entry_IndividualOrTeam",
                table: "Entries",
                sql: "(\"StudentId\" IS NOT NULL AND \"HouseCode\" IS NULL) OR (\"StudentId\" IS NULL AND \"HouseCode\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_RelayLegs_EntryId_LegOrder",
                table: "RelayLegs",
                columns: new[] { "EntryId", "LegOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelayLegs_EntryId_StudentId",
                table: "RelayLegs",
                columns: new[] { "EntryId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelayLegs_RosterSnapshotId",
                table: "RelayLegs",
                column: "RosterSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_RelayLegs_StudentId",
                table: "RelayLegs",
                column: "StudentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelayLegs");

            migrationBuilder.DropIndex(
                name: "IX_Entries_CarnivalEventId_HouseCode",
                table: "Entries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Entry_IndividualOrTeam",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "EventDefinitions");

            migrationBuilder.DropColumn(
                name: "LegsJson",
                table: "EventDefinitions");

            migrationBuilder.DropColumn(
                name: "ScoringWeight",
                table: "EventDefinitions");

            migrationBuilder.DropColumn(
                name: "TeamSize",
                table: "EventDefinitions");

            migrationBuilder.DropColumn(
                name: "HouseCode",
                table: "Entries");

            migrationBuilder.AlterColumn<Guid>(
                name: "StudentId",
                table: "Entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RosterSnapshotId",
                table: "Entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
