using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.Consultations;
using Practice.Application.Consultations;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Consultations;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The public intake endpoint, against real SQL Server.
///
/// THIS IS THE ONLY UNAUTHENTICATED WRITE IN THE SYSTEM. Every other endpoint is reached
/// through a session that has already been established; this one is reached by a stranger
/// with a browser. So the questions here are different from the rest of the suite: not
/// "can Michelle see somebody else's record" but "can an anonymous caller choose a tenant,
/// store an address, get an over-long value into a column, or make the practice send an
/// email describing a child".
///
/// ITS OWN DATABASE, and why. The endpoint resolves the SOLE ACTIVE PROVIDER and refuses
/// when the answer is ambiguous, so its behaviour is a function of the Providers table —
/// which, in the shared collection database, holds whatever every earlier test class
/// seeded. See SqlServerFixture.CreateIsolatedDatabaseAsync.
///
/// SYNTHETIC DATA ONLY. Names, emails and descriptions are invented; the telephone numbers
/// are in the 555-01xx range reserved for fiction.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class ConsultationIntakeTests(SqlServerFixture sql) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private RecordingNotifier _notifications = new();

    /// <summary>
    /// One database for the class, emptied before each test.
    ///
    /// xUnit builds a fresh instance per test, so this runs every time. The tables are
    /// cleared rather than the database recreated because migrating a new one costs
    /// seconds per test and proves nothing extra — what these tests need is a Providers
    /// table they control and an intake table they can count, not a virgin schema.
    ///
    /// AuditEvents is emptied here as well. The application principal has no DELETE on it
    /// in production (docs/SECURITY.md) and this connection is `sa`; that grant is
    /// asserted elsewhere, and counting arrivals requires starting from none.
    /// </summary>
    public async Task InitializeAsync()
    {
        _connectionString = await sql.CreateIsolatedDatabaseAsync("BlueShellIntake");

        await using var db = DbFor(null);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ConsultationRequests; DELETE FROM AuditEvents;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------ fixtures

    /*
     * DISTINCTIVE MARKERS, not plausible-looking values.
     *
     * Several assertions below are "this string appears NOWHERE in X". A child called
     * "Emma" would give a false pass the moment "emma" occurs inside a GUID or a column
     * name, so the fixtures use tokens nothing else in the system produces.
     */
    private const string ParentMarker = "Zephyrine Quillbrook";
    private const string ChildMarker = "Vexlimund";
    private const string ConcernMarker =
        "Qwintaxel: about ten single words, no combinations, and real frustration at bedtime.";

    /// <summary>A well-formed SHA-256 digest — the shape `hashClientId` in the BFF produces.</summary>
    private const string SourceHash =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static SubmitConsultationRequest NewSubmission(
        string? phone = "410-555-0142",
        string preferredContactMethod = "Email",
        string? sourceIpHash = SourceHash,
        string? concerns = null,
        short childAgeMonths = 30) =>
        new(
            ParentMarker,
            "zephyrine@example.com",
            phone,
            ChildMarker,
            childAgeMonths,
            concerns ?? ConcernMarker,
            preferredContactMethod,
            sourceIpHash);

    /// <summary>
    /// Captures what the API asks to be sent, so the notification can be inspected.
    ///
    /// It can only record a Guid, because that is the only thing IConsultationNotifier is
    /// able to hand it — which is the point of that interface and is asserted directly in
    /// ConsultationNotifierTests.
    /// </summary>
    private sealed class RecordingNotifier : IConsultationNotifier
    {
        public List<Guid> Notified { get; } = [];

        public Task NotifyAsync(Guid consultationRequestPublicId)
        {
            Notified.Add(consultationRequestPublicId);
            return Task.CompletedTask;
        }
    }

    private sealed class UnreachableMailbox : IConsultationNotifier
    {
        public Task NotifyAsync(Guid consultationRequestPublicId) =>
            throw new InvalidOperationException("The mail service is unavailable.");
    }

    private PracticeApiFactory Api(
        IConsultationNotifier? notifier = null,
        Action<IServiceCollection>? extra = null)
    {
        _notifications = notifier as RecordingNotifier ?? new RecordingNotifier();
        var chosen = notifier ?? _notifications;

        return new PracticeApiFactory(_connectionString, services =>
        {
            services.AddScoped(_ => chosen);
            extra?.Invoke(services);
        });
    }

    /// <summary>
    /// Leaves the Providers table holding exactly <paramref name="count"/> ACTIVE rows.
    ///
    /// Existing providers are deactivated rather than deleted: Provider.Deactivate is what
    /// the application itself does, a clinician row is never removed (docs/DATA_MODEL.md),
    /// and a delete would fall foul of every foreign key pointing at it.
    /// </summary>
    private async Task<IReadOnlyList<Provider>> SeedActiveProvidersAsync(int count)
    {
        await using var db = DbFor(null);

        foreach (var existing in await db.Providers.Where(p => p.IsActive).ToListAsync())
        {
            existing.Deactivate();
        }

        var seeded = new List<Provider>();
        for (var i = 0; i < count; i++)
        {
            var provider = Provider.Create(
                $"user-{Guid.NewGuid():N}", $"Clinician {i}", "M.S., CCC-SLP", "SLP-1", "MD");
            db.Providers.Add(provider);
            seeded.Add(provider);
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    /// <summary>An inactive provider, to stand in for a clinician who has stopped practising.</summary>
    private async Task<Provider> SeedInactiveProviderAsync()
    {
        await using var db = DbFor(null);

        var provider = Provider.Create(
            $"user-{Guid.NewGuid():N}", "Retired", "M.S., CCC-SLP", "SLP-2", "MD");
        provider.Deactivate();

        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    /// <summary>
    /// A context scoped to one provider, for reading rows back THROUGH the query filter.
    ///
    /// A null provider matches nothing, which is what lets the tenancy test below tell
    /// "filtered out" from "not there".
    /// </summary>
    private PracticeDbContext DbFor(long? providerId) =>
        new(
            new DbContextOptionsBuilder<PracticeDbContext>()
                .UseSqlServer(_connectionString)
                .Options,
            new FixedProviderContext(providerId));

    private async Task<List<ConsultationRequest>> RequestsForAsync(long providerId)
    {
        await using var db = DbFor(providerId);
        return await db.ConsultationRequests.AsNoTracking().ToListAsync();
    }

    /// <summary>
    /// Every row in the table, filter or no filter.
    ///
    /// IgnoreQueryFilters here on purpose: "nothing was written" has to mean nothing
    /// anywhere, not "nothing this reader can see". A count through the filter would
    /// return 0 for a row that was written under a different provider, which is the exact
    /// mistake a refusal test must not make.
    /// </summary>
    private async Task<int> RequestCountAsync()
    {
        await using var db = DbFor(null);
        return await db.ConsultationRequests.IgnoreQueryFilters().CountAsync();
    }

    private async Task<List<AuditEvent>> AuditEventsAsync(AuditEventType type)
    {
        await using var db = DbFor(null);

        // AuditEvents carries no query filter — it is the record OF tenancy, not a tenant
        // table — so a null provider context reads all of it.
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.EventType == type)
            .ToListAsync();
    }

    // -------------------------------------------------------------- the happy path

    /// <summary>
    /// The enquiry lands, against the practice's one clinician, with the hash and not the
    /// address.
    ///
    /// Control: the <c>db.ConsultationRequests.Add(attempted)</c> inside the atomic write.
    /// Deleted → red, "Assert.Single() Failure: The collection was empty".
    ///
    /// The audit row's IpAddress being null is the other half of the SourceIpHash
    /// decision, and it has no deletable control because it is an ABSENCE: AuditEvent has
    /// an ipAddress parameter and other endpoints pass it. Falsified by adding
    /// <c>ipAddress: http.Connection.RemoteIpAddress?.ToString()</c> to the Record call →
    /// red, "Assert.Null() Failure: Value is not null, Actual: ::1".
    /// </summary>
    [Fact]
    public async Task A_submission_is_stored_against_the_sole_active_provider()
    {
        var michelle = (await SeedActiveProvidersAsync(1))[0];

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<SubmittedConsultationRequest>();
        Assert.NotEqual(Guid.Empty, created!.PublicId);

        var stored = Assert.Single(await RequestsForAsync(michelle.Id));

        Assert.Equal(created.PublicId, stored.PublicId);
        Assert.Equal(michelle.Id, stored.ProviderId);
        Assert.Equal(ParentMarker, stored.ParentName);
        Assert.Equal(ChildMarker, stored.ChildFirstName);
        Assert.Equal(ConcernMarker, stored.Concerns);
        Assert.Equal((short)30, stored.ChildAgeMonths);
        Assert.Equal(PreferredContactMethod.Email, stored.PreferredContactMethod);
        Assert.Equal(ConsultationStatus.New, stored.Status);
        Assert.Null(stored.ConvertedPatientId);

        // The hash, byte for byte, and nothing that looks like an address.
        Assert.Equal(SourceHash, stored.SourceIpHash);

        Assert.Equal(DateTimeKind.Utc, stored.SubmittedAtUtc.Kind);

        var audited = Assert.Single(
            await AuditEventsAsync(AuditEventType.ConsultationRequestReceived));
        Assert.Equal(AuditOutcome.Success, audited.Outcome);
        Assert.Equal(created.PublicId, audited.EntityPublicId);
        Assert.Equal(michelle.Id, audited.ProviderId);

        /*
         * The raw address is not in the audit table either.
         *
         * Hashing it on the row and then writing it in full one table over would undo the
         * decision entirely — and AuditEvents is the table docs/SECURITY.md says is never
         * purged and most likely to be exported.
         */
        Assert.Null(audited.IpAddress);
        Assert.Contains(SourceHash, audited.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing the parent typed comes back out of the API, or reaches the notifier.
    ///
    /// Two channels, one rule. The response is the easy one to get wrong — echoing the
    /// created resource is the ordinary REST habit, and here it would put a child's name
    /// and a description of their difficulties back on the wire on the way to a page that
    /// displays none of it. The notification is the one that matters more, because email
    /// leaves this system entirely.
    ///
    /// Asserted on the RAW body rather than a deserialised record, so a field added to the
    /// response type later is caught rather than ignored.
    ///
    /// Control: the SubmittedConsultationRequest record — that it carries a Guid and
    /// nothing else. Given a <c>string ParentName</c> populated from the request → red on
    /// the first marker, "Assert.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task Nothing_the_parent_typed_leaves_the_api_in_its_response_or_its_notification()
    {
        var michelle = (await SeedActiveProvidersAsync(1))[0];

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        var body = await response.Content.ReadAsStringAsync();

        foreach (var marker in new[] { ParentMarker, ChildMarker, ConcernMarker })
        {
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        }

        /*
         * The notifier was handed an opaque id and could not have been handed anything
         * else — IConsultationNotifier has no parameter for it, which is asserted directly
         * in ConsultationNotifierTests. This checks the other half: that the id it got is
         * the stored row's, so the announcement is about something real.
         */
        var stored = Assert.Single(await RequestsForAsync(michelle.Id));
        Assert.Equal(stored.PublicId, Assert.Single(_notifications.Notified));
    }

    // ------------------------------------------------- whose enquiry is it

    /// <summary>
    /// With no clinician to receive it, the practice says so instead of accepting it.
    ///
    /// A 200 here would be the worst available outcome: a family told "thank you, we will
    /// be in touch" whose enquiry was never stored does not follow up, and nobody finds
    /// out. 503 is what the BFF turns into "we could not record that — please call".
    ///
    /// Control: the <c>providerId is null</c> branch in SubmitConsultationRequest.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: ServiceUnavailable,
    /// Actual: InternalServerError" — because providerId.Value then throws.
    /// </summary>
    [Fact]
    public async Task An_enquiry_with_no_provider_to_receive_it_is_refused_rather_than_dropped()
    {
        await SeedActiveProvidersAsync(0);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
        Assert.Empty(_notifications.Notified);
    }

    /// <summary>
    /// Two clinicians is an unanswered ROUTING question, not a coin toss.
    ///
    /// "Which of them gets a parent's enquiry" is a decision a human has to make — intake
    /// rota, specialty, coordinator — and picking the lowest id would answer it silently,
    /// land every family on whoever was seeded first, and be discovered months later. The
    /// refusal is loud on the day a second clinician is added, which is the day the
    /// decision is actually available to be made.
    ///
    /// Control: the <c>active.Count == 1</c> test in ResolveSoleProviderAsync.
    /// Relaxed to <c>active.Count >= 1</c> → red, "Assert.Equal() Failure: Values differ,
    /// Expected: ServiceUnavailable, Actual: Created".
    /// </summary>
    [Fact]
    public async Task An_enquiry_is_refused_when_two_clinicians_could_receive_it()
    {
        await SeedActiveProvidersAsync(2);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
    }

    /// <summary>
    /// The second clinician arriving mid-write is the same unanswered question, and gets
    /// the same answer.
    ///
    /// THE SIBLING OF THE DISCARD REGRESSION, found by walking the other call site of
    /// AtomicWrites rather than by anyone reporting it. WriteAtomicallyAsync clears the
    /// change tracker on every attempt and re-runs the body, so a conclusion drawn OUTSIDE
    /// the call is a statement about a database that may have moved on — and "there is
    /// exactly one clinician who could receive this" is a conclusion, not a value. Resolved
    /// once outside, the enquiry commits against whoever happened to be sole a moment
    /// earlier, which is precisely the silent answer D078 exists to refuse.
    ///
    /// The window is small and the outcome is not: an enquiry landing on a clinician
    /// nobody chose is discovered when a family says they never heard back.
    ///
    /// Control: the ResolveSoleProviderAsync call INSIDE the WriteAtomicallyAsync body in
    /// SubmitConsultationRequest.
    /// Deleted — the provider resolved only once, before the call — → red, "Assert.Equal()
    /// Failure: Values differ, Expected: ServiceUnavailable, Actual: Created": the
    /// interleaved activation is never seen, because with one resolve there is no second
    /// read for it to land in front of.
    /// </summary>
    [Fact]
    public async Task An_enquiry_is_refused_when_a_second_clinician_arrives_mid_write()
    {
        await SeedActiveProvidersAsync(1);

        async Task ASecondClinicianIsActivated()
        {
            await using var db = DbFor(null);

            db.Providers.Add(Provider.Create(
                $"user-{Guid.NewGuid():N}", "Clinician 2", "M.S., CCC-SLP", "SLP-3", "MD"));

            await db.SaveChangesAsync();
        }

        var interleave = new InterleavesOneWriteBeforeTheSecondRead(
            "Providers", ASecondClinicianIsActivated);

        using var api = Api(extra: FailureHarness.With(_connectionString, interleave));
        using var client = api.CreateClient();

        // After the host is up: the seeder asks whether a provider exists at startup.
        interleave.Arm();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());

        // And no parent is told to expect a call that nobody is going to make.
        Assert.Empty(_notifications.Notified);
    }

    /// <summary>
    /// An anonymous caller cannot choose a tenant.
    ///
    /// Every other route in this API takes the provider from X-Provider-Id, which the BFF
    /// sets from its own encrypted session cookie. This one has no session behind it, so
    /// honouring that header would let anyone on the internet nominate whose records they
    /// were writing into. The header is ignored, not merely unused: the handler does not
    /// take an IProviderContext at all.
    ///
    /// The retired clinician makes the claim testable — with him inactive the sole ACTIVE
    /// provider is unambiguous, so the only way his id can reach the row is through the
    /// header.
    ///
    /// Control: ResolveSoleProviderAsync — that the provider comes from the table rather
    /// than from the request. Substituted with the request's IProviderContext.ProviderId →
    /// red, "Assert.Equal() Failure: Values differ, Expected: Created, Actual:
    /// ServiceUnavailable", because the middleware refuses to resolve an inactive provider
    /// and the handler then has nobody to file the enquiry under.
    /// </summary>
    [Fact]
    public async Task The_forwarded_provider_header_cannot_steer_a_public_submission()
    {
        var retired = await SeedInactiveProviderAsync();
        var michelle = (await SeedActiveProvidersAsync(1))[0];

        using var api = Api();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, retired.PublicId.ToString());

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = Assert.Single(await RequestsForAsync(michelle.Id));
        Assert.Equal(michelle.Id, stored.ProviderId);
    }

    /// <summary>
    /// The tenancy filter, proved against a row the API cannot be made to produce.
    ///
    /// The endpoint files every enquiry under the sole active provider, so there is no
    /// request that puts one on a DIFFERENT provider — which means a test posting through
    /// the API and finding nothing would be finding nothing because nothing was written
    /// (D066 F3). The row is planted directly, and the same row is then read back through
    /// its OWN provider's context, so "invisible" is distinguished from "absent".
    ///
    /// Control: the ConsultationRequest query filter in PracticeDbContext.OnModelCreating.
    /// Deleted → red on the second assertion, "Assert.Empty() Failure: Collection was not
    /// empty". The first assertion stays green either way, correctly: it is there to show
    /// the row EXISTS, so that the second one is measuring visibility and not absence.
    /// </summary>
    [Fact]
    public async Task Another_providers_enquiry_is_invisible_through_the_query_filter()
    {
        var providers = await SeedActiveProvidersAsync(2);
        var mine = providers[0];
        var theirs = providers[1];

        await using (var db = DbFor(theirs.Id))
        {
            db.ConsultationRequests.Add(ConsultationRequest.Submit(
                theirs.Id, ParentMarker, "zephyrine@example.com", "410-555-0142",
                ChildMarker, 30, ConcernMarker, PreferredContactMethod.Email,
                SourceHash, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        // Visible to the provider it belongs to...
        Assert.Single(await RequestsForAsync(theirs.Id));

        // ...and to nobody else.
        Assert.Empty(await RequestsForAsync(mine.Id));
    }

    // -------------------------------------------------------- the notification

    /// <summary>
    /// A stored enquiry is announced once, carrying its opaque id.
    ///
    /// Control: the <c>await notifier.NotifyAsync(publicId).WaitAsync(deadline.Token)</c>
    /// call in SubmitConsultationRequest.
    /// Deleted → red, "Assert.Single() Failure: The collection was empty". Re-run against
    /// the new home when that statement gained its <c>WaitAsync</c> bound (D077): same
    /// deletion, same message.
    /// </summary>
    [Fact]
    public async Task A_stored_enquiry_is_announced_exactly_once()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());
        var created = await response.Content.ReadFromJsonAsync<SubmittedConsultationRequest>();

        var announced = Assert.Single(_notifications.Notified);
        Assert.Equal(created!.PublicId, announced);
    }

    /// <summary>
    /// Nothing is announced for an enquiry that did not survive its transaction.
    ///
    /// An email describing an arrival that rolled back sends Michelle to look for a record
    /// that is not there — and, once the notifier is a real mail sender, cannot be taken
    /// back. The ordering is the control: notify after the commit, never inside the body,
    /// which is also re-run on every retry.
    ///
    /// Forced with an audit writer that cannot write, because a run where everything
    /// succeeds cannot tell "announced afterwards" from "announced inside".
    ///
    /// Control: the position of the NotifyAsync call, AFTER WriteAtomicallyAsync returns.
    /// Moved inside the write body, immediately after the row is saved and BEFORE the audit
    /// write → red, "Assert.Empty() Failure: Collection was not empty, Collection:
    /// [7acbbd43-…]".
    ///
    /// WHERE inside the body matters, which the earlier version of this line did not say
    /// and which is worth knowing before somebody re-runs it. Moved to the END of the body,
    /// after <c>audit.WriteAsync</c>, the test stays GREEN — this harness breaks the audit
    /// write, so the notifier is never reached and "nothing was announced" is true for the
    /// wrong reason. Re-run at both positions when the statement gained its
    /// <c>WaitAsync(deadline.Token)</c> bound (D077).
    /// </summary>
    [Fact]
    public async Task An_enquiry_that_could_not_be_stored_is_never_announced()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api(extra: services =>
            services.AddScoped<IAuditWriter, UnwritableAuditWriter>());
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(_notifications.Notified);
    }

    /// <summary>
    /// The enquiry and its audit entry commit together, or neither does.
    ///
    /// The audit row is the ONLY record that an anonymous write was attempted — there is
    /// no session, no actor id, and nobody to ask afterwards — so a design where the row
    /// lands and the audit save fails would lose exactly the evidence a submission flood
    /// consists of.
    ///
    /// Control: the transaction in AtomicWrites.WriteAtomicallyAsync — its
    /// BeginTransactionAsync / CommitAsync pair.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 0, Actual: 1" —
    /// the enquiry survives the audit failure.
    /// </summary>
    [Fact]
    public async Task The_enquiry_and_its_audit_entry_commit_together()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api(extra: services =>
            services.AddScoped<IAuditWriter, UnwritableAuditWriter>());
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
    }

    /// <summary>
    /// One submission leaves one row and one audit entry, however many attempts it took.
    ///
    /// Azure SQL serverless auto-pauses, so a retry is the ordinary case here rather than
    /// the exotic one — and a DbContext carries state across attempts. AuditEvents is
    /// append-only by grant, so a duplicate is permanent and says two families enquired.
    ///
    /// Control: the <c>db.ChangeTracker.Clear()</c> at the top of each attempt in
    /// AtomicWrites.WriteAtomicallyAsync.
    /// Deleted → red, "Assert.Single() Failure: The collection contained 2 items" — the
    /// audit entity the failed attempt staged is still Added, and the next save inserts
    /// both.
    /// </summary>
    [Fact]
    public async Task A_retried_submission_stores_one_row_and_one_audit_entry()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api(extra: services =>
            FailureHarness.RetryOnceOnATransientBlip(_connectionString, services));
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        // The retry is meant to be invisible to the parent.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await RequestCountAsync());
        Assert.Single(await AuditEventsAsync(AuditEventType.ConsultationRequestReceived));
    }

    /// <summary>
    /// A mailbox that cannot be reached does not lose the enquiry — and does not pass
    /// unnoticed either.
    ///
    /// Failing the request would be a lie in the other direction: the row is committed, so
    /// telling the parent it did not work sends them round again and produces two
    /// enquiries for one family. What must not happen is the failure being swallowed —
    /// "Michelle was never told" is the same shape of defect as an audio deletion job that
    /// silently stops (WORK_QUEUE 4.6), and a silently failing notifier looks exactly like
    /// a working one.
    ///
    /// Control: the try/catch around notifier.NotifyAsync in SubmitConsultationRequest.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Created, Actual:
    /// InternalServerError". Re-run when that block gained a second clause — a
    /// <c>catch (OperationCanceledException)</c> for a notification abandoned at the
    /// ceiling, which falls through to the 201 deliberately (D077): same deletion, same
    /// message. The two clauses are not interchangeable and neither covers for the other:
    /// this test's mailbox THROWS, so it exercises the second clause only.
    /// </summary>
    [Fact]
    public async Task A_failed_notification_leaves_the_enquiry_stored_and_records_the_failure()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api(new UnreachableMailbox());
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await RequestCountAsync());

        var failure = Assert.Single(
            await AuditEventsAsync(AuditEventType.ConsultationNotificationFailed));
        Assert.Equal(AuditOutcome.Failure, failure.Outcome);

        // The arrival is still recorded as a success — it happened.
        Assert.Single(await AuditEventsAsync(AuditEventType.ConsultationRequestReceived));
    }

    /// <summary>
    /// A mailbox that never answers does not hold the parent past this tier's ceiling — and
    /// the parent is still told the enquiry was stored, because it was.
    ///
    /// WHY THIS IS ABOUT A CLASS THAT HAS NOT LANDED YET. <c>IConsultationNotifier</c> takes
    /// no CancellationToken, for the same reason IAuditWriter takes none (D075, D079): with
    /// one present, CA2016 forces every call site holding a token to forward it, and the
    /// analyser would enforce the defect. But unlike an audit write, this seam was on NO
    /// bound at all — not the request timeout it cannot observe, and not the
    /// uncancellable-write deadline either. That was harmless only while the implementation
    /// wrote a log line. The real mail transport is queued (WORK_QUEUE, Blocked — needs
    /// David), it is a network call to somebody else's infrastructure, and the day it lands
    /// it would silently move <c>DatabaseTimeouts.Ceiling</c> — the number the BFF's
    /// API_TIMEOUT_MS is sized against, and the number a parent's "was my enquiry stored?"
    /// depends on. This test is what makes that landing safe: it fails on any notifier that
    /// outlives the ceiling, whoever writes it.
    ///
    /// Both halves of the claim are in the elapsed time, for the same reason
    /// <c>RequestBoundsTests.The_ceiling_is_the_request_bound_plus_the_uncancellable_tail</c>
    /// asserts both: a response arriving before the request bound would mean there was no
    /// tail to bound and the test proved nothing.
    ///
    /// AND THE STATUS IS PART OF THE CLAIM. Abandoning the notification must not turn into a
    /// 500 — the row is committed by then, and <c>web/lib/api/consultations.ts</c> reads
    /// <c>!response.ok</c> as <c>{stored: false}</c>, which tells a family their enquiry was
    /// not recorded when it was. That is the defect D086 and D090 exist to prevent, reached
    /// by a different door.
    ///
    /// Control: the <c>.WaitAsync(deadline.Token)</c> on the notifier call in
    /// ConsultationEndpoints.SubmitConsultationRequest.
    /// Deleted → red after 20 seconds, "The notification held the request for 20.2s past a
    /// 1s request bound. IConsultationNotifier holds no request token by design, so unless
    /// the call site bounds it the ceiling DatabaseTimeouts.Ceiling states is whatever a
    /// mail transport feels like taking — and the BFF's API_TIMEOUT_MS is sized against
    /// that number."
    /// </summary>
    [Fact]
    public async Task A_notification_that_never_answers_does_not_outlive_the_ceiling()
    {
        await SeedActiveProvidersAsync(1);

        var requestBound = TimeSpan.FromSeconds(1);
        var grace = TimeSpan.FromSeconds(2);
        var unanswering = TimeSpan.FromSeconds(20);

        using var api = Api(
            new SilentMailbox(unanswering),
            services =>
            {
                // A distant backstop, so the deadline arriving on time is attributable to
                // ProviderContextMiddleware's binding rather than to the fallback.
                FailureHarness.BoundedBy(services, backstop: TimeSpan.FromSeconds(60), grace);

                services.Configure<RequestTimeoutOptions>(options => options.DefaultPolicy =
                    new RequestTimeoutPolicy { Timeout = requestBound });
            });

        using var client = api.CreateClient();

        // Warm the host, the pool and the query plans, so what is measured below is the
        // request rather than everything a first request drags in with it.
        (await client.GetAsync("/health/live")).Dispose();

        var started = Stopwatch.GetTimestamp();
        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission());
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await RequestCountAsync());

        Assert.True(
            elapsed > requestBound,
            $"The response arrived in {elapsed.TotalSeconds:0.0}s, within the "
            + $"{requestBound.TotalSeconds:0}s request bound. This test has to reach a "
            + "notification that is still running when that bound fires, or it measures "
            + "nothing.");

        var ceiling = requestBound + grace + TimeSpan.FromSeconds(3);

        Assert.True(
            elapsed < ceiling,
            $"The notification held the request for {elapsed.TotalSeconds:0.0}s past a "
            + $"{requestBound.TotalSeconds:0}s request bound. IConsultationNotifier holds "
            + "no request token by design, so unless the call site bounds it the ceiling "
            + "DatabaseTimeouts.Ceiling states is whatever a mail transport feels like "
            + "taking — and the BFF's API_TIMEOUT_MS is sized against that number.");
    }

    /// <summary>
    /// A mailbox that accepts the message and never answers.
    ///
    /// The shape a real transport fails in far more often than by throwing: a TCP
    /// connection to a mail service that is up but wedged. <see cref="UnreachableMailbox"/>
    /// covers the loud failure; nothing covered the quiet one, which is the one that moves
    /// a ceiling.
    ///
    /// It waits on its OWN token — not the caller's, which it has none of — so it stops
    /// only when whatever is bounding it stops it. That is the point: a notifier that
    /// cooperated would prove nothing about the bound.
    /// </summary>
    private sealed class SilentMailbox(TimeSpan silence) : IConsultationNotifier
    {
        public Task NotifyAsync(Guid consultationRequestPublicId) => Task.Delay(silence);
    }

    // ---------------------------------------------------------- hostile input

    /// <summary>
    /// Server-side bounds, on the assumption the browser was never involved.
    ///
    /// The BFF validates this same field, and that is a convenience for the parent rather
    /// than a control (docs/SECURITY.md). Nothing here may be skipped because something
    /// upstream already checked.
    ///
    /// Control: the length check inside Guard.MaxLength, which
    /// ConsultationRequest.Submit calls for Concerns.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: BadRequest,
    /// Actual: InternalServerError".
    ///
    /// NOT the Created that was expected before running it, and the difference is the
    /// point: with the guard gone the value reaches nvarchar(2000) and SQL Server refuses
    /// it, so the column is a second control covering for the first (the D077 shape). It
    /// covers BADLY — a 500 with no field named, instead of a 400 the BFF can act on —
    /// which is why the aggregate holds the rule and the column merely agrees with it.
    /// </summary>
    [Fact]
    public async Task An_over_long_description_is_refused_and_nothing_is_written()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests",
            NewSubmission(concerns: new string('x', ConsultationRequest.MaxConcernsLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
        Assert.Empty(_notifications.Notified);
    }

    /// <summary>
    /// A raw address offered in place of its hash is refused, not stored.
    ///
    /// The point of SourceIpHash is that this application never holds a visitor
    /// identifier. A caller — including a future version of our own BFF — that passed the
    /// address through would defeat that silently, because the column would happily take
    /// it.
    ///
    /// Control: the sourceIpHash format check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: BadRequest,
    /// Actual: Created".
    /// </summary>
    [Fact]
    public async Task A_raw_address_in_place_of_a_hash_is_refused()
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission(sourceIpHash: "203.0.113.7"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
    }

    /// <summary>
    /// A contact preference outside the enum is refused, by NAME and by NUMBER.
    ///
    /// The two cases are stopped by two different clauses, which is why they are one
    /// theory with the same assertion rather than one case: "Carrier pigeon" fails the
    /// endpoint's Enum.TryParse, and "99" gets PAST it — TryParse accepts numeric strings
    /// — and is refused by Enum.IsDefined inside the aggregate. A single case would leave
    /// whichever clause it missed untested.
    ///
    /// Control: the Enum.IsDefined check in ConsultationRequest.Submit — the clause that
    /// catches the numeric case, and the one an endpoint-shaped test would miss.
    /// Deleted → red on the "99" case, "Assert.Equal() Failure: Values differ, Expected:
    /// BadRequest, Actual: Created", and green on the other, correctly.
    /// </summary>
    [Theory]
    [InlineData("Carrier pigeon")]
    [InlineData("99")]
    public async Task A_contact_preference_outside_the_enum_is_refused(string preference)
    {
        await SeedActiveProvidersAsync(1);

        using var api = Api();
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/consultation-requests", NewSubmission(preferredContactMethod: preference));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RequestCountAsync());
    }
}
