using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConsultationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsultationRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProviderId = table.Column<long>(type: "bigint", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ParentName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ChildFirstName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ChildAgeMonths = table.Column<short>(type: "smallint", nullable: false),
                    Concerns = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreferredContactMethod = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ConvertedPatientId = table.Column<long>(type: "bigint", nullable: true),
                    SourceIpHash = table.Column<string>(type: "char(64)", nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsultationRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsultationRequests_Patients_ConvertedPatientId",
                        column: x => x.ConvertedPatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultationRequests_ConvertedPatientId",
                table: "ConsultationRequests",
                column: "ConvertedPatientId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsultationRequests_ProviderId_Status_SubmittedAtUtc",
                table: "ConsultationRequests",
                columns: new[] { "ProviderId", "Status", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConsultationRequests_PublicId",
                table: "ConsultationRequests",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsultationRequests");
        }
    }
}
