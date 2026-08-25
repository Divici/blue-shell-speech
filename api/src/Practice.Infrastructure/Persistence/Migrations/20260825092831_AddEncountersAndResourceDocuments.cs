using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncountersAndResourceDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Encounters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    AppointmentId = table.Column<long>(type: "bigint", nullable: false),
                    RenderingProviderId = table.Column<long>(type: "bigint", nullable: false),
                    ClinicalNoteId = table.Column<long>(type: "bigint", nullable: true),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CptCode = table.Column<string>(type: "varchar(5)", nullable: false),
                    Modifiers = table.Column<string>(type: "varchar(11)", nullable: true),
                    PlaceOfService = table.Column<int>(type: "int", nullable: false),
                    Units = table.Column<short>(type: "smallint", nullable: false),
                    ChargeAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    PaymentStatus = table.Column<int>(type: "int", nullable: false),
                    PaymentMethod = table.Column<int>(type: "int", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SuperbillGeneratedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Encounters", x => x.Id);
                    table.CheckConstraint("CK_Encounters_MoneyIsNotNegative", "[ChargeAmount] >= 0 AND [AmountPaid] >= 0");
                    table.CheckConstraint("CK_Encounters_PaymentsCarryADate", "([AmountPaid] = 0 AND [PaidAtUtc] IS NULL) OR ([AmountPaid] > 0 AND [PaidAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_Encounters_UnitsAreBillable", "[Units] >= 1 AND [Units] <= 16");
                    table.ForeignKey(
                        name: "FK_Encounters_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounters_ClinicalNotes_ClinicalNoteId",
                        column: x => x.ClinicalNoteId,
                        principalTable: "ClinicalNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounters_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Encounters_Providers_RenderingProviderId",
                        column: x => x.RenderingProviderId,
                        principalTable: "Providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourceDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Slug = table.Column<string>(type: "varchar(80)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BlobUri = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    ContentUpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    WithdrawnAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SortOrder = table.Column<short>(type: "smallint", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceDocuments", x => x.Id);
                    table.CheckConstraint("CK_ResourceDocuments_FileHasBytes", "[FileSizeBytes] > 0 AND [FileSizeBytes] <= 20971520");
                    table.CheckConstraint("CK_ResourceDocuments_PublishedRowsCarryADate", "([IsPublished] = 0) OR ([PublishedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_ResourceDocuments_RevisionsStartAtOne", "[RevisionNumber] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "EncounterDiagnoses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    EncounterId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<short>(type: "smallint", nullable: false),
                    Code = table.Column<string>(type: "varchar(8)", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncounterDiagnoses", x => x.Id);
                    table.CheckConstraint("CK_EncounterDiagnoses_SequenceIsAPointer", "[Sequence] >= 1 AND [Sequence] <= 4");
                    table.ForeignKey(
                        name: "FK_EncounterDiagnoses_Encounters_EncounterId",
                        column: x => x.EncounterId,
                        principalTable: "Encounters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EncounterDiagnoses_PublicId",
                table: "EncounterDiagnoses",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EncounterDiagnoses_OnePerPosition",
                table: "EncounterDiagnoses",
                columns: new[] { "EncounterId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EncounterDiagnoses_OneRowPerCode",
                table: "EncounterDiagnoses",
                columns: new[] { "EncounterId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_AppointmentId",
                table: "Encounters",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_ClinicalNoteId",
                table: "Encounters",
                column: "ClinicalNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_PatientId_ServiceDate",
                table: "Encounters",
                columns: new[] { "PatientId", "ServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_ProviderId_ServiceDate",
                table: "Encounters",
                columns: new[] { "ProviderId", "ServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_PublicId",
                table: "Encounters",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Encounters_RenderingProviderId",
                table: "Encounters",
                column: "RenderingProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceDocuments_ProviderId_IsPublished_SortOrder",
                table: "ResourceDocuments",
                columns: new[] { "ProviderId", "IsPublished", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceDocuments_PublicId",
                table: "ResourceDocuments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ResourceDocuments_OneDocumentPerPublicUrl",
                table: "ResourceDocuments",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncounterDiagnoses");

            migrationBuilder.DropTable(
                name: "ResourceDocuments");

            migrationBuilder.DropTable(
                name: "Encounters");
        }
    }
}
