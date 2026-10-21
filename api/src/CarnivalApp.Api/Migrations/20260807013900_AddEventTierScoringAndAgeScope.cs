using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0014 §4 event classification: competition tier, per-event scoring mode
    /// and points-exclusion toggle, and the age axis generalised from one column
    /// to a set (canonical JSON + indexable key + display label).
    ///
    /// Hand-written, because the scaffolder read the column drop plus the column
    /// add as a RENAME of AgeGroup → ScoringMode, which would have moved "13"
    /// into the scoring mode and lost every division. The order below is the
    /// contract: add, BACKFILL, drop, re-index — so no row passes through a state
    /// where its division is unknown.
    /// </summary>
    public partial class AddEventTierScoringAndAgeScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old key ended at (…, AgeGroup, Category); it has to go before
            // AgeGroup can be dropped, and it is replaced at the end by the
            // tier-aware one.
            migrationBuilder.DropIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroup_Catego~",
                table: "CarnivalEvents");

            migrationBuilder.AddColumn<string>(
                name: "AgeGroupsJson",
                table: "CarnivalEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // "OPEN" is the canonical key of an event open across the age axis,
            // and the default is what a null AgeGroup row (an open relay) needs.
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
                name: "Tier",
                table: "CarnivalEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Competitive");

            // Null = "follow the tier"; every existing event stays on places.
            migrationBuilder.AddColumn<string>(
                name: "ScoringMode",
                table: "CarnivalEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScoresPoints",
                table: "CarnivalEvents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Deterministic backfill: every existing event scoped to exactly one
            // age group becomes the singleton set of that group, and its key is
            // the group itself — the values were already normalised (trimmed,
            // upper-cased) on write, so no re-normalisation is needed here.
            // Rows with a null AgeGroup keep AgeGroupsJson null and the "OPEN"
            // key default. String concatenation only, so the statement is valid
            // on both providers (ADR-0001 review note 5).
            migrationBuilder.Sql("""
                UPDATE "CarnivalEvents"
                SET "AgeGroupsJson" = '["' || "AgeGroup" || '"]',
                    "AgeGroupKey" = "AgeGroup"
                WHERE "AgeGroup" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "AgeGroup",
                table: "CarnivalEvents");

            // Pre-flight, because the new key is STRICTER than the old one and a
            // pre-existing row pair can make it unbuildable. Under the old
            // schema, an event with a NULL AgeGroup and an event with the literal
            // AgeGroup 'OPEN' were different divisions — NULLs compare distinct
            // in a unique index, and the duplicate check was plain equality — but
            // both collapse to the key 'OPEN' here. If any deployment holds such
            // a pair, CREATE UNIQUE INDEX would fail with a bare 23505 naming
            // only the index, and the API container would not start. This raises
            // the actual conflict instead, so the operator can merge or delete
            // the duplicate before retrying. Postgres-specific, like the column
            // types the scaffolder emits — migrations are per-provider artefacts;
            // the ADR-0001 note-5 portability rule is about application code.
            migrationBuilder.Sql("""
                DO $$
                DECLARE conflict text;
                BEGIN
                    -- Tier is omitted from the grouping deliberately: every row
                    -- is 'Competitive' at this point, so it cannot separate a pair.
                    SELECT string_agg(d.described, '; ') INTO conflict
                      FROM (SELECT format('carnival %s / definition %s / %s / %s (%s rows)',
                                          "CarnivalId", "EventDefinitionId", "AgeGroupKey",
                                          COALESCE("Category", '-'), count(*)) AS described
                              FROM "CarnivalEvents"
                             GROUP BY "CarnivalId", "EventDefinitionId", "AgeGroupKey", "Category"
                            HAVING count(*) > 1) AS d;
                    IF conflict IS NOT NULL THEN
                        RAISE EXCEPTION
                            'ADR-0014 migration: these events collapse to one division under the new age-scope key: %. Merge or remove the duplicates, then re-run.', conflict;
                    END IF;
                END $$;
                """);

            // Tier joins the uniqueness key (a Competitive and a Social 13
            // Freestyle are two events). Note this is also STRICTER than the key
            // it replaces: AgeGroupKey is non-null, so two open-age instances of
            // one definition now collide at the database instead of only in
            // application code.
            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroupKey_Cat~",
                table: "CarnivalEvents",
                columns: new[] { "CarnivalId", "EventDefinitionId", "AgeGroupKey", "Category", "Tier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroupKey_Cat~",
                table: "CarnivalEvents");

            migrationBuilder.AddColumn<string>(
                name: "AgeGroup",
                table: "CarnivalEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Reverse backfill: single-group scopes return to the old column;
            // a genuinely multi-group scope has no representation there, so it
            // reverts to open — the only lossless alternative would be to refuse
            // the downgrade, and a rollback must not be blocked by data. The
            // length predicate matters as much as the separator one: the key
            // column is four times wider than the one it is being copied into,
            // and a rollback is the worst moment to hit a 22001.
            migrationBuilder.Sql("""
                UPDATE "CarnivalEvents"
                SET "AgeGroup" = "AgeGroupKey"
                WHERE "AgeGroupKey" <> 'OPEN'
                  AND "AgeGroupKey" NOT LIKE '%-%'
                  AND length("AgeGroupKey") <= 16;
                """);

            migrationBuilder.DropColumn(name: "ScoresPoints", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "ScoringMode", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "Tier", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupLabel", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupKey", table: "CarnivalEvents");
            migrationBuilder.DropColumn(name: "AgeGroupsJson", table: "CarnivalEvents");

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroup_Catego~",
                table: "CarnivalEvents",
                columns: new[] { "CarnivalId", "EventDefinitionId", "AgeGroup", "Category" },
                unique: true);
        }
    }
}
