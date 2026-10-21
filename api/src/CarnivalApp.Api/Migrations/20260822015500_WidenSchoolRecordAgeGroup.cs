using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0028: a record category is named by the canonical <c>AgeScope</c> key,
    /// which is sized 64 on <c>CarnivalEvent</c> and <c>CarnivalDivision</c>. A
    /// wide merged band overflows 16 on PostgreSQL only. Additive and
    /// non-destructive — the table is empty in every environment.
    /// </summary>
    public partial class WidenSchoolRecordAgeGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "SchoolRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "SchoolRecords",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }
    }
}
