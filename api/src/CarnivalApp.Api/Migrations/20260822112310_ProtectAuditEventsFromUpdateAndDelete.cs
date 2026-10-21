using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// Audit rows are append-only through the API (proposal §9.3) and, from here,
    /// below it too. The application principal OWNS this schema, so REVOKE alone
    /// is theatre — it can grant itself back. A BEFORE UPDATE OR DELETE trigger
    /// refuses the statement whatever the grant says, and is the artefact the
    /// records-management gate asks for. PostgreSQL only: the SQLite dev loop
    /// builds its schema from the model with EnsureCreated and never runs
    /// migrations at all.
    /// </summary>
    public partial class ProtectAuditEventsFromUpdateAndDelete : Migration
    {
        private const string Postgres = "Npgsql.EntityFrameworkCore.PostgreSQL";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != Postgres) return;

            // CREATE FUNCTION, not CREATE OR REPLACE: the migration-drift checker
            // recognises the former and reports the latter as unaccounted SQL.
            migrationBuilder.Sql("""
                CREATE FUNCTION "AuditEvents_append_only"() RETURNS trigger AS $audit$
                BEGIN
                    RAISE EXCEPTION 'AuditEvents is append-only; % is refused', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $audit$ LANGUAGE plpgsql;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "AuditEvents_append_only"
                BEFORE UPDATE OR DELETE ON "AuditEvents"
                FOR EACH ROW EXECUTE FUNCTION "AuditEvents_append_only"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != Postgres) return;

            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "AuditEvents_append_only" ON "AuditEvents";""");
            migrationBuilder.Sql("""DROP FUNCTION IF EXISTS "AuditEvents_append_only"();""");
        }
    }
}
