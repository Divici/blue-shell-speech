using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Practice.Infrastructure.Persistence.Migrations;

/// <summary>
/// Extends TR_ClinicalNotes_PreventDeletingRealNotes to refuse an AMENDMENT.
///
/// The trigger as first written asked two questions — is this a draft, and is it empty —
/// which is exactly what ClinicalNote.CanBeDiscarded and the DELETE endpoint asked. All
/// three therefore had the same hole, and a supported sequence walked through it: sign a
/// note, POST /notes/{id}/amend, PUT the amendment with four empty strings, DELETE it.
/// The amendment is a Draft with four empty sections, so every clause passed.
///
/// What that left behind is the reason this is a migration and not a comment. Amend()
/// marks the previous version Amended with IsCurrent = 0 BEFORE the new row exists, so
/// deleting the amendment leaves the visit with no current note: the schedule offers to
/// start a fresh one, GET /notes/appointment/{visit} answers 404, and a signed clinical
/// record is reachable by nothing the product renders. A signed note that no navigation
/// path can reach has been destroyed in every sense that matters to the person who needs
/// to read it.
///
/// A superseded version was already refused by the Status clause — it is Amended, not
/// Draft. This closes the other end of the same pair.
///
/// Forward-only and safe against the previous app revision (D022): during a rollout the
/// old code can still reach this DELETE, and it now answers with a refusal rather than a
/// deletion. Stricter is the safe direction for a schema change to move mid-deploy.
/// </summary>
public partial class AmendmentDeletionGuard : Migration
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
                 * Rejects the delete unless every deleted row was an empty ORIGINAL draft:
                 * Status 1 = Draft, nothing written in any section, and superseding
                 * nothing. A signed note, a superseded version, a draft with a single
                 * sentence in it, and an amendment are all records that stay.
                 *
                 * SupersedesNoteId IS NOT NULL is not covered by the other clauses. An
                 * amendment starts as a Draft and can be blanked by an ordinary edit, so
                 * without this line the strictest guard in the system agrees the row is
                 * disposable at precisely the moment deleting it strands the signed
                 * version underneath.
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
                       OR d.[SupersedesNoteId] IS NOT NULL
                       OR ISNULL(d.[Subjective], N'') <> N''
                       OR ISNULL(d.[Objective], N'')  <> N''
                       OR ISNULL(d.[Assessment], N'') <> N''
                       OR ISNULL(d.[Plan], N'')       <> N''
                )
                BEGIN
                    ROLLBACK TRANSACTION;
                    THROW 50002,
                        'A clinical note that has been written in, signed, or amended cannot be deleted. Amend it instead.',
                        1;
                END
            END
            """);
    }

    /// <summary>
    /// Restores the trigger as ClinicalNoteDeletionGuard left it, rather than dropping it.
    ///
    /// Down() undoes THIS migration, not the one before it. Dropping the trigger outright
    /// would roll a schema back to having no DELETE guard at all, which is a larger
    /// reversal than the one being asked for.
    /// </summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR ALTER TRIGGER TR_ClinicalNotes_PreventDeletingRealNotes
            ON dbo.ClinicalNotes
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;

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
}
