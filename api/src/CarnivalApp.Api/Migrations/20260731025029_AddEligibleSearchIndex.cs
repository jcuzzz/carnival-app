using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEligibleSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RosterSnapshots_IsCurrent_CompetitionAgeGroup_CompetitionCa~",
                table: "RosterSnapshots",
                columns: new[] { "IsCurrent", "CompetitionAgeGroup", "CompetitionCategory" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RosterSnapshots_IsCurrent_CompetitionAgeGroup_CompetitionCa~",
                table: "RosterSnapshots");
        }
    }
}
