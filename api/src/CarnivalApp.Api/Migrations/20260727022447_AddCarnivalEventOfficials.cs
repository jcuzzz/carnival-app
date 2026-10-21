using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCarnivalEventOfficials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedOfficialsJson",
                table: "CarnivalEvents");

            migrationBuilder.CreateTable(
                name: "CarnivalEventOfficials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssignedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarnivalEventOfficials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CarnivalEventOfficials_CarnivalEvents_CarnivalEventId",
                        column: x => x.CarnivalEventId,
                        principalTable: "CarnivalEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEventOfficials_CarnivalEventId_SubjectId",
                table: "CarnivalEventOfficials",
                columns: new[] { "CarnivalEventId", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEventOfficials_SubjectId",
                table: "CarnivalEventOfficials",
                column: "SubjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarnivalEventOfficials");

            migrationBuilder.AddColumn<string>(
                name: "AssignedOfficialsJson",
                table: "CarnivalEvents",
                type: "text",
                nullable: true);
        }
    }
}
