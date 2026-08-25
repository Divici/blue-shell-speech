using BlueShell.DemoSeed;
using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Common;
using Practice.Domain.Consultations;
using Practice.Domain.Goals;
using Practice.Domain.Patients;
using Practice.Domain.Providers;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The demo seeder, against real SQL Server.
///
/// It has to run here rather than anywhere cheaper, because most of what it claims is a
/// claim about the DATABASE: the filtered unique index that permits one current note per
/// visit, the CHECK that keeps AAC fields off non-AAC goals, the UPDATE trigger that
/// refuses to let a signed note change, and the provider query filter every idempotency
/// lookup runs through. An in-memory provider fakes all four away (D020), and a seeder that
/// passes against a fake and fails against Azure SQL is worse than no seeder at all — it
/// fails in front of an audience.
///
/// Each test seeds its own PROVIDER, so the shared database's other rows are invisible
/// through the filter and the roster lookups are honest.
///
/// Every name these tests write is invented (CLAUDE.md non-negotiable #1). They assert on
/// the SHAPE of the fixture, never on a clinical sentence, so a roster edit does not
/// cascade into ten broken assertions.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class DemoSeedTests(SqlServerFixture sql)
{
    /// <summary>
    /// A fixed instant: 2026-08-25 14:30 UTC, which is 10:30 in Maryland.
    ///
    /// Mid-morning on purpose. The roster's day runs 08:15 to 19:00 practice-local, so at
    /// this instant one visit is over, one is under way, and three are still ahead — which
    /// is what gives the documentation gate something to allow AND something to refuse
    /// without either being contrived.
    /// </summary>
    private static readonly DateTime Now = new(2026, 8, 25, 14, 30, 0, DateTimeKind.Utc);

    private static DbContextOptions<PracticeDbContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<PracticeDbContext>()
            .UseSqlServer(connectionString)
            .Options;

    /// <summary>A provider of this test's own, so the query filter isolates it.</summary>
    private static async Task<(long Id, string DisplayName)> SeedProviderAsync(
        string connectionString, string displayName = "Michelle Demo")
    {
        await using var db = new PracticeDbContext(
            OptionsFor(connectionString), new FixedProviderContext(null));

        var provider = Provider.Create(
            $"demo-{Guid.NewGuid():N}", displayName, "M.S., CCC-SLP", "SLP-DEMO", "MD");

        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return (provider.Id, provider.DisplayName);
    }

    private static PracticeDbContext ReaderFor(string connectionString, long providerId) =>
        new(OptionsFor(connectionString), new FixedProviderContext(providerId));

    private async Task<DemoSeedReport> SeedAsync(long providerId, string displayName)
    {
        await using var seeder = new DemoSeeder(
            OptionsFor(sql.ConnectionString), providerId, displayName);

        var refusal = await seeder.RefusalReasonAsync(CancellationToken.None);
        Assert.Null(refusal);

        return await seeder.SeedAsync(Now, CancellationToken.None);
    }

    // --------------------------------------------------------------- idempotency

    /// <summary>
    /// David may run this more than once — before a demo, and again when it goes wrong.
    ///
    /// The second run must create nothing and throw nothing. Throwing is the likelier of the
    /// two failures: a seeder that re-inserted would hit its own roster index or
    /// UX_ClinicalNotes_OneCurrentPerAppointment long before it finished quietly doubling
    /// the caseload — so both halves are asserted, the report AND the row counts.
    ///
    /// Control: the existing-patient check at the top of DemoSeeder.SeedPatientsAsync (the
    /// `if (existing.Any(...)) continue;`). Deleted → red inside the second run with
    /// "System.ArgumentException : An item with the same key has already been added. Key:
    /// Quillfeather".
    ///
    /// Worth recording that the failure is NOT the one this docstring first predicted. The
    /// guess was the filtered unique index; what actually fires first is
    /// LoadRosterPatientsAsync's ToDictionary, because the second run duplicates the
    /// patients three phases before it reaches a note. Both are real, and the surname index
    /// is the earlier of the two — which is the whole reason D070 says to run the deletion
    /// rather than reason about it.
    /// </summary>
    [Fact]
    public async Task Seeding_the_same_database_twice_creates_nothing_the_second_time()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);

        var first = await SeedAsync(providerId, displayName);
        Assert.True(first.Total > 0);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var before = await CountEverythingAsync(reader);

        var second = await SeedAsync(providerId, displayName);

        Assert.Equal(0, second.Total);
        Assert.Equal(before, await CountEverythingAsync(reader));
    }

    /// <summary>
    /// The report is what the operator reads to decide whether the demo is ready, so it has
    /// to describe the database rather than the intent.
    ///
    /// Control: the `created++` in DemoSeeder.SeedGoalsAsync. Deleted → red on the goals
    /// assertion, "Assert.Equal() Failure: Values differ / Expected: 20 / Actual: 0".
    /// </summary>
    [Fact]
    public async Task The_report_counts_exactly_what_the_run_wrote()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);

        var report = await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var ct = CancellationToken.None;

        Assert.Equal(await reader.Patients.CountAsync(ct), report.Patients);
        Assert.Equal(await reader.Guardians.CountAsync(ct), report.Guardians);
        Assert.Equal(await reader.PatientAddresses.CountAsync(ct), report.Addresses);
        Assert.Equal(await reader.Goals.CountAsync(ct), report.Goals);
        Assert.Equal(await reader.Appointments.CountAsync(ct), report.Visits);
        Assert.Equal(await reader.ClinicalNotes.CountAsync(ct), report.Notes);
        Assert.Equal(await reader.ConsultationRequests.CountAsync(ct), report.Enquiries);

        // A floor, not an exact total: the roster grows, and a test that pins its size is a
        // test about the day it was written. What must not change is that every screen has
        // something to show.
        Assert.True(report.Patients >= 6, $"only {report.Patients} patients seeded");
        Assert.True(report.Guardians >= report.Patients, "every patient needs a guardian");
        Assert.True(report.Goals >= report.Patients, "every patient needs a goal");
        Assert.True(report.Visits >= 10, $"only {report.Visits} visits seeded");
        Assert.True(report.Notes >= 4, $"only {report.Notes} notes seeded");
        Assert.Equal(4, report.Enquiries);
    }

    // ------------------------------------------------------------------- tenancy

    /// <summary>
    /// ProviderId on every domain row from day one, even at one provider (CLAUDE.md
    /// conventions). A seeder is the easiest place in the system to break that, because it
    /// is the only writer that is not a request with a caller attached to it.
    ///
    /// Rows are identified by id watermark rather than by "everything this provider owns",
    /// which would ask the question by assuming the answer.
    ///
    /// Control: `ProviderId = providerId,` in Guardian.Create (Practice.Domain). Deleted →
    /// red, with this test's own message: "Guardians rows carrying the wrong provider: 11".
    /// </summary>
    [Fact]
    public async Task Every_row_the_seed_writes_carries_the_provider_it_was_given()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        var ct = CancellationToken.None;

        await using var all = new PracticeDbContext(
            OptionsFor(sql.ConnectionString), new FixedProviderContext(null));

        var watermark = new Watermark(
            await MaxIdAsync(all.Patients.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.Guardians.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.PatientAddresses.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.Goals.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.Appointments.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.ClinicalNotes.IgnoreQueryFilters().Select(x => x.Id), ct),
            await MaxIdAsync(all.ConsultationRequests.IgnoreQueryFilters().Select(x => x.Id), ct));

        await SeedAsync(providerId, displayName);

        await AssertAllCarryProviderAsync(
            "Patients",
            all.Patients.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Patients).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "Guardians",
            all.Guardians.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Guardians).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "PatientAddresses",
            all.PatientAddresses.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Addresses).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "Goals",
            all.Goals.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Goals).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "Appointments",
            all.Appointments.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Visits).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "ClinicalNotes",
            all.ClinicalNotes.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Notes).Select(x => x.ProviderId), providerId, ct);

        await AssertAllCarryProviderAsync(
            "ConsultationRequests",
            all.ConsultationRequests.IgnoreQueryFilters()
                .Where(x => x.Id > watermark.Enquiries).Select(x => x.ProviderId), providerId, ct);
    }

    // ----------------------------------------------------------- production guard

    /// <summary>
    /// The run-time half of the production guard (D099).
    ///
    /// The structural half — that nothing shipped references this project — cannot be
    /// asserted by a test, because it is the ABSENCE of a reference. This is the half that
    /// can: a database holding one record the roster does not name is refused before the
    /// first insert, so pointing the tool at a real caseload writes nothing.
    ///
    /// Control: the `strangers` clause in DemoSeeder.RefusalReasonAsync (the `.Count(p =>
    /// !roster.Contains(...))` and the `strangers == 0` half of the guard). Deleted → red on
    /// the first assertion, "Assert.NotNull() Failure: Value is null".
    /// </summary>
    [Fact]
    public async Task A_database_holding_a_record_the_roster_does_not_name_is_refused()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        var ct = CancellationToken.None;

        await using (var db = ReaderFor(sql.ConnectionString, providerId))
        {
            // Stands in for a real patient. Invented, like everything else in this repo —
            // what matters is only that the roster does not name it.
            db.Patients.Add(Patient.Create(
                providerId, "Somebody", "Notonthislist",
                new DateOnly(2023, 3, 3), new DateOnly(2026, 8, 25)));

            await db.SaveChangesAsync(ct);
        }

        await using var seeder = new DemoSeeder(
            OptionsFor(sql.ConnectionString), providerId, displayName);

        var refusal = await seeder.RefusalReasonAsync(ct);

        Assert.NotNull(refusal);

        // Counts, never names: this message reaches a terminal and a scrollback buffer.
        Assert.Contains("1 patient", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Notonthislist", refusal, StringComparison.Ordinal);

        // And nothing was written. A refusal that has already inserted half a roster is not
        // a refusal.
        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        Assert.Equal(1, await reader.Patients.CountAsync(ct));
    }

    /// <summary>
    /// Whose caseload the demo belongs to is a question, not a default — the same rule the
    /// public consultation form runs (D078). Ambiguity is refused rather than resolved by
    /// picking the lowest id.
    ///
    /// Runs against a database of its own, because the shared one accumulates a provider
    /// per test class and "how many active providers are there" is the whole assertion.
    ///
    /// Control: the `1 =>` arm of the switch in DemoSeeder.ResolveSoleActiveProviderAsync,
    /// replaced by falling through to the ambiguous arm. Deleted → red on the sole-provider
    /// assertion, "Assert.Null() Failure: Value is not null / Expected: null / Actual: \"This
    /// database has 1 active providers, so whose cas\"···".
    /// </summary>
    [Fact]
    public async Task An_ambiguous_provider_is_refused_and_a_sole_one_is_resolved()
    {
        var connectionString = await sql.CreateIsolatedDatabaseAsync("demoseedproviders");
        var options = OptionsFor(connectionString);
        var ct = CancellationToken.None;

        var empty = await DemoSeeder.ResolveSoleActiveProviderAsync(options, ct);
        Assert.NotNull(empty.Refusal);
        Assert.Null(empty.ProviderId);

        var (soleId, _) = await SeedProviderAsync(connectionString, "Michelle Demo");

        var sole = await DemoSeeder.ResolveSoleActiveProviderAsync(options, ct);
        Assert.Null(sole.Refusal);
        Assert.Equal(soleId, sole.ProviderId);

        await SeedProviderAsync(connectionString, "Second Clinician");

        var ambiguous = await DemoSeeder.ResolveSoleActiveProviderAsync(options, ct);
        Assert.NotNull(ambiguous.Refusal);
        Assert.Null(ambiguous.ProviderId);
    }

    // ------------------------------------------------------- what the screens need

    /// <summary>
    /// The immutability story is the one thing in this product that cannot be demonstrated
    /// by a screenshot of a form. It needs a visit whose record was corrected: v1 signed and
    /// retained, v2 signed and current, and the correction visible between them.
    ///
    /// VerifyIntegrity on v1 is the assertion that matters. It recomputes the SHA-256 over
    /// v1's four sections and compares it with the hash stored at signature — so it goes red
    /// if the amendment altered so much as a character of the version it superseded.
    ///
    /// Control: the `SeedAmendment` call in DemoSeeder.SeedNotesAsync. Deleted → red on the
    /// Amended lookup, "Assert.Single() Failure: The collection did not contain any matching
    /// items".
    /// </summary>
    [Fact]
    public async Task The_note_states_the_screens_render_are_present_and_the_superseded_version_is_intact()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var notes = await reader.ClinicalNotes.ToListAsync(CancellationToken.None);

        Assert.Contains(notes, n => n.Status == NoteStatus.Draft);
        Assert.Contains(notes, n => n.Status == NoteStatus.Signed && n.SupersedesNoteId is null);

        var v1 = Assert.Single(notes, n => n.Status == NoteStatus.Amended);
        var v2 = Assert.Single(notes, n => n.SupersedesNoteId == v1.Id);

        Assert.False(v1.IsCurrent);
        Assert.Equal(1, v1.VersionNumber);

        Assert.True(v2.IsCurrent);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal(NoteStatus.Signed, v2.Status);
        Assert.False(string.IsNullOrWhiteSpace(v2.AmendmentReason));

        // The correction actually corrected something, so the version history is worth
        // opening.
        Assert.NotEqual(v1.Objective, v2.Objective);

        // Both rows still hash to their own content: v1 was superseded, not edited.
        Assert.True(v1.VerifyIntegrity(), "the superseded version's content was altered");
        Assert.True(v2.VerifyIntegrity(), "the amendment's content does not match its hash");
    }

    /// <summary>
    /// The AAC-only fields are unmounted and refused rather than hidden and dropped (D062),
    /// and both the aggregate and CK_Goals_AacFieldsOnlyOnAacGoals enforce it. So a demo
    /// with no AAC goal cannot show that half of the goals UI at all, and one with a
    /// modality on a non-AAC goal would not have inserted.
    ///
    /// Control: the `g.AacModality` argument in the Goal.Create call in
    /// DemoSeeder.SeedGoalsAsync, replaced with null. Deleted → red on the modality
    /// assertion, with this test's own message: "no AAC goal carries a modality".
    /// </summary>
    [Fact]
    public async Task The_aac_goal_carries_aac_fields_and_no_other_goal_does()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var goals = await reader.Goals.ToListAsync(CancellationToken.None);

        var aac = goals.Where(g => g.Domain == GoalDomain.Aac).ToList();
        Assert.NotEmpty(aac);

        Assert.True(
            aac.Any(g => g.AacModality is not null && !string.IsNullOrWhiteSpace(g.AacDeviceNotes)),
            "no AAC goal carries a modality");

        Assert.All(
            goals.Where(g => g.Domain != GoalDomain.Aac),
            g =>
            {
                Assert.Null(g.AacModality);
                Assert.Null(g.AacDeviceNotes);
            });

        // Statuses the goal list has to be able to render. Closed goals are closed, not
        // deleted (D063), so a demo with only Active goals hides half the screen.
        Assert.Contains(goals, g => g.Status == GoalStatus.Active);
        Assert.Contains(goals, g => g.Status == GoalStatus.Met);
        Assert.Contains(goals, g => g.Status == GoalStatus.Discontinued);
        Assert.Contains(goals, g => g.Status == GoalStatus.OnHold);
    }

    /// <summary>
    /// The day view is the screen the demo opens on, and half of what it shows is the
    /// documentation gate: which visits offer a note and which refuse, in the clinician's
    /// own words. Every refusal Appointment.DocumentationBlockedReason can produce needs a
    /// visit behind it, or the gate is invisible.
    ///
    /// Enumerated over the reasons rather than asserted as "at least one is blocked", which
    /// would stay green with any two of the three cases missing.
    ///
    /// Control: `appointment.Cancel(spec.CancellationReason)` in DemoSeeder.SeedVisitsAsync.
    /// Deleted → red on the cancelled reason, "Assert.Contains() Failure: Filter not
    /// matched in collection".
    /// </summary>
    [Fact]
    public async Task Todays_calendar_gives_the_documentation_gate_every_refusal_and_one_pass()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var today = PracticeTime.LocalDateOf(Now);

        var visits = (await reader.Appointments.ToListAsync(CancellationToken.None))
            .Where(a => PracticeTime.LocalDateOf(a.StartUtc) == today)
            .ToList();

        Assert.True(visits.Count >= 5, $"only {visits.Count} visits on the demo day");

        var reasons = visits
            .Select(v => v.DocumentationBlockedReason(Now))
            .ToList();

        Assert.Contains(reasons, r => r is not null && r.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r is not null && r.Contains("no-show", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r is not null && r.Contains("not started yet", StringComparison.Ordinal));

        // And at least one visit the gate lets through, or there is nothing to document.
        Assert.Contains(reasons, r => r is null);

        // Mileage is what the day's footer totals, and it only lands on a completed visit.
        Assert.True(
            visits.Any(v => v.Mileage is > 0m),
            "no completed visit carries mileage, so the day view's total reads zero");
    }

    /// <summary>
    /// The distinction D073 exists for: the adult Michelle rings is not always the adult
    /// entitled to the record. A fixture where primary contact and legal authority always
    /// agree demonstrates nothing, because every screen looks correct whichever field it
    /// reads.
    ///
    /// Also covers the state docs/DATA_MODEL.md calls real rather than erroneous — a patient
    /// with guardians and no authorised one, which is what `recordsReleaseState` renders.
    ///
    /// Control: `HasLegalAuthority: false` in DemoRoster — BOTH occurrences, flipped to
    /// true. Deleted → red on the first assertion, "Assert.Contains() Failure: Filter not
    /// matched in collection".
    ///
    /// Both, because flipping only the stepmother leaves the grandmother satisfying the same
    /// assertion and the test stays green — the "second control covering for the first"
    /// shape D096 catalogued four times. The two rows are the same control, so the deletion
    /// is of the class. The grandmother's row is additionally the ONLY source of the third
    /// assertion (a patient with guardians and no authorised one), which is why it is
    /// asserted separately rather than folded into the first.
    /// </summary>
    [Fact]
    public async Task A_primary_contact_without_legal_authority_is_in_the_fixture()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var ct = CancellationToken.None;

        var patients = await reader.Patients.Include(p => p.Guardians).ToListAsync(ct);
        var guardians = patients.SelectMany(p => p.Guardians).ToList();

        Assert.Contains(guardians, g => g.IsPrimaryContact && !g.HasLegalAuthority);
        Assert.Contains(guardians, g => !g.IsPrimaryContact && g.HasLegalAuthority);

        // A family whose custody paperwork has not arrived: guardians on file, none of them
        // able to receive the record.
        Assert.Contains(
            patients,
            p => p.Guardians.Count > 0 && p.Guardians.All(g => !g.HasLegalAuthority));

        // At most one primary contact per patient — the invariant Patient.AddGuardian holds
        // and a seeder writing rows directly would have broken.
        Assert.All(patients, p => Assert.True(p.Guardians.Count(g => g.IsPrimaryContact) <= 1));
    }

    /// <summary>
    /// The inbox filters on four statuses, so all four need a row or the filter chips lead
    /// to empty lists.
    ///
    /// Control: `request.Decline()` in DemoSeeder.SeedEnquiriesAsync. Deleted → red,
    /// "Assert.Contains() Failure: Filter not matched in collection" on the Declined arm.
    /// </summary>
    [Fact]
    public async Task An_enquiry_exists_in_each_status_the_inbox_filters_on()
    {
        var (providerId, displayName) = await SeedProviderAsync(sql.ConnectionString);
        await SeedAsync(providerId, displayName);

        await using var reader = ReaderFor(sql.ConnectionString, providerId);
        var ct = CancellationToken.None;

        var enquiries = await reader.ConsultationRequests.ToListAsync(ct);

        Assert.Contains(enquiries, e => e.Status == ConsultationStatus.New);
        Assert.Contains(enquiries, e => e.Status == ConsultationStatus.Contacted);
        Assert.Contains(enquiries, e => e.Status == ConsultationStatus.Converted);
        Assert.Contains(enquiries, e => e.Status == ConsultationStatus.Declined);

        // Converted says WHICH patient, and that patient is one the seed created — so
        // "this enquiry became a patient" leads somewhere in the demo.
        var converted = enquiries.Single(e => e.Status == ConsultationStatus.Converted);
        Assert.NotNull(converted.ConvertedPatientId);
        Assert.True(
            await reader.Patients.AnyAsync(p => p.Id == converted.ConvertedPatientId, ct),
            "the converted enquiry points at a patient that is not on the caseload");
    }

    // ------------------------------------------------------------------- helpers

    private sealed record Watermark(
        long Patients, long Guardians, long Addresses, long Goals,
        long Visits, long Notes, long Enquiries);

    private sealed record Totals(
        int Patients, int Guardians, int Addresses, int Goals,
        int Visits, int Notes, int Enquiries);

    private static async Task<Totals> CountEverythingAsync(PracticeDbContext db)
    {
        var ct = CancellationToken.None;

        return new Totals(
            await db.Patients.CountAsync(ct),
            await db.Guardians.CountAsync(ct),
            await db.PatientAddresses.CountAsync(ct),
            await db.Goals.CountAsync(ct),
            await db.Appointments.CountAsync(ct),
            await db.ClinicalNotes.CountAsync(ct),
            await db.ConsultationRequests.CountAsync(ct));
    }

    // MaxAsync throws on an empty sequence; the shared database may or may not already hold
    // rows for a given table depending on which classes ran first.
    private static async Task<long> MaxIdAsync(IQueryable<long> ids, CancellationToken ct) =>
        await ids.OrderByDescending(id => id).FirstOrDefaultAsync(ct);

    private static async Task AssertAllCarryProviderAsync(
        string table, IQueryable<long> providerIds, long expected, CancellationToken ct)
    {
        var wrong = await providerIds.Where(id => id != expected).ToListAsync(ct);

        Assert.True(
            wrong.Count == 0,
            $"{table} rows carrying the wrong provider: {wrong.Count}");

        var total = await providerIds.CountAsync(ct);
        Assert.True(total > 0, $"{table} seeded nothing, so this proves nothing about it");
    }
}
