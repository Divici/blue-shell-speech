using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Billing;
using Practice.Domain.Patients;
using Practice.Domain.Providers;
using Practice.Domain.Resources;
using Practice.Domain.Scheduling;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The two tables the scope ledger ships EMPTY — Encounters and ResourceDocuments — against
/// real SQL Server.
///
/// There is no endpoint behind either of them yet, which changes what a test here can be.
/// Every other integration class in this project drives the API; these drive the DbContext
/// and raw SQL directly, because the controls being asserted are the two that exist before
/// any handler does: the global tenancy query filter, and the constraints in the migration.
///
/// **No test here reaches a row through an already-filtered parent.** That is the D066 F4
/// defect, found three more times since (D073, twice), and it is worth restating because
/// the shape is seductive: reading an encounter through its patient proves the *Patient*
/// filter works and says nothing about the Encounter one. Every foreign row below is
/// planted by raw INSERT onto a patient, visit or encounter the caller genuinely OWNS, so
/// the filter under test is the only thing standing in the way.
///
/// SYNTHETIC DATA ONLY. Every name, code, charge and handout below is invented.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class BillingAndResourceSchemaTests(SqlServerFixture sql)
{
    /// <summary>
    /// A context scoped to one provider, with no web host in the way.
    ///
    /// FixedProviderContext is what arms the tenancy filter — the same interface the
    /// request-scoped implementation satisfies, so the filter under test is production's,
    /// not a stand-in. Null means "no authenticated caller", which the filter must read as
    /// MATCHING NOTHING (D051).
    /// </summary>
    private PracticeDbContext Db(long? providerId) =>
        new(
            new DbContextOptionsBuilder<PracticeDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options,
            new FixedProviderContext(providerId));

    private async Task<long> SeedProviderAsync(string name)
    {
        await using var db = Db(null);

        // No Identity user: IdentityUserId is a link, not a foreign key across the
        // boundary (see Provider), and nothing here authenticates.
        var provider = Provider.Create(
            $"{name}-{Guid.NewGuid():N}", name, "M.S., CCC-SLP", "SLP-1", "MD");

        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return provider.Id;
    }

    /// <summary>A patient and a visit belonging to the given provider.</summary>
    private async Task<(long PatientId, long AppointmentId)> SeedCaseloadAsync(long providerId)
    {
        await using var db = Db(providerId);

        var patient = Patient.Create(
            providerId, "Maya", "Reyes", new DateOnly(2024, 2, 24), new DateOnly(2026, 8, 25));
        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        var visit = Appointment.Schedule(
            providerId, patient.Id, AppointmentType.Therapy,
            new DateTime(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc), 45);
        db.Appointments.Add(visit);
        await db.SaveChangesAsync();

        return (patient.Id, visit.Id);
    }

    /// <summary>
    /// Plants an encounter OWNED BY <paramref name="ownerProviderId"/> on someone else's
    /// patient and visit, by raw INSERT.
    ///
    /// The application cannot produce this row — Encounter.Record stamps one provider onto
    /// all of it — and that is exactly why it has to be written directly. Without it, every
    /// foreign encounter is reachable only through a foreign patient, which the Patient
    /// filter has already removed, and the Encounter filter could be deleted with nothing
    /// going red.
    /// </summary>
    private async Task<Guid> PlantEncounterAsync(
        long ownerProviderId,
        long patientId,
        long appointmentId,
        string cptCode = "92507",
        short units = 1,
        decimal chargeAmount = 150m,
        decimal amountPaid = 0m,
        DateTime? paidAtUtc = null)
    {
        await using var db = Db(null);
        var publicId = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.Encounters
                (PublicId, ProviderId, PatientId, AppointmentId, RenderingProviderId,
                 ClinicalNoteId, ServiceDate, CptCode, Modifiers, PlaceOfService, Units,
                 ChargeAmount, AmountPaid, PaymentStatus, PaymentMethod, PaidAtUtc,
                 SuperbillGeneratedAtUtc, CreatedAtUtc)
            VALUES
                ({publicId}, {ownerProviderId}, {patientId}, {appointmentId},
                 {ownerProviderId}, NULL, '2026-08-25', {cptCode}, 'GN', 12, {units},
                 {chargeAmount}, {amountPaid}, 1, NULL, {paidAtUtc}, NULL, SYSUTCDATETIME())
            """);

        return publicId;
    }

    /// <summary>Same technique, same reason, for the diagnosis filter.</summary>
    private async Task PlantDiagnosisAsync(
        long ownerProviderId, long encounterId, short sequence, string code)
    {
        await using var db = Db(null);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.EncounterDiagnoses
                (PublicId, ProviderId, EncounterId, Sequence, Code, CreatedAtUtc)
            VALUES
                ({Guid.NewGuid()}, {ownerProviderId}, {encounterId}, {sequence}, {code},
                 SYSUTCDATETIME())
            """);
    }

    private async Task PlantResourceAsync(
        long ownerProviderId,
        string slug,
        bool isPublished = false,
        DateTime? publishedAtUtc = null)
    {
        await using var db = Db(null);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.ResourceDocuments
                (PublicId, ProviderId, Title, Slug, Description, BlobUri, ContentType,
                 FileSizeBytes, RevisionNumber, ContentUpdatedAtUtc, IsPublished,
                 PublishedAtUtc, WithdrawnAtUtc, SortOrder, CreatedAtUtc)
            VALUES
                ({Guid.NewGuid()}, {ownerProviderId}, N'Talking at mealtimes', {slug}, NULL,
                 N'https://storage.example.invalid/resources/mealtimes.pdf',
                 'application/pdf', 240000, 1, NULL, {isPublished}, {publishedAtUtc}, NULL,
                 0, SYSUTCDATETIME())
            """);
    }

    /// <summary>A slug no other test in the shared database can collide with.</summary>
    private static string UniqueSlug() => $"handout-{Guid.NewGuid():N}";

    // ------------------------------------------------------------------- tenancy

    /// <summary>
    /// The Encounter filter, tested where it is the ONLY control in the way: a visit the
    /// caller genuinely owns, carrying a charge owned by somebody else.
    ///
    /// It matters beyond tidiness. A stranger's line on a patient's billing history is a
    /// CPT code, a diagnosis and an amount belonging to a different child, on the page
    /// somebody would print a superbill from.
    ///
    /// Control: the Encounter global query filter in PracticeDbContext.
    /// Deleted → red, "Assert.Single() Failure: The collection contained 2 items". The stranger's
    /// 92526 line appears on this practice's visit.
    /// </summary>
    [Fact]
    public async Task An_encounter_owned_by_another_provider_is_invisible_on_a_visit_the_caller_owns()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);

        await PlantEncounterAsync(michelle, patientId, appointmentId);
        await PlantEncounterAsync(stranger, patientId, appointmentId, cptCode: "92526");

        await using var db = Db(michelle);
        var encounters = await db.Encounters
            .AsNoTracking()
            .Where(e => e.AppointmentId == appointmentId)
            .ToListAsync();

        var only = Assert.Single(encounters);
        Assert.Equal("92507", only.CptCode);
    }

    /// <summary>
    /// The EncounterDiagnosis filter, on an encounter the caller owns.
    ///
    /// This is the child-row case D066 F4 and D073 both found open: a diagnosis is only
    /// ever reachable through an encounter, so the Encounter filter covers for this one on
    /// every row the application can produce, and it could be deleted outright without a
    /// single test noticing. The planted row hangs off MICHELLE's own encounter, which is
    /// the only shape where this filter is the sole thing in the way.
    ///
    /// Control: the EncounterDiagnosis global query filter in PracticeDbContext.
    /// Deleted → red, "Assert.Single() Failure: The collection contained 2 items".
    /// </summary>
    [Fact]
    public async Task A_diagnosis_owned_by_another_provider_is_invisible_on_an_encounter_the_caller_owns()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);
        var encounterPublicId = await PlantEncounterAsync(michelle, patientId, appointmentId);

        long encounterId;
        await using (var raw = Db(null))
        {
            encounterId = await raw.Encounters.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.PublicId == encounterPublicId).Select(e => e.Id).SingleAsync();
        }

        await PlantDiagnosisAsync(michelle, encounterId, 1, "F80.2");
        await PlantDiagnosisAsync(stranger, encounterId, 2, "R62.50");

        await using var db = Db(michelle);
        var encounter = await db.Encounters
            .AsNoTracking()
            .Include(e => e.Diagnoses)
            .SingleAsync(e => e.Id == encounterId);

        var only = Assert.Single(encounter.Diagnoses);
        Assert.Equal("F80.2", only.Code);
    }

    /// <summary>
    /// Handouts are public content, and the WRITE side is still tenant-scoped.
    ///
    /// docs/DATA_MODEL.md previously said this table needed no filter because the rows are
    /// not PHI. That is true of the published rows and wrong about the table: the library
    /// is edited through a session, and an unfiltered one shows a second clinician's
    /// unpublished drafts to the first. The public read path is a deliberate,
    /// greppable IgnoreQueryFilters() when it is built — see PracticeDbContext.
    ///
    /// Control: the ResourceDocument global query filter in PracticeDbContext.
    /// Deleted → red, "Assert.Single() Failure: The collection contained 2 items".
    /// </summary>
    [Fact]
    public async Task A_handout_owned_by_another_provider_is_invisible_in_the_library()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        var mine = UniqueSlug();

        await PlantResourceAsync(michelle, mine);
        await PlantResourceAsync(stranger, UniqueSlug());

        await using var db = Db(michelle);
        var documents = await db.ResourceDocuments.AsNoTracking().ToListAsync();

        var only = Assert.Single(documents);
        Assert.Equal(mine, only.Slug);
    }

    /// <summary>
    /// A null provider matches NOTHING, on the one table whose rows are meant to be public.
    ///
    /// This is the direction D051 says a filter must fail in, and it is the direction a
    /// naive implementation gets backwards — most tempting here, where "these are public
    /// handouts" is an argument for showing them to an unauthenticated reader. The public
    /// page opts out explicitly instead; it does not get served by a filter that quietly
    /// gives up.
    ///
    /// Control: the ResourceDocument query filter's `providerContext.ProviderId != null`
    /// clause in PracticeDbContext. Inverted to `|| providerContext.ProviderId == null`,
    /// which is the shape a "but these are public handouts" argument produces → red,
    /// "Assert.Empty() Failure: Collection was not empty".
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_context_sees_no_handouts_at_all()
    {
        var michelle = await SeedProviderAsync("Michelle");
        await PlantResourceAsync(michelle, UniqueSlug(), isPublished: true,
            publishedAtUtc: new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));

        await using var db = Db(null);
        var documents = await db.ResourceDocuments.AsNoTracking().ToListAsync();

        Assert.Empty(documents);
    }

    // --------------------------------------------------------------- constraints

    /// <summary>
    /// Two handouts cannot claim the same public URL, and the DATABASE is what says so.
    ///
    /// The index is unique across the whole table rather than per provider, because
    /// /resources/{slug} has no tenant segment in it. The two rows below belong to
    /// DIFFERENT providers, which is the case a per-provider index would have allowed and
    /// which would have left the public route resolving by insertion order.
    ///
    /// Control: UX_ResourceDocuments_OneDocumentPerPublicUrl — the `unique: true` argument
    /// in the AddEncountersAndResourceDocuments MIGRATION, not in
    /// ResourceDocumentConfiguration. The test database is built by running the migrations,
    /// so deleting .IsUnique() from the configuration changes the model and leaves the
    /// index on the table — and the test green.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task Two_handouts_cannot_claim_the_same_public_url()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        var slug = UniqueSlug();

        await PlantResourceAsync(michelle, slug);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantResourceAsync(stranger, slug));

        Assert.Contains("UX_ResourceDocuments_OneDocumentPerPublicUrl", ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A published handout carries the date it went up, or it does not go in the table.
    ///
    /// The aggregate sets the pair together. This is the belt: the row a bad migration, a
    /// bulk load, or a script run at 11pm would leave behind, on the one table an anonymous
    /// reader is served from.
    ///
    /// Control: CK_ResourceDocuments_PublishedRowsCarryADate — its CheckConstraint line in
    /// the AddEncountersAndResourceDocuments migration.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task A_published_handout_with_no_publication_date_is_refused_by_the_database()
    {
        var michelle = await SeedProviderAsync("Michelle");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantResourceAsync(michelle, UniqueSlug(), isPublished: true));

        Assert.Contains("CK_ResourceDocuments_PublishedRowsCarryADate", ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Money that arrived has a date. A row with an amount and no date cannot be reconciled
    /// against anything, and it is the row a half-written import leaves.
    ///
    /// Control: CK_Encounters_PaymentsCarryADate — its CheckConstraint line in the
    /// AddEncountersAndResourceDocuments migration.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task A_payment_with_no_date_is_refused_by_the_database()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantEncounterAsync(
                michelle, patientId, appointmentId, amountPaid: 50m, paidAtUtc: null));

        Assert.Contains("CK_Encounters_PaymentsCarryADate", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A billable line bills something. Zero units on a superbill is a charge with no
    /// service behind it.
    ///
    /// Control: CK_Encounters_UnitsAreBillable — its CheckConstraint line in the
    /// AddEncountersAndResourceDocuments migration.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task A_line_billing_no_units_is_refused_by_the_database()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantEncounterAsync(michelle, patientId, appointmentId, units: 0));

        Assert.Contains("CK_Encounters_UnitsAreBillable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same diagnosis twice on one line, at DIFFERENT positions.
    ///
    /// The position and the code have a unique index each, and either would refuse a
    /// duplicate planted at the same sequence — which is the two-clauses-covering-for-each-
    /// other shape D077 found on the note DELETE trigger. Sequence 2 is what isolates this
    /// one: only the code index can refuse it.
    ///
    /// Control: UX_EncounterDiagnoses_OneRowPerCode — the `unique: true` argument on that
    /// index in the AddEncountersAndResourceDocuments migration.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task The_same_diagnosis_cannot_be_recorded_twice_on_one_line()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);
        var encounterId = await PlantedEncounterIdAsync(michelle, patientId, appointmentId);

        await PlantDiagnosisAsync(michelle, encounterId, 1, "F80.2");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantDiagnosisAsync(michelle, encounterId, 2, "F80.2"));

        Assert.Contains("UX_EncounterDiagnoses_OneRowPerCode", ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two codes claiming to be the primary diagnosis, at the same position.
    ///
    /// The mirror of the test above, and the reason there are two: a superbill prints the
    /// diagnoses in order, and "which of these is the primary" is not a tie a later reader
    /// can break. A DIFFERENT code at the same sequence is refused by the position index
    /// alone.
    ///
    /// Control: UX_EncounterDiagnoses_OnePerPosition — the `unique: true` argument on that
    /// index in the AddEncountersAndResourceDocuments migration.
    /// Deleted → red, "Assert.ThrowsAny() Failure: No exception was thrown / Expected:
    /// typeof(System.Exception)".
    /// </summary>
    [Fact]
    public async Task Two_diagnoses_cannot_share_a_position_on_one_line()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);
        var encounterId = await PlantedEncounterIdAsync(michelle, patientId, appointmentId);

        await PlantDiagnosisAsync(michelle, encounterId, 1, "F80.2");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => PlantDiagnosisAsync(michelle, encounterId, 1, "R62.50"));

        Assert.Contains("UX_EncounterDiagnoses_OnePerPosition", ex.Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- mapping

    /// <summary>
    /// The migration runs and the aggregate round-trips through it — a date of service that
    /// stays a calendar date, a place of service stored as the CMS number, and diagnoses
    /// that come back in the order they were recorded.
    ///
    /// This test names NO control. It is the one that fails if any of the three new tables
    /// is shaped wrong, and there is no single clause whose deletion is its subject; saying
    /// so is honest where inventing a Control: line would not be (D070).
    /// </summary>
    [Fact]
    public async Task An_encounter_and_its_diagnoses_round_trip_through_the_database()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var (patientId, appointmentId) = await SeedCaseloadAsync(michelle);

        // 01:30 UTC is the evening of the previous day in Maryland — the ordinary case for
        // an in-home practice, and the one a UTC date would bill on the wrong day.
        var eveningVisitUtc = new DateTime(2026, 3, 10, 1, 30, 0, DateTimeKind.Utc);

        Guid publicId;
        await using (var write = Db(michelle))
        {
            var encounter = Encounter.Record(
                michelle, patientId, appointmentId, michelle, eveningVisitUtc,
                "92507", PlaceOfService.Home, 1, 150m, "GN");

            encounter.AddDiagnosis("F80.2");
            encounter.AddDiagnosis("F80.1");

            write.Encounters.Add(encounter);
            await write.SaveChangesAsync();
            publicId = encounter.PublicId;
        }

        await using var read = Db(michelle);
        var stored = await read.Encounters
            .AsNoTracking()
            .Include(e => e.Diagnoses)
            .SingleAsync(e => e.PublicId == publicId);

        Assert.Equal(new DateOnly(2026, 3, 9), stored.ServiceDate);
        Assert.Equal(PlaceOfService.Home, stored.PlaceOfService);
        Assert.Equal(PaymentStatus.Unpaid, stored.PaymentStatus);
        Assert.Null(stored.PaidAtUtc);

        Assert.Collection(
            stored.Diagnoses.OrderBy(d => d.Sequence),
            first => Assert.Equal("F80.2", first.Code),
            second => Assert.Equal("F80.1", second.Code));
    }

    /// <summary>
    /// A handout round-trips, and its publication history survives a withdrawal.
    ///
    /// Names no control, for the same reason as the encounter round trip above.
    /// </summary>
    [Fact]
    public async Task A_handout_round_trips_and_keeps_the_date_it_first_went_up()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var published = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        var slug = UniqueSlug();

        Guid publicId;
        await using (var write = Db(michelle))
        {
            var document = ResourceDocument.Draft(
                michelle, "Talking at mealtimes", slug, "Ten minutes a day.",
                "https://storage.example.invalid/resources/mealtimes.pdf",
                "application/pdf", 240_000);

            document.Publish(published);
            document.Withdraw(published.AddDays(30));

            write.ResourceDocuments.Add(document);
            await write.SaveChangesAsync();
            publicId = document.PublicId;
        }

        await using var read = Db(michelle);
        var stored = await read.ResourceDocuments
            .AsNoTracking()
            .SingleAsync(r => r.PublicId == publicId);

        Assert.False(stored.IsPublished);
        Assert.Equal(published, stored.PublishedAtUtc);
        Assert.Equal(published.AddDays(30), stored.WithdrawnAtUtc);
        Assert.Equal(1, stored.RevisionNumber);
    }

    private async Task<long> PlantedEncounterIdAsync(
        long providerId, long patientId, long appointmentId)
    {
        var publicId = await PlantEncounterAsync(providerId, patientId, appointmentId);

        await using var db = Db(null);
        return await db.Encounters.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.PublicId == publicId).Select(e => e.Id).SingleAsync();
    }
}
