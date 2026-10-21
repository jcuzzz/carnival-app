using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImportStagedRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StagedRowsJson",
                table: "ImportBatches",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StagedRowsJson",
                table: "ImportBatches");
        }
    }
}
