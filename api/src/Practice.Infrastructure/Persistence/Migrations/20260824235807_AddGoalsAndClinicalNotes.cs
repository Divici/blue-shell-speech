using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoalsAndClinicalNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClinicalNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    AppointmentId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    SupersedesNoteId = table.Column<long>(type: "bigint", nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Subjective = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Objective = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Assessment = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    SignedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SignedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AmendmentReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalNotes", x => x.Id);
                    table.CheckConstraint("CK_ClinicalNotes_AmendmentsHaveAReason", "([SupersedesNoteId] IS NULL) OR ([AmendmentReason] IS NOT NULL)");
                    table.CheckConstraint("CK_ClinicalNotes_SignedNotesAreAttested", "([Status] = 1) OR ([SignedAtUtc] IS NOT NULL AND [SignedBy] IS NOT NULL AND [ContentHash] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Goals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    GoalText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Domain = table.Column<int>(type: "int", nullable: false),
                    TargetCriteria = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CueLevelExpected = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AacModality = table.Column<int>(type: "int", nullable: true),
                    AacDeviceNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Goals", x => x.Id);
                    table.CheckConstraint("CK_Goals_AacFieldsOnlyOnAacGoals", "([Domain] = 7) OR ([AacModality] IS NULL AND [AacDeviceNotes] IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_PatientId_AppointmentId",
                table: "ClinicalNotes",
                columns: new[] { "PatientId", "AppointmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_PublicId",
                table: "ClinicalNotes",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalNotes_SupersedesNoteId",
                table: "ClinicalNotes",
                column: "SupersedesNoteId");

            migrationBuilder.CreateIndex(
                name: "UX_ClinicalNotes_OneCurrentPerAppointment",
                table: "ClinicalNotes",
                column: "AppointmentId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Goals_PatientId_Status",
                table: "Goals",
                columns: new[] { "PatientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Goals_PublicId",
                table: "Goals",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicalNotes");

            migrationBuilder.DropTable(
                name: "Goals");
        }
    }
}
