using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations;

/// <summary>
/// The database half of note immutability.
///
/// ClinicalNote already refuses to edit a signed note, and that holds for every write that
/// goes through the application. This trigger holds for the ones that do not: a migration
/// script, a bulk update, SSMS at 11pm, a future developer who reaches for
/// ExecuteUpdateAsync because it is faster.
///
/// docs/DATA_MODEL.md: "Application-layer immutability survives exactly until someone
/// writes a migration script or opens SSMS."
/// </summary>
public partial class ClinicalNoteImmutabilityTrigger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR ALTER TRIGGER TR_ClinicalNotes_PreventSignedEdits
            ON dbo.ClinicalNotes
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;

                /*
                 * Rejects any change to attested content on a note that was NOT a draft
                 * before the update (Status 1 = Draft).
                 *
                 * Signing itself is permitted: the row is a Draft at that moment, so this
                 * does not fire. Superseding is permitted too — Amend() flips IsCurrent
                 * and Status on the previous version without touching its content, and
                 * only content and signature columns are guarded here.
                 *
                 * ISNULL on both sides because SQL comparisons against NULL are UNKNOWN,
                 * and an UNKNOWN in this predicate would let a NULL-ing update through.
                 */
                IF EXISTS (
                    SELECT 1
                    FROM deleted AS d
                    INNER JOIN inserted AS i ON d.Id = i.Id
                    WHERE d.[Status] <> 1
                      AND (
                            ISNULL(d.[Subjective], N'')  <> ISNULL(i.[Subjective], N'')
                         OR ISNULL(d.[Objective], N'')   <> ISNULL(i.[Objective], N'')
                         OR ISNULL(d.[Assessment], N'')  <> ISNULL(i.[Assessment], N'')
                         OR ISNULL(d.[Plan], N'')        <> ISNULL(i.[Plan], N'')
                         OR ISNULL(d.[ContentHash], 0x)  <> ISNULL(i.[ContentHash], 0x)
                         OR ISNULL(d.[SignedBy], N'')    <> ISNULL(i.[SignedBy], N'')
                         OR ISNULL(d.[SignedAtUtc], '1900-01-01')
                              <> ISNULL(i.[SignedAtUtc], '1900-01-01')
                      )
                )
                BEGIN
                    ROLLBACK TRANSACTION;
                    THROW 50001,
                        'A signed clinical note cannot be modified. Create an amendment instead.',
                        1;
                END
            END
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_ClinicalNotes_PreventSignedEdits;");
    }
}
