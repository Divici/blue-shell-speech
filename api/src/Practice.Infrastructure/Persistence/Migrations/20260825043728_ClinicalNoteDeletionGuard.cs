using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations;

/// <summary>
/// The database half of the discard rule.
///
/// One row may be deleted from this table: an unsigned draft with nothing written in any
/// section. That narrow case exists so a mis-tap on the day view is recoverable —
/// ClinicalNote.CanBeDiscarded, and the DELETE /notes/{id} endpoint.
///
/// Everything else is a clinical record. The aggregate refuses to hand one to Remove(),
/// and this refuses the DELETE that never went through the aggregate at all: a cleanup
/// script, a bulk operation, SSMS at 11pm. Same argument as
/// TR_ClinicalNotes_PreventSignedEdits, applied to the other verb.
/// </summary>
public partial class ClinicalNoteDeletionGuard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR ALTER TRIGGER TR_ClinicalNotes_PreventDeletingRealNotes
            ON dbo.ClinicalNotes
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;

                /*
                 * Rejects the delete unless every deleted row was an empty draft
                 * (Status 1 = Draft). A signed note, a superseded version, and a draft
                 * with a single sentence in it are all records that stay.
                 *
                 * ISNULL on the left because a comparison against NULL is UNKNOWN, and an
                 * UNKNOWN here would let a NULL-ed section through as if it were empty.
                 * SQL Server's string comparison ignores trailing spaces, so a section
                 * holding only spaces reads as empty — which matches the aggregate, where
                 * UpdateContent trims before storing.
                 */
                IF EXISTS (
                    SELECT 1
                    FROM deleted AS d
                    WHERE d.[Status] <> 1
                       OR ISNULL(d.[Subjective], N'') <> N''
                       OR ISNULL(d.[Objective], N'')  <> N''
                       OR ISNULL(d.[Assessment], N'') <> N''
                       OR ISNULL(d.[Plan], N'')       <> N''
                )
                BEGIN
                    ROLLBACK TRANSACTION;
                    THROW 50002,
                        'A clinical note that has been written in or signed cannot be deleted. Amend it instead.',
                        1;
                END
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ClinicalNotes_PreventDeletingRealNotes;");
    }
}
