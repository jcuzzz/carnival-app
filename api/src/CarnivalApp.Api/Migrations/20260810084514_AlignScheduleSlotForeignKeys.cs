using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <summary>
    /// Aligns ONE deployed foreign key with the model that is supposed to
    /// describe it. Nothing in the model, the snapshot or any historical
    /// migration changes — this migration exists purely so the database and the
    /// model agree about <c>FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId</c>.
    ///
    /// THE DIVERGENCE. <c>CarnivalEvent.ScheduleSlot</c> is configured
    /// <c>DeleteBehavior.ClientSetNull</c> (CarnivalEventConfiguration, ADR-0014
    /// §1: deleting a program line must never delete competition, and a database
    /// SET NULL would give CarnivalEvents a second cascade path from Carnival,
    /// which SQL Server refuses at schema creation — ADR-0001 review note 5).
    /// EF Core 10 emits NO <c>onDelete</c> argument at all for ClientSetNull,
    /// i.e. NO ACTION. But <c>20260807024702_AddScheduleStreamsAndSlots</c> was
    /// HAND-EDITED to say <c>onDelete: ReferentialAction.Restrict</c>, so every
    /// database built from migrations (PostgreSQL, including the deployed
    /// carnival-app-host) got RESTRICT, while every database built from the model by
    /// <c>EnsureCreated</c> (the test suite, the SQLite dev loop) got NO ACTION.
    /// <c>has-pending-model-changes</c> cannot see this: the snapshot records the
    /// model's ClientSetNull, and the model matches it — the drift is between the
    /// migration and the model, which nothing compares automatically.
    ///
    /// WHY IT IS BEING FIXED ANYWAY. The effect is benign: NO ACTION and RESTRICT
    /// both refuse to delete a slot that still has events pointing at it, and
    /// differ only in WHEN the check runs (NO ACTION defers to end of statement,
    /// RESTRICT is immediate). Nothing in the codebase performs the
    /// multi-statement delete-then-repoint sequence where that difference is
    /// observable — the slot and stream delete endpoints unschedule their events
    /// first, in application code. The fix is for the TREE, not the runtime: a
    /// hand-edited migration is a misleading artefact, and a reviewer has already
    /// reasoned from this one as evidence of what EF generates, and been wrong.
    ///
    /// SCOPE. One foreign key. Every other FK in the schema was compared between
    /// `dotnet ef dbcontext script` (built from the model, what EnsureCreated
    /// makes) and `dotnet ef migrations script` (built from the migrations, what
    /// production has): 23 foreign keys, 22 identical, this one the sole
    /// divergence. In particular ADR-0015's <c>FK_Races_ScheduleSlots_ScheduleSlotId</c>
    /// and <c>FK_Entries_Races_RaceId</c> are already NO ACTION in both, because
    /// that migration was left exactly as EF scaffolded it.
    ///
    /// DEPLOYMENT. The re-add is validated: PostgreSQL re-checks every existing
    /// <c>CarnivalEvents.ScheduleSlotId</c> against ScheduleSlots and takes a
    /// brief ACCESS EXCLUSIVE lock on CarnivalEvents (and a SHARE ROW EXCLUSIVE
    /// on ScheduleSlots). At carnival scale — hundreds of event rows — that is
    /// milliseconds. It is still a schema change on a live database, so it runs
    /// through the ordinary backup-gated migration bundle, not on a carnival day.
    /// </summary>
    public partial class AlignScheduleSlotForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents");

            // No onDelete: exactly what EF Core 10 scaffolds for ClientSetNull,
            // and what `dotnet ef dbcontext script` puts in a database built from
            // this model. NO ACTION still refuses to orphan an event.
            migrationBuilder.AddForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents",
                column: "ScheduleSlotId",
                principalTable: "ScheduleSlots",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents");

            // Restores the hand-edited RESTRICT that AddScheduleStreamsAndSlots
            // applied, so Down is a true reverse of Up and a rollback lands back
            // on the schema the previous migration left behind.
            migrationBuilder.AddForeignKey(
                name: "FK_CarnivalEvents_ScheduleSlots_ScheduleSlotId",
                table: "CarnivalEvents",
                column: "ScheduleSlotId",
                principalTable: "ScheduleSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
