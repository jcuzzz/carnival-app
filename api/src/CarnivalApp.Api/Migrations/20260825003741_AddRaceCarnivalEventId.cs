using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRaceCarnivalEventId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CarnivalEventId",
                table: "Races",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Races_CarnivalEventId",
                table: "Races",
                column: "CarnivalEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_Races_CarnivalEvents_CarnivalEventId",
                table: "Races",
                column: "CarnivalEventId",
                principalTable: "CarnivalEvents",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Races_CarnivalEvents_CarnivalEventId",
                table: "Races");

            migrationBuilder.DropIndex(
                name: "IX_Races_CarnivalEventId",
                table: "Races");

            migrationBuilder.DropColumn(
                name: "CarnivalEventId",
                table: "Races");
        }
    }
}
