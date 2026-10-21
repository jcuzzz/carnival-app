using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// ADR-0027: the division profile leaves <c>Carnival.SettingsJson</c> for its
    /// own table. A DATA migration — existing profiles are read out of the blob,
    /// and the <c>Divisions</c> key is deliberately kept for one release.
    /// </summary>
    public partial class MoveDivisionProfileToItsOwnTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarnivalDivisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgeGroupKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgeGroupsJson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AgeGroupLabel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    Tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarnivalDivisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CarnivalDivisions_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_AgeGroupKey",
                table: "CarnivalDivisions",
                column: "AgeGroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalDivisions_CarnivalId_AgeGroupKey_Category_Tier",
                table: "CarnivalDivisions",
                columns: new[] { "CarnivalId", "AgeGroupKey", "Category", "Tier" },
                unique: true);

            migrationBuilder.Sql(ReadProfilesOutOfTheBlob);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(WriteProfilesBackIntoTheBlob);
            migrationBuilder.DropTable(
                name: "CarnivalDivisions");
        }

        /// <summary>
        /// Every carnival whose blob holds a readable profile gains its rows. Keys
        /// are matched case-insensitively and the age axis is canonicalised exactly
        /// as <c>AgeScope.Of</c> does — the key is a uniqueness constraint, so two
        /// spellings of one band must not become two bands. See ADR-0027's
        /// implementation notes for what is skipped and why.
        /// </summary>
        private const string ReadProfilesOutOfTheBlob = """
            INSERT INTO "CarnivalDivisions"
                ("Id", "CarnivalId", "AgeGroupKey", "AgeGroupsJson", "AgeGroupLabel",
                 "Category", "Tier", "SortOrder")
            WITH settings AS MATERIALIZED (
                -- pg_input_is_valid (PostgreSQL 16+) is the try-cast: a hand-edited
                -- blob that is not JSON reads as no settings at all today, and must
                -- not abort the migration. MATERIALIZED so the guard cannot be
                -- flattened into a WHERE, where AND has no evaluation order.
                SELECT c."Id" AS carnival_id,
                       CASE WHEN c."SettingsJson" IS NOT NULL
                                 AND pg_input_is_valid(c."SettingsJson", 'jsonb')
                            THEN c."SettingsJson"::jsonb END AS blob
                FROM "Carnivals" c
            ),
            profile AS (
                SELECT s.carnival_id,
                       (SELECT e.value FROM jsonb_each(
                            CASE WHEN jsonb_typeof(s.blob) = 'object'
                                 THEN s.blob ELSE '{}'::jsonb END) e
                        WHERE lower(e.key) = 'divisions' ORDER BY e.key LIMIT 1) AS d
                FROM settings s
            ),
            axes AS (
                SELECT p.carnival_id,
                       (SELECT e.value FROM jsonb_each(p.d) e
                        WHERE lower(e.key) = 'agebands' ORDER BY e.key LIMIT 1) AS age_bands,
                       (SELECT e.value FROM jsonb_each(p.d) e
                        WHERE lower(e.key) = 'categories' ORDER BY e.key LIMIT 1) AS categories,
                       (SELECT e.value FROM jsonb_each(p.d) e
                        WHERE lower(e.key) = 'tiers' ORDER BY e.key LIMIT 1) AS tiers
                FROM profile p
                WHERE jsonb_typeof(p.d) = 'object'
            ),
            declared AS (
                SELECT a.carnival_id,
                       CASE WHEN jsonb_typeof(a.age_bands) = 'array'
                            THEN jsonb_array_length(a.age_bands) ELSE 0 END AS band_count
                FROM axes a
            ),
            band AS (
                SELECT a.carnival_id,
                       b.ord - 1 AS band_ord,
                       btrim(coalesce((
                           SELECT e.value #>> '{}' FROM jsonb_each(b.value) e
                           WHERE lower(e.key) = 'label' ORDER BY e.key LIMIT 1), '')) AS label,
                       coalesce((
                           SELECT CASE WHEN jsonb_typeof(e.value) = 'array' THEN e.value END
                           FROM jsonb_each(b.value) e
                           WHERE lower(e.key) = 'groups' ORDER BY e.key LIMIT 1), '[]'::jsonb) AS groups
                FROM axes a
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(a.age_bands) = 'array'
                         THEN a.age_bands ELSE '[]'::jsonb END) WITH ORDINALITY AS b(value, ord)
                WHERE jsonb_typeof(b.value) = 'object'
            ),
            code AS (
                SELECT DISTINCT b.carnival_id, b.band_ord, b.label,
                       upper(btrim(g.value #>> '{}')) AS code
                FROM band b
                CROSS JOIN LATERAL jsonb_array_elements(b.groups) AS g(value)
                WHERE btrim(coalesce(g.value #>> '{}', '')) <> ''
            ),
            scope AS (
                SELECT c.carnival_id, c.band_ord, c.label,
                       bool_or(c.code = 'OPEN') AS is_open,
                       -- COLLATE "C" is StringComparer.Ordinal; the database's own
                       -- collation would order a mixed-alphabet band differently.
                       string_agg(c.code, '-' ORDER BY c.rank, c.code COLLATE "C") AS narrow_key,
                       '[' || string_agg(to_jsonb(c.code)::text, ','
                                         ORDER BY c.rank, c.code COLLATE "C") || ']' AS narrow_groups
                FROM (
                    SELECT code.*,
                           -- int.TryParse ranks numerics ahead of everything else;
                           -- anything it would refuse sorts with the non-numerics.
                           CASE WHEN code.code ~ '^[0-9]{1,9}$' THEN code.code::bigint
                                ELSE 2147483647 END AS rank
                    FROM code
                ) c
                GROUP BY c.carnival_id, c.band_ord, c.label
            ),
            age_band AS (
                SELECT s.carnival_id, s.band_ord,
                       CASE WHEN s.is_open THEN 'OPEN' ELSE s.narrow_key END AS age_key,
                       CASE WHEN s.is_open THEN NULL ELSE s.narrow_groups END AS groups_json,
                       CASE WHEN s.label <> '' THEN s.label
                            WHEN s.is_open THEN 'OPEN'
                            ELSE s.narrow_key END AS label
                FROM scope s
            ),
            category AS (
                SELECT a.carnival_id,
                       upper(btrim(c.value #>> '{}')) AS category,
                       min(c.ord) AS category_ord
                FROM axes a
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(a.categories) = 'array'
                         THEN a.categories ELSE '[]'::jsonb END) WITH ORDINALITY AS c(value, ord)
                WHERE upper(btrim(coalesce(c.value #>> '{}', ''))) IN ('M', 'F', 'S')
                GROUP BY a.carnival_id, upper(btrim(c.value #>> '{}'))
            ),
            tier AS (
                SELECT a.carnival_id,
                       initcap(lower(btrim(t.value #>> '{}'))) AS tier,
                       min(t.ord) AS tier_ord
                FROM axes a
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(a.tiers) = 'array'
                         THEN a.tiers ELSE '[]'::jsonb END) WITH ORDINALITY AS t(value, ord)
                WHERE lower(btrim(coalesce(t.value #>> '{}', ''))) IN ('competitive', 'social')
                GROUP BY a.carnival_id, initcap(lower(btrim(t.value #>> '{}')))
            ),
            usable AS (
                SELECT d.carnival_id
                FROM declared d
                JOIN age_band a ON a.carnival_id = d.carnival_id
                GROUP BY d.carnival_id, d.band_count
                HAVING count(*) = d.band_count
                   AND count(DISTINCT a.age_key) = count(*)
                   AND max(length(a.age_key)) <= 64
                   AND coalesce(max(length(a.groups_json)), 0) <= 200
                   AND max(length(a.label)) <= 32
                   AND EXISTS (SELECT 1 FROM category c WHERE c.carnival_id = d.carnival_id)
                   AND EXISTS (SELECT 1 FROM tier t WHERE t.carnival_id = d.carnival_id)
            )
            SELECT gen_random_uuid(), a.carnival_id, a.age_key, a.groups_json, a.label,
                   c.category, t.tier,
                   (row_number() OVER (PARTITION BY a.carnival_id
                                       ORDER BY a.band_ord, c.category_ord, t.tier_ord) - 1)::int
            FROM age_band a
            JOIN usable u ON u.carnival_id = a.carnival_id
            JOIN category c ON c.carnival_id = a.carnival_id
            JOIN tier t ON t.carnival_id = a.carnival_id;
            """;

        /// <summary>
        /// The reverse: rebuild the <c>Divisions</c> key from the rows, in the
        /// PascalCase shape the pre-move writer produced, dropping any case variant
        /// first. A profile that no longer fits the 4000-character column makes the
        /// rollback FAIL rather than truncate.
        /// </summary>
        private const string WriteProfilesBackIntoTheBlob = """
            WITH band AS (
                SELECT d."CarnivalId" AS carnival_id, d."AgeGroupKey" AS age_key,
                       min(d."SortOrder") AS band_ord,
                       min(d."AgeGroupLabel") AS label,
                       min(d."AgeGroupsJson") AS groups_json
                FROM "CarnivalDivisions" d
                GROUP BY d."CarnivalId", d."AgeGroupKey"
            ),
            category AS (
                SELECT d."CarnivalId" AS carnival_id, d."Category" AS category,
                       min(d."SortOrder") AS category_ord
                FROM "CarnivalDivisions" d
                GROUP BY d."CarnivalId", d."Category"
            ),
            tier AS (
                SELECT d."CarnivalId" AS carnival_id, d."Tier" AS tier,
                       min(d."SortOrder") AS tier_ord
                FROM "CarnivalDivisions" d
                GROUP BY d."CarnivalId", d."Tier"
            ),
            profile AS (
                SELECT b.carnival_id,
                       jsonb_build_object(
                           'AgeBands', (
                               SELECT jsonb_agg(jsonb_build_object(
                                          'Label', x.label,
                                          'Groups', coalesce(x.groups_json::jsonb, '["OPEN"]'::jsonb))
                                      ORDER BY x.band_ord)
                               FROM band x WHERE x.carnival_id = b.carnival_id),
                           'Categories', (
                               SELECT jsonb_agg(y.category ORDER BY y.category_ord)
                               FROM category y WHERE y.carnival_id = b.carnival_id),
                           'Tiers', (
                               SELECT jsonb_agg(z.tier ORDER BY z.tier_ord)
                               FROM tier z WHERE z.carnival_id = b.carnival_id)) AS d
                FROM band b
                GROUP BY b.carnival_id
            ),
            target AS (
                SELECT c."Id" AS carnival_id,
                       CASE WHEN c."SettingsJson" IS NOT NULL
                                 AND pg_input_is_valid(c."SettingsJson", 'jsonb')
                            THEN CASE WHEN jsonb_typeof(c."SettingsJson"::jsonb) = 'object'
                                      THEN c."SettingsJson"::jsonb ELSE '{}'::jsonb END
                            ELSE '{}'::jsonb END AS blob
                FROM "Carnivals" c
            ),
            restored AS (
                SELECT t.carnival_id,
                       ((t.blob - ARRAY(SELECT e.key FROM jsonb_each(t.blob) e
                                        WHERE lower(e.key) = 'divisions'))
                        || jsonb_build_object('Divisions', p.d))::text AS settings
                FROM target t
                JOIN profile p ON p.carnival_id = t.carnival_id
            )
            UPDATE "Carnivals" c
            SET "SettingsJson" = r.settings
            FROM restored r
            WHERE c."Id" = r.carnival_id;
            """;
    }
}
