using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class InAppRoleAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_SubjectId_Role_Scope",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "RoleAssignments");

            migrationBuilder.AlterColumn<string>(
                name: "SubjectId",
                table: "RoleAssignments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "RoleAssignments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RevocationReason",
                table: "RoleAssignments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAtUtc",
                table: "RoleAssignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedBySubject",
                table: "RoleAssignments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectUpn",
                table: "RoleAssignments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // The two defaults above exist only so adding a NOT NULL column to a
            // table that may already hold a row cannot abort the deploy. The model
            // carries no default, so drop them once the columns exist.
            migrationBuilder.Sql("ALTER TABLE \"RoleAssignments\" ALTER COLUMN \"Reason\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"RoleAssignments\" ALTER COLUMN \"SubjectUpn\" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_SubjectId",
                table: "RoleAssignments",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_SubjectUpn_Role",
                table: "RoleAssignments",
                columns: new[] { "SubjectUpn", "Role" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_SubjectId",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_SubjectUpn_Role",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "RevocationReason",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "RevokedBySubject",
                table: "RoleAssignments");

            migrationBuilder.DropColumn(
                name: "SubjectUpn",
                table: "RoleAssignments");

            migrationBuilder.AlterColumn<string>(
                name: "SubjectId",
                table: "RoleAssignments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveFrom",
                table: "RoleAssignments",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveTo",
                table: "RoleAssignments",
                type: "date",
                nullable: true);

            // Deliberately NOT unique, unlike the index this replaced: by rollback
            // time the table holds rows, every unbound one takes SubjectId '' from
            // the AlterColumn above, and a unique index would abort the rollback on
            // the first collision. A rollback must not be the thing that fails.
            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_SubjectId_Role_Scope",
                table: "RoleAssignments",
                columns: new[] { "SubjectId", "Role", "Scope" });
        }
    }
}
