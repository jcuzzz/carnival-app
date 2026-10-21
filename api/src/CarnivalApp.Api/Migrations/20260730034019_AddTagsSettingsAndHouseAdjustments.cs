using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTagsSettingsAndHouseAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TagsJson",
                table: "EventDefinitions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettingsJson",
                table: "Carnivals",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HousePointsAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RecordedByRole = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Voided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VoidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HousePointsAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HousePointsAdjustments_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HousePointsAdjustments_CarnivalId",
                table: "HousePointsAdjustments",
                column: "CarnivalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HousePointsAdjustments");

            migrationBuilder.DropColumn(
                name: "TagsJson",
                table: "EventDefinitions");

            migrationBuilder.DropColumn(
                name: "SettingsJson",
                table: "Carnivals");
        }
    }
}
