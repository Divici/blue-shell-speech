using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ProviderId = table.Column<long>(type: "bigint", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EntityPublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Metadata = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityPublicId",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityPublicId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventType_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc",
                table: "AuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_PublicId",
                table: "AuditEvents",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");
        }
    }
}
