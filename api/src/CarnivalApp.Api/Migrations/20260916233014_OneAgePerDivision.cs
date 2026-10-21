using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0058 §5: the age axis of a division goes back to ONE code. Both
    /// division tables trade <c>AgeGroupsJson</c> + <c>AgeGroupKey</c> +
    /// <c>AgeGroupLabel</c> for a single <c>AgeGroup</c>, <c>SchoolRecords</c>
    /// narrows to 16 again, and the two unique indexes are rebuilt on the new
    /// column.
    ///
    /// Hand-edited: the scaffolder drops the three columns before adding the one,
    /// which loses every division. The order below is the contract — REFUSE,
    /// add, BACKFILL, drop, tighten, re-index — so no row passes through a state
    /// where its division is unknown. Migrations are Npgsql artefacts (the SQLite
    /// dev loop and the tests build from the model with EnsureCreated), so the
    /// guard is written in PL/pgSQL like the ADR-0014 one it undoes.
    /// </summary>
    public partial class OneAgePerDivision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A merged row cannot be carried across: "15-16" is not an age any
            // roster derives, so carrying it would make every entry in that
            // division ineligible on deck day, silently. Phase 1 is synthetic
            // data and both shipped templates are single-age, so the operator
            // removes the merged divisions and re-runs.
            migrationBuilder.Sql("""
                DO $$
                DECLARE merged text;
                BEGIN
                    SELECT string_agg(d.described, '; ') INTO merged
                      FROM (SELECT format('CarnivalEvents.%s (%s rows)', "AgeGroupKey", count(*)) AS described
                              FROM "CarnivalEvents" WHERE "AgeGroupKey" LIKE '%-%'
                             GROUP BY "AgeGroupKey"
                             UNION ALL
                            SELECT format('CarnivalDivisions.%s (%s rows)', "AgeGroupKey", count(*))
                              FROM "CarnivalDivisions" WHERE "AgeGroupKey" LIKE '%-%'
                             GROUP BY "AgeGroupKey"
                             UNION ALL
                            SELECT format('SchoolRecords.%s (%s rows)', "AgeGroup", count(*))
                              FROM "SchoolRecords" WHERE "AgeGroup" LIKE '%-%'
                             GROUP BY "AgeGroup") AS d;
                    IF merged IS NOT NULL THEN
                        RAISE EXCEPTION
                            'ADR-0058 migration: these rows hold a merged age scope, which one age per division cannot represent: %. Remove those divisions (and any records they set), then re-run.', merged;
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "AgeGroup",
                table: "CarnivalEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeGroup",
                table: "CarnivalDivisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // The key IS the age once no merged row is left — "13", "SNR" or the
            // OPEN sentinel, all of them already trimmed and upper-cased on write.
            migrationBuilder.Sql("""
                UPDATE "CarnivalEvents" SET "AgeGroup" = "AgeGroupKey";
                UPDATE "CarnivalDivisions" SET "AgeGroup" = "AgeGroupKey";
                """);

            migrationBuilder.DropIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroupKey_Cat~",
                table: "CarnivalEvents");

            migrationBuilder.DropIndex(
                name: "IX_CarnivalDivisions_AgeGroupKey",
                table: "CarnivalDivisions");

            migrationBuilder.DropIndex(
                name: "IX_CarnivalDivisions_CarnivalId_AgeGroupKey_Category_Tier",
                table: "CarnivalDivisions");

            migrationBuilder.DropColumn(name: "AgeGroupKey", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupLabel", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupsJson", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupKey", table: "CarnivalDivisions");
            migrationBuilder.DropColumn(name: "AgeGroupLabel", table: "CarnivalDivisions");
            migrationBuilder.DropColumn(name: "AgeGroupsJson", table: "CarnivalDivisions");

            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "CarnivalEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "CarnivalDivisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            // Safe only after the guard above: a merged key is the one thing that
            // could exceed 16 here (ADR-0028 widened it for exactly that).
            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "SchoolRecords",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroup_Catego~",
                table: "CarnivalEvents",
                columns: new[] { "CarnivalId", "EventDefinitionId", "AgeGroup", "Category", "Tier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_AgeGroup",
                table: "CarnivalDivisions",
                column: "AgeGroup");

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_CarnivalId_AgeGroup_Category_Tier",
                table: "CarnivalDivisions",
                columns: new[] { "CarnivalId", "AgeGroup", "Category", "Tier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroup_Catego~",
                table: "CarnivalEvents");

            migrationBuilder.DropIndex(
                name: "IX_CarnivalDivisions_AgeGroup",
                table: "CarnivalDivisions");

            migrationBuilder.DropIndex(
                name: "IX_CarnivalDivisions_CarnivalId_AgeGroup_Category_Tier",
                table: "CarnivalDivisions");

            migrationBuilder.AlterColumn<string>(
                name: "AgeGroup",
                table: "SchoolRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupKey",
                table: "CarnivalEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "OPEN");

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupLabel",
                table: "CarnivalEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupsJson",
                table: "CarnivalEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupKey",
                table: "CarnivalDivisions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "OPEN");

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupLabel",
                table: "CarnivalDivisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "OPEN");

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupsJson",
                table: "CarnivalDivisions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // The set form of one age is its singleton; OPEN keeps a null set,
            // which is what "open across the axis" was stored as. A division's
            // label was the key when staff called it nothing else, and an event's
            // was null in that case.
            migrationBuilder.Sql("""
                UPDATE "CarnivalEvents"
                SET "AgeGroupKey" = "AgeGroup",
                    "AgeGroupsJson" = CASE WHEN "AgeGroup" <> 'OPEN'
                                           THEN '["' || "AgeGroup" || '"]' END,
                    "AgeGroupLabel" = NULL;
                UPDATE "CarnivalDivisions"
                SET "AgeGroupKey" = "AgeGroup",
                    "AgeGroupsJson" = CASE WHEN "AgeGroup" <> 'OPEN'
                                           THEN '["' || "AgeGroup" || '"]' END,
                    "AgeGroupLabel" = "AgeGroup";
                """);

            migrationBuilder.DropColumn(name: "AgeGroup", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroup", table: "CarnivalDivisions");

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroupKey_Cat~",
                table: "CarnivalEvents",
                columns: new[] { "CarnivalId", "EventDefinitionId", "AgeGroupKey", "Category", "Tier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_AgeGroupKey",
                table: "CarnivalDivisions",
                column: "AgeGroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_CarnivalId_AgeGroupKey_Category_Tier",
                table: "CarnivalDivisions",
                columns: new[] { "CarnivalId", "AgeGroupKey", "Category", "Tier" },
                unique: true);
        }
    }
}
