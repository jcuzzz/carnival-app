using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class UniqueResultPerEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Results_EntryId",
                table: "Results");

            migrationBuilder.CreateIndex(
                name: "IX_Results_EntryId",
                table: "Results",
                column: "EntryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Results_EntryId",
                table: "Results");

            migrationBuilder.CreateIndex(
                name: "IX_Results_EntryId",
                table: "Results",
                column: "EntryId");
        }
    }
}
