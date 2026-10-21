using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CarnivalApp.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PriorValuesJson = table.Column<string>(type: "text", nullable: true),
                    NewValuesJson = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ImportBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApplicationVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RuleVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Carnivals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Venue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carnivals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sport = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MeasureType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Units = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DivisionsJson = table.Column<string>(type: "text", nullable: true),
                    EligibilityJson = table.Column<string>(type: "text", nullable: true),
                    RecordCategoriesJson = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyKeys",
                columns: table => new
                {
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResponseBody = table.Column<string>(type: "text", nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyKeys", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    SourceFileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImportedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ImportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidationResultsJson = table.Column<string>(type: "text", nullable: true),
                    NewCount = table.Column<int>(type: "integer", nullable: false),
                    ChangedCount = table.Column<int>(type: "integer", nullable: false),
                    InactiveCount = table.Column<int>(type: "integer", nullable: false),
                    UnmatchedCount = table.Column<int>(type: "integer", nullable: false),
                    RejectedCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true),
                    PromotedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Scope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchoolConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SchoolIdentifier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BrandingJson = table.Column<string>(type: "text", nullable: true),
                    HousesJson = table.Column<string>(type: "text", nullable: true),
                    SettingsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Students",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalStudentId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Surname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PreferredName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Students", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CarnivalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Category = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeatLaneSettingsJson = table.Column<string>(type: "text", nullable: true),
                    AssignedOfficialsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarnivalEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CarnivalEvents_Carnivals_CarnivalId",
                        column: x => x.CarnivalId,
                        principalTable: "Carnivals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CarnivalEvents_EventDefinitions_EventDefinitionId",
                        column: x => x.EventDefinitionId,
                        principalTable: "EventDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RosterSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchoolYear = table.Column<int>(type: "integer", nullable: false),
                    YearLevel = table.Column<int>(type: "integer", nullable: false),
                    HouseCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CompetitionAgeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompetitionCategory = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    HomeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ImportBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RosterSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RosterSnapshots_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RosterSnapshots_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarnivalEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Heat = table.Column<int>(type: "integer", nullable: true),
                    Lane = table.Column<int>(type: "integer", nullable: true),
                    RosterSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SnapshotYearLevel = table.Column<int>(type: "integer", nullable: false),
                    SnapshotHouseCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotAgeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SnapshotCategory = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    CategoryOverride = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: true),
                    CategoryOverrideActorSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CategoryOverrideAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CategoryOverrideReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entries_CarnivalEvents_CarnivalEventId",
                        column: x => x.CarnivalEventId,
                        principalTable: "CarnivalEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Entries_RosterSnapshots_RosterSnapshotId",
                        column: x => x.RosterSnapshotId,
                        principalTable: "RosterSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Entries_Students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "Students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeasuredValue = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    Place = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    LifecycleState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Points = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    CurrentRevision = table.Column<int>(type: "integer", nullable: false),
                    RecordedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Results_Entries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResultRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    PriorValuesJson = table.Column<string>(type: "text", nullable: false),
                    NewValuesJson = table.Column<string>(type: "text", nullable: false),
                    ActorSubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResultRevisions_Results_ResultId",
                        column: x => x.ResultId,
                        principalTable: "Results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SchoolRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgeGroup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Category = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordValue = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    HolderDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PreviousRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApprovedBySubject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovalNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchoolRecords_EventDefinitions_EventDefinitionId",
                        column: x => x.EventDefinitionId,
                        principalTable: "EventDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SchoolRecords_Results_ResultId",
                        column: x => x.ResultId,
                        principalTable: "Results",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SchoolRecords_SchoolRecords_PreviousRecordId",
                        column: x => x.PreviousRecordId,
                        principalTable: "SchoolRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorSubject",
                table: "AuditEvents",
                column: "ActorSubject");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AtUtc",
                table: "AuditEvents",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityId",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_CarnivalId_EventDefinitionId_AgeGroup_Catego~",
                table: "CarnivalEvents",
                columns: new[] { "CarnivalId", "EventDefinitionId", "AgeGroup", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarnivalEvents_EventDefinitionId",
                table: "CarnivalEvents",
                column: "EventDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Carnivals_Date",
                table: "Carnivals",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_CarnivalEventId_StudentId",
                table: "Entries",
                columns: new[] { "CarnivalEventId", "StudentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RosterSnapshotId",
                table: "Entries",
                column: "RosterSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_StudentId",
                table: "Entries",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_EventDefinitions_Code",
                table: "EventDefinitions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_CreatedAtUtc",
                table: "IdempotencyKeys",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_ImportedAtUtc",
                table: "ImportBatches",
                column: "ImportedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_Status",
                table: "ImportBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ResultRevisions_ResultId_RevisionNumber",
                table: "ResultRevisions",
                columns: new[] { "ResultId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Results_EntryId",
                table: "Results",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_LifecycleState",
                table: "Results",
                column: "LifecycleState");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_SubjectId_Role_Scope",
                table: "RoleAssignments",
                columns: new[] { "SubjectId", "Role", "Scope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RosterSnapshots_ImportBatchId",
                table: "RosterSnapshots",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_RosterSnapshots_SchoolYear",
                table: "RosterSnapshots",
                column: "SchoolYear");

            migrationBuilder.CreateIndex(
                name: "IX_RosterSnapshots_StudentId_ImportBatchId",
                table: "RosterSnapshots",
                columns: new[] { "StudentId", "ImportBatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RosterSnapshots_StudentId_IsCurrent",
                table: "RosterSnapshots",
                columns: new[] { "StudentId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_SchoolConfigurations_SchoolIdentifier",
                table: "SchoolConfigurations",
                column: "SchoolIdentifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRecords_EventDefinitionId_AgeGroup_Category_Status",
                table: "SchoolRecords",
                columns: new[] { "EventDefinitionId", "AgeGroup", "Category", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRecords_PreviousRecordId",
                table: "SchoolRecords",
                column: "PreviousRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRecords_ResultId",
                table: "SchoolRecords",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_ExternalStudentId",
                table: "Students",
                column: "ExternalStudentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "IdempotencyKeys");

            migrationBuilder.DropTable(
                name: "ResultRevisions");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "SchoolConfigurations");

            migrationBuilder.DropTable(
                name: "SchoolRecords");

            migrationBuilder.DropTable(
                name: "Results");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "CarnivalEvents");

            migrationBuilder.DropTable(
                name: "RosterSnapshots");

            migrationBuilder.DropTable(
                name: "Carnivals");

            migrationBuilder.DropTable(
                name: "EventDefinitions");

            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "Students");
        }
    }
}
