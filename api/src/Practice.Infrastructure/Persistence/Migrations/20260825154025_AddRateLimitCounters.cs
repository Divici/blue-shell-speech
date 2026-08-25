using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RateLimitCounters",
                columns: table => new
                {
                    PartitionKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    WindowEndsAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitCounters", x => x.PartitionKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitCounters_WindowEndsAtUtc",
                table: "RateLimitCounters",
                column: "WindowEndsAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RateLimitCounters");
        }
    }
}
