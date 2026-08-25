using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.Consultations;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Consultations;
using Practice.Domain.Patients;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The consultation inbox — the destination "sign in to view" points at.
///
/// The intake side of this table is tested in <see cref="ConsultationIntakeTests"/>, and
/// the questions there are about a hostile stranger writing. The questions HERE are about
/// reading: an enquiry holds a child's first name beside a parent's description of that
/// child's difficulties, which is not PHI by the regulation's definition and is the same
/// category of information by every other measure (ConsultationRequest's own docstring).
/// So it is scoped like patient data, refused like patient data, and audited like patient
/// data — on the endpoint the product actually calls, which is the whole of D065.
///
/// SYNTHETIC DATA ONLY. The names below are invented tokens rather than plausible ones,
/// because several assertions are "this string appears NOWHERE in X" and a child called
/// "Emma" gives a false pass the moment "emma" occurs inside a GUID.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class ConsultationInboxTests(SqlServerFixture sql) : IDisposable
{
    private readonly PracticeApiFactory _factory = new(sql.ConnectionString);

    public void Dispose() => _factory.Dispose();

    private const string ParentMarker = "Thessaly Undermarch";
    private const string ChildMarker = "Brambleworth";
    private const string ConcernMarker =
        "Nyxobalt: about ten single words, no combinations, and real frustration at bedtime.";

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A provider, and both of its identifiers.
    ///
    /// The public id is what the BFF forwards; the internal one is what an enquiry row
    /// carries. Tests need both, which is why this returns the entity rather than a Guid.
    /// </summary>
    private async Task<Provider> SeedProviderAsync(string name)
    {
        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        var user = new PracticeUser { UserName = email, Email = email };
        await users.CreateAsync(user, "correct-horse-battery-staple");

        var provider = Provider.Create(user.Id, name, "M.S., CCC-SLP", "SLP-1", "MD");
        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return provider;
    }

    /// <summary>A client that presents a provider identity, as the BFF does.</summary>
    private HttpClient ClientFor(Guid providerPublicId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    /// <summary>
    /// An enquiry, written straight to the table rather than posted through the form.
    ///
    /// POST /consultation-requests resolves THE SOLE ACTIVE PROVIDER and refuses when the
    /// answer is ambiguous (D078), and every class in this collection seeds providers as
    /// it goes — so the public route cannot be used to set up a two-provider tenancy test.
    /// Building the aggregate directly gives each row an owner the test chose, which is
    /// exactly what these tests are about.
    /// </summary>
    private async Task<ConsultationRequest> SeedEnquiryAsync(
        long providerId,
        string parentName = ParentMarker,
        string childFirstName = ChildMarker,
        DateTime? submittedAtUtc = null)
    {
        await using var db = DbFor(null);

        var enquiry = ConsultationRequest.Submit(
            providerId,
            parentName,
            "thessaly@example.com",
            "410-555-0142",
            childFirstName,
            30,
            ConcernMarker,
            PreferredContactMethod.Either,
            sourceIpHash: null,
            submittedAtUtc ?? new DateTime(2026, 8, 20, 14, 30, 0, DateTimeKind.Utc));

        db.ConsultationRequests.Add(enquiry);
        await db.SaveChangesAsync();

        return enquiry;
    }

    /// <summary>
    /// A context scoped to one provider, for reading rows back THROUGH the query filter.
    ///
    /// A null provider matches nothing, which is what lets a tenancy test tell "filtered
    /// out" from "not there" — and what lets the seeder above write a row it could not
    /// then read.
    /// </summary>
    private PracticeDbContext DbFor(long? providerId) =>
        new(
            new DbContextOptionsBuilder<PracticeDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options,
            new FixedProviderContext(providerId));

    private async Task<ConsultationRequest?> ReloadAsync(long providerId, Guid publicId)
    {
        await using var db = DbFor(providerId);
        return await db.ConsultationRequests.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PublicId == publicId);
    }

    /// <summary>
    /// Audit rows of one type, narrowed to one subject or one provider.
    ///
    /// The narrowing is not optional in practice. This collection shares one database and
    /// every class in it writes audit rows as it goes, so an unfiltered count answers a
    /// question about the whole run rather than about this test. A provider seeded by this
    /// test is the narrowest handle a listing row offers, because a list has no subject.
    /// </summary>
    private async Task<List<AuditEvent>> AuditEventsAsync(
        AuditEventType type, Guid? entity = null, long? providerId = null)
    {
        await using var db = DbFor(null);

        // AuditEvents carries no query filter — it is the record OF tenancy, not a tenant
        // table — so a null provider context reads all of it.
        var query = db.AuditEvents.AsNoTracking().Where(e => e.EventType == type);
        if (entity is not null) query = query.Where(e => e.EntityPublicId == entity);
        if (providerId is not null) query = query.Where(e => e.ProviderId == providerId);

        return await query.ToListAsync();
    }

    private static ConvertConsultationRequest NewConversion(
        string lastName = "Undermarch", int year = 2024) =>
        new(lastName, new DateOnly(year, 2, 24));

    // ------------------------------------------------------------------ the list

    /// <summary>
    /// The inbox: unanswered enquiries first, and within a status the newest first.
    ///
    /// The ordering is the product decision, not a detail. Michelle opens this between
    /// houses to find the families nobody has replied to, and an inbox sorted by arrival
    /// alone buries a new enquiry under a year of declined ones.
    ///
    /// Control: ConsultationEndpoints.ListConsultationRequests — the
    /// <c>OrderBy(c =&gt; c.Status).ThenByDescending(c =&gt; c.SubmittedAtUtc)</c>.
    /// Deleted → red, "Assert.Equal() Failure: Collections differ" — the older enquiry
    /// arrives first.
    /// </summary>
    [Fact]
    public async Task The_inbox_shows_unanswered_enquiries_first_then_the_newest()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var older = await SeedEnquiryAsync(
            michelle.Id, submittedAtUtc: new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = await SeedEnquiryAsync(
            michelle.Id, submittedAtUtc: new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc));
        var answered = await SeedEnquiryAsync(
            michelle.Id, submittedAtUtc: new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc));

        using var marked = await client.PostAsync(
            $"/consultation-requests/{answered.PublicId}/contacted", null);
        marked.EnsureSuccessStatusCode();

        var inbox = await client.GetFromJsonAsync<List<ConsultationRequestSummary>>(
            "/consultation-requests");

        Assert.Equal(
            [newer.PublicId, older.PublicId, answered.PublicId],
            inbox!.Select(e => e.PublicId));
    }

    /// <summary>
    /// The list carries enough to triage and NOT what the parent wrote.
    ///
    /// A description of a child's difficulties is the most sensitive thing on this row,
    /// and an inbox does not need it — it needs a name, an age, and how long they have
    /// been waiting. Keeping it off the summary means exactly one endpoint discloses it,
    /// which is the endpoint that audits the disclosure (D065). A list that carried it
    /// would be an unaudited read of the same content, one fetch away from the audited one.
    ///
    /// Asserted on the RAW body rather than the deserialised record, so a field added to
    /// the summary later is caught rather than ignored.
    ///
    /// Control: the ConsultationRequestSummary record — that it has no Concerns member.
    /// Given a <c>string Concerns</c> populated from the row → red,
    /// "Assert.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task The_list_does_not_carry_what_the_parent_wrote_about_their_child()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        await SeedEnquiryAsync(michelle.Id);

        using var response = await client.GetAsync("/consultation-requests");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(ConcernMarker, body, StringComparison.Ordinal);

        // The triage fields ARE there — otherwise the assertion above would pass on an
        // empty list and prove nothing.
        Assert.Contains(ParentMarker, body, StringComparison.Ordinal);
        Assert.Contains(ChildMarker, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Another provider's enquiry is not in this provider's inbox.
    ///
    /// The row is planted with the stranger's own ProviderId, so the ONLY thing between it
    /// and Michelle's list is the query filter.
    ///
    /// Control: the ConsultationRequest global query filter in PracticeDbContext.
    /// Deleted → red on Assert.Single, "Assert.Single() Failure: The collection contained
    /// 2 items".
    /// </summary>
    [Fact]
    public async Task An_enquiry_belonging_to_another_provider_is_not_in_the_inbox()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        using var client = ClientFor(michelle.PublicId);

        var mine = await SeedEnquiryAsync(michelle.Id);
        await SeedEnquiryAsync(stranger.Id, parentName: "Someone Else");

        var inbox = await client.GetFromJsonAsync<List<ConsultationRequestSummary>>(
            "/consultation-requests");

        var only = Assert.Single(inbox!);
        Assert.Equal(mine.PublicId, only.PublicId);
    }

    /// <summary>
    /// Filtering to the ones nobody has answered, which is what the index on
    /// (ProviderId, Status, SubmittedAtUtc) was built for.
    ///
    /// Control: ConsultationEndpoints.ListConsultationRequests — the
    /// <c>query.Where(c =&gt; c.Status == wanted)</c> branch.
    /// Deleted → red on Assert.Single, "Assert.Single() Failure: The collection contained
    /// 2 items".
    /// </summary>
    [Fact]
    public async Task The_inbox_can_be_narrowed_to_one_status()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var untouched = await SeedEnquiryAsync(michelle.Id);
        var answered = await SeedEnquiryAsync(michelle.Id);

        using var marked = await client.PostAsync(
            $"/consultation-requests/{answered.PublicId}/contacted", null);
        marked.EnsureSuccessStatusCode();

        var inbox = await client.GetFromJsonAsync<List<ConsultationRequestSummary>>(
            "/consultation-requests?status=New");

        var only = Assert.Single(inbox!);
        Assert.Equal(untouched.PublicId, only.PublicId);
    }

    /// <summary>
    /// A status nobody defined is a 400 naming the field, not an empty inbox.
    ///
    /// An unparsed filter silently ignored is worse than a refusal: the screen renders
    /// "nothing to answer" for a practice with a full inbox, and looks like it worked.
    ///
    /// Control: ConsultationEndpoints.ListConsultationRequests — the
    /// <c>Enum.TryParse</c> failure branch. Deleted (the filter left unparsed and ignored)
    /// → red, "Assert.Equal() Failure: Expected: BadRequest / Actual: OK".
    /// </summary>
    [Fact]
    public async Task A_status_filter_outside_the_vocabulary_is_refused()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        using var response = await client.GetAsync("/consultation-requests?status=Pending");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ the detail

    /// <summary>
    /// Reading one enquiry returns what the parent wrote, and audits the read.
    ///
    /// THIS IS THE D065 TEST, and it is written against the route the detail page fetches
    /// rather than against a sibling that happens to audit correctly. The finding there
    /// was that the audited endpoint had stopped being the one the product called; the
    /// assertion below therefore reads the content and the audit row out of the SAME
    /// response.
    ///
    /// Control: ConsultationEndpoints.GetConsultationRequest — the audit.WriteAsync call.
    /// Deleted → red, "Assert.Single() Failure: The collection was empty".
    /// </summary>
    [Fact]
    public async Task Reading_an_enquiry_discloses_the_concerns_and_audits_the_read()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        var detail = await client.GetFromJsonAsync<ConsultationRequestDetail>(
            $"/consultation-requests/{enquiry.PublicId}");

        Assert.Equal(ConcernMarker, detail!.Concerns);
        Assert.Equal(ParentMarker, detail.ParentName);
        Assert.Equal(ChildMarker, detail.ChildFirstName);
        Assert.Equal("New", detail.Status);
        Assert.Null(detail.ConvertedPatientPublicId);

        // Every *Utc value crosses the wire as UTC (D072); a value read back from
        // datetime2 with no Kind serialises without a Z and moves by five hours.
        Assert.Equal(DateTimeKind.Utc, detail.SubmittedAtUtc.Kind);

        var audited = Assert.Single(
            await AuditEventsAsync(AuditEventType.ConsultationRequestViewed, enquiry.PublicId));

        Assert.Equal(AuditOutcome.Success, audited.Outcome);
        Assert.Equal(michelle.Id, audited.ProviderId);
        Assert.Equal(nameof(ConsultationRequest), audited.EntityType);
    }

    /// <summary>
    /// The list is a disclosure too, and says how much of one.
    ///
    /// Every row it returns carries a parent's name and a child's first name. "Somebody
    /// opened the inbox" cannot tell one enquiry from forty afterwards, which is the only
    /// time this table is read — the same argument D065 makes for <c>versions=n</c> on a
    /// note history. The count is not content.
    ///
    /// Control: ConsultationEndpoints.ListConsultationRequests — the audit.WriteAsync call.
    /// Deleted → red, "Assert.Single() Failure: The collection did not contain any
    /// matching items".
    /// </summary>
    [Fact]
    public async Task Listing_the_inbox_records_how_many_enquiries_were_disclosed()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        await SeedEnquiryAsync(michelle.Id);
        await SeedEnquiryAsync(michelle.Id);

        using var response = await client.GetAsync("/consultation-requests");
        response.EnsureSuccessStatusCode();

        // The list's row is the one with no single subject; the detail reads name theirs.
        var listed = Assert.Single(
            await AuditEventsAsync(
                AuditEventType.ConsultationRequestViewed, providerId: michelle.Id),
            e => e.EntityPublicId is null);

        Assert.Equal(AuditOutcome.Success, listed.Outcome);
        Assert.Equal(michelle.Id, listed.ProviderId);
        Assert.Contains("count=2", listed.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An enquiry belonging to someone else is indistinguishable from one that is not
    /// there — 404 both ways, byte for byte (D052).
    ///
    /// Control: the ConsultationRequest global query filter in PracticeDbContext.
    /// Deleted → red on the first assertion, "Assert.Equal() Failure: Expected: NotFound /
    /// Actual: OK" — the stranger reads another practice's enquiry.
    /// </summary>
    [Fact]
    public async Task An_unreachable_enquiry_is_indistinguishable_from_a_missing_one()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        using var client = ClientFor(stranger.PublicId);

        var hers = await SeedEnquiryAsync(michelle.Id);

        using var foreign = await client.GetAsync($"/consultation-requests/{hers.PublicId}");
        using var absent = await client.GetAsync($"/consultation-requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A read that returned nothing writes no audit row.
    ///
    /// The row says a disclosure happened. One written on a 404 would say a stranger saw
    /// an enquiry they were refused, and a count of "who read this family's enquiry" would
    /// include somebody who read nothing.
    ///
    /// Control: ConsultationEndpoints.GetConsultationRequest — the
    /// <c>if (enquiry is null) return Results.NotFound()</c> guard ABOVE the audit write.
    /// Moved below the write → red, "Assert.Empty() Failure: Collection was not empty".
    /// </summary>
    [Fact]
    public async Task A_refused_read_discloses_nothing_and_records_no_disclosure()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        using var client = ClientFor(stranger.PublicId);

        var hers = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.GetAsync($"/consultation-requests/{hers.PublicId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Empty(
            await AuditEventsAsync(AuditEventType.ConsultationRequestViewed, hers.PublicId));
    }

    /// <summary>
    /// No provider identity reaches nothing — not an empty list, a refusal.
    ///
    /// Control: ConsultationEndpoints — the <c>provider.ProviderId is null</c> guard on
    /// ListConsultationRequests. Deleted → red, "Assert.Equal() Failure: Expected:
    /// Unauthorized / Actual: OK" (the filter answers with an empty list, which reads to a
    /// caller as "this practice has no enquiries").
    /// </summary>
    [Fact]
    public async Task A_request_with_no_provider_identity_is_rejected()
    {
        var michelle = await SeedProviderAsync("Michelle");
        await SeedEnquiryAsync(michelle.Id);

        using var anonymous = _factory.CreateClient();

        using var list = await anonymous.GetAsync("/consultation-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    // ------------------------------------------------------------------ transitions

    /// <summary>
    /// Michelle replied. The row moves and the move is on file.
    ///
    /// Control: ConsultationEndpoints.ApplyTransitionAsync — the
    /// <c>enquiry.MarkContacted()</c> / transition delegate invocation.
    /// Deleted → red, "Assert.Equal() Failure: Strings differ / Expected: Contacted /
    /// Actual: New".
    /// </summary>
    [Fact]
    public async Task Marking_an_enquiry_contacted_moves_it_and_is_audited()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.PostAsync(
            $"/consultation-requests/{enquiry.PublicId}/contacted", null);

        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<ConsultationRequestSummary>();
        Assert.Equal("Contacted", summary!.Status);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.Contacted, stored!.Status);

        var audited = Assert.Single(
            await AuditEventsAsync(AuditEventType.ConsultationRequestUpdated, enquiry.PublicId));
        Assert.Contains("action=contacted", audited.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declining is a transition, not a delete. The family's enquiry stays on the record.
    ///
    /// Control: ConsultationEndpoints — the MapPost route registration for
    /// <c>/{publicId:guid}/declined</c>. Deleted → red, "Assert.Equal() Failure: Expected:
    /// OK / Actual: NotFound".
    /// </summary>
    [Fact]
    public async Task Declining_an_enquiry_keeps_the_row()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.PostAsync(
            $"/consultation-requests/{enquiry.PublicId}/declined", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.Declined, stored!.Status);
        Assert.Equal(ParentMarker, stored.ParentName);
    }

    /// <summary>
    /// A closed enquiry is refused, in the aggregate's own words.
    ///
    /// The sentence explains a rule rather than reporting a malfunction, so it is surfaced
    /// as a 409 the BFF passes through — the same treatment a goal that is already closed
    /// gets.
    ///
    /// Control: ConsultationEndpoints.RefusalToTransition — the closed-status branch.
    /// Deleted → red, "Assert.Equal() Failure: Expected: Conflict / Actual:
    /// InternalServerError" (the aggregate still refuses, as an unhandled
    /// InvalidOperationException inside the transaction).
    /// </summary>
    [Fact]
    public async Task A_closed_enquiry_cannot_be_reopened_by_a_transition()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var declined = await client.PostAsync(
            $"/consultation-requests/{enquiry.PublicId}/declined", null);
        declined.EnsureSuccessStatusCode();

        using var response = await client.PostAsync(
            $"/consultation-requests/{enquiry.PublicId}/contacted", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.Declined, stored!.Status);
    }

    /// <summary>
    /// A transition on another provider's enquiry is the refusal an absent one produces.
    ///
    /// Hiding the button is not authorization (CLAUDE.md non-negotiable #6): this posts
    /// straight at the route with a valid session belonging to somebody else.
    ///
    /// Control: the ConsultationRequest global query filter in PracticeDbContext.
    /// Deleted → red on the status code, "Assert.Equal() Failure: Expected: NotFound /
    /// Actual: OK" — the stranger closes another practice's enquiry.
    /// </summary>
    [Fact]
    public async Task A_provider_cannot_close_another_providers_enquiry()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var stranger = await SeedProviderAsync("Stranger");
        using var client = ClientFor(stranger.PublicId);

        var hers = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.PostAsync(
            $"/consultation-requests/{hers.PublicId}/declined", null);
        using var absent = await client.PostAsync(
            $"/consultation-requests/{Guid.NewGuid()}/declined", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(absent.StatusCode, response.StatusCode);

        var stored = await ReloadAsync(michelle.Id, hers.PublicId);
        Assert.Equal(ConsultationStatus.New, stored!.Status);
    }

    // ------------------------------------------------------------------ conversion

    /// <summary>
    /// The enquiry becomes a patient: one patient created, one enquiry linked to it.
    ///
    /// The child's first name and the age the parent gave come off the enquiry; the
    /// surname and the date of birth are asked for, because the public form never collects
    /// them — it asks a first name and an age in months on purpose.
    ///
    /// Control: ConsultationEndpoints.ConvertConsultationRequest — the
    /// <c>enquiry.ConvertTo(patient.Id)</c> call.
    /// Deleted → red, "Assert.Equal() Failure: Strings differ / Expected: Converted /
    /// Actual: New" — the patient is created and the enquiry never learns of it.
    /// </summary>
    [Fact]
    public async Task Converting_an_enquiry_creates_the_patient_and_links_it()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());

        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<ConsultationRequestSummary>();

        Assert.Equal("Converted", summary!.Status);
        Assert.NotNull(summary.ConvertedPatientPublicId);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.Converted, stored!.Status);
        Assert.NotNull(stored.ConvertedPatientId);

        // The new patient is reachable through the ordinary patient route, carrying the
        // child's first name from the enquiry and the surname the clinician supplied.
        var patient = await client.GetFromJsonAsync<Practice.Api.Patients.PatientDetail>(
            $"/patients/{summary.ConvertedPatientPublicId}");

        Assert.Equal(ChildMarker, patient!.FirstName);
        Assert.Equal("Undermarch", patient.LastName);
    }

    /// <summary>
    /// The link is an opaque public id, never the clustered key.
    ///
    /// A sequential integer on the wire is an enumeration oracle and a patient-facing
    /// identifier this project does not use anywhere (CLAUDE.md conventions). The row
    /// stores the internal id — a real foreign key is the only thing that keeps the link
    /// honest — and the endpoint resolves it to the Guid on the way out.
    ///
    /// Control: ConsultationRequestSummary.ConvertedPatientPublicId being a <c>Guid?</c>,
    /// together with the subquery over db.Patients that resolves it. Substituting the raw
    /// ConvertedPatientId does not compile — long is not Guid?, and the type is half the
    /// control. Changed to <c>long?</c> so it does, with SummaryOf and the list projection
    /// handing over the row id → red, "Assert.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task The_converted_patient_is_named_by_a_guid_and_never_by_a_row_id()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var converted = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());
        converted.EnsureSuccessStatusCode();

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        var body = await converted.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            $":{stored!.ConvertedPatientId}", body, StringComparison.Ordinal);

        var summary = await client.GetFromJsonAsync<List<ConsultationRequestSummary>>(
            "/consultation-requests");

        Assert.NotNull(Assert.Single(summary!).ConvertedPatientPublicId);
    }

    /// <summary>
    /// An enquiry already converted is refused, and does NOT produce a second patient.
    ///
    /// A double tap on a phone with a slow connection is the ordinary way to reach this,
    /// and the expensive failure is not the refusal — it is two records for one child, on
    /// a caseload where the second one silently collects half the sessions.
    ///
    /// Control: ConsultationEndpoints.RefusalToTransition — the <c>Converted</c> branch.
    /// Deleted → red on the status, "Assert.Equal() Failure: Values differ / Expected:
    /// Conflict / Actual: InternalServerError".
    ///
    /// AND THE PATIENT COUNT STAYED AT ONE, which is worth writing down rather than
    /// leaving as a prediction nobody checked. With the branch gone the second request
    /// creates a patient, and then ConsultationRequest.ConvertTo refuses a converted
    /// enquiry from inside the transaction — so the insert rolls back with it. The
    /// duplicate is prevented by the aggregate; what this branch buys is the difference
    /// between a refusal a clinician can act on and a 500 with a trace id (D064's three
    /// layers, doing their job one layer down). The second assertion here does not isolate
    /// this control, and the first one does.
    /// </summary>
    [Fact]
    public async Task Converting_an_enquiry_twice_is_refused_and_creates_one_patient()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var first = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());
        first.EnsureSuccessStatusCode();

        using var second = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var db = DbFor(michelle.Id);
        Assert.Single(await db.Patients.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A date of birth the domain refuses is a 400 the clinician can read, and no patient.
    ///
    /// Patient.Create refuses a birthdate in the future and one implausibly far back. That
    /// refusal has to arrive before anything is written, or a conversion leaves a linked
    /// enquiry pointing at a patient that never existed.
    ///
    /// Control: ConsultationEndpoints.ConvertConsultationRequest — the
    /// <c>catch (ArgumentException)</c> around the build, which answers 400.
    /// Deleted → red, "Assert.Equal() Failure: Expected: BadRequest / Actual:
    /// InternalServerError".
    /// </summary>
    [Fact]
    public async Task A_date_of_birth_the_record_refuses_writes_nothing()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var response = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert",
            new ConvertConsultationRequest("Undermarch", new DateOnly(1957, 3, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.New, stored!.Status);

        await using var db = DbFor(michelle.Id);
        Assert.Empty(await db.Patients.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The patient, the link and the audit row commit together, or none of them does.
    ///
    /// A conversion that created a child's record and left the enquiry saying New is the
    /// state that produces the duplicate above on the next tap. Forced by making the audit
    /// write — the last thing in the body — fail, which is the only way to observe the
    /// boundary at all: a run where everything succeeds looks identical either way.
    ///
    /// Control: ConsultationEndpoints.ConvertConsultationRequest — the
    /// <c>db.WriteAtomicallyAsync(ConvertTheEnquiry, ct)</c> wrapper, replaced with
    /// <c>await ConvertTheEnquiry(ct)</c> so the body runs with no transaction around it.
    /// Deleted → red on the enquiry's status, which is the assertion reached first:
    /// "Assert.Equal() Failure: Values differ / Expected: New / Actual: Converted". The
    /// child's record and the link both survive a conversion the API answered 500 to.
    /// </summary>
    [Fact]
    public async Task A_conversion_that_cannot_be_audited_leaves_no_patient_behind()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var api = new PracticeApiFactory(
            sql.ConnectionString,
            services => services.AddScoped<IAuditWriter, UnwritableAuditWriter>());

        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, michelle.PublicId.ToString());

        using var response = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var stored = await ReloadAsync(michelle.Id, enquiry.PublicId);
        Assert.Equal(ConsultationStatus.New, stored!.Status);
        Assert.Null(stored.ConvertedPatientId);

        await using var db = DbFor(michelle.Id);
        Assert.Empty(await db.Patients.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A conversion decided outside the transaction and applied inside it would act on a
    /// row that has moved. It is re-read and re-checked inside instead (D081).
    ///
    /// FORCED, NOT RACED. Michelle taps Convert on her phone; the tap she made a moment
    /// earlier on the tablet in her bag lands in the gap between this request's two reads
    /// of ConsultationRequests. The interceptor makes that ordering a certainty rather
    /// than a coincidence — two live requests reproduce it once in thousands of runs and
    /// never in CI.
    ///
    /// Control: ConsultationEndpoints.ConvertConsultationRequest — the second
    /// <c>RefusalToTransition</c> call, the one INSIDE the atomic write.
    /// Deleted → red on the status, "Assert.Equal() Failure: Values differ / Expected:
    /// Conflict / Actual: InternalServerError".
    ///
    /// THE PATIENT COUNT STAYED AT ONE with the control deleted, and saying so is the
    /// honest version of this test. Without the re-check the request builds a second
    /// patient, saves it, and is then refused by ConsultationRequest.ConvertTo from inside
    /// the transaction — so the rollback takes the duplicate away. The aggregate is what
    /// prevents two records; the re-check is what makes the race answer 409 with a
    /// sentence rather than 500 with a trace id, which is precisely the outcome D081
    /// closed one window earlier on the discard path. The second assertion below does not
    /// isolate this control and is kept because the duplicate is the consequence that
    /// matters: if the aggregate's guard is ever relaxed, this is where it shows.
    /// </summary>
    [Fact]
    public async Task An_enquiry_converted_while_this_request_was_deciding_is_refused()
    {
        var michelle = await SeedProviderAsync("Michelle");
        var enquiry = await SeedEnquiryAsync(michelle.Id);

        // Through a context of its own, so the interleaved write is not part of the
        // request under test and its reads are not part of the interceptor's count.
        async Task ConvertOnTheOtherDevice()
        {
            await using var other = DbFor(michelle.Id);

            var row = await other.ConsultationRequests
                .SingleAsync(c => c.PublicId == enquiry.PublicId);

            var patient = Patient.Create(
                michelle.Id, ChildMarker, "Undermarch",
                new DateOnly(2024, 2, 24), new DateOnly(2026, 8, 25));

            other.Patients.Add(patient);
            await other.SaveChangesAsync();

            row.ConvertTo(patient.Id);
            await other.SaveChangesAsync();
        }

        var interleave = new InterleavesOneWriteBeforeTheSecondRead(
            "ConsultationRequests", ConvertOnTheOtherDevice);

        using var api = new PracticeApiFactory(
            sql.ConnectionString,
            FailureHarness.With(sql.ConnectionString, interleave));

        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, michelle.PublicId.ToString());

        interleave.Arm();

        using var response = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // One child, one record. The point of the whole test.
        await using var db = DbFor(michelle.Id);
        Assert.Single(await db.Patients.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------------ the audit table

    /// <summary>
    /// Nothing a parent wrote reaches the audit table.
    ///
    /// It is the table docs/SECURITY.md says is never purged and is the most likely to be
    /// exported, so a child's first name in a metadata string outlives every retention
    /// policy this application has.
    ///
    /// Control: the metadata strings in ConsultationEndpoints — that they are a fixed
    /// vocabulary and opaque ids. Given <c>child={enquiry.ChildFirstName}</c> → red,
    /// "Assert.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task The_audit_table_never_carries_the_parents_words()
    {
        var michelle = await SeedProviderAsync("Michelle");
        using var client = ClientFor(michelle.PublicId);

        var enquiry = await SeedEnquiryAsync(michelle.Id);

        using var read = await client.GetAsync($"/consultation-requests/{enquiry.PublicId}");
        read.EnsureSuccessStatusCode();
        using var listed = await client.GetAsync("/consultation-requests");
        listed.EnsureSuccessStatusCode();
        using var contacted = await client.PostAsync(
            $"/consultation-requests/{enquiry.PublicId}/contacted", null);
        contacted.EnsureSuccessStatusCode();
        using var converted = await client.PostAsJsonAsync(
            $"/consultation-requests/{enquiry.PublicId}/convert", NewConversion());
        converted.EnsureSuccessStatusCode();

        await using var db = DbFor(null);
        var written = string.Join(
            "\n",
            await db.AuditEvents.AsNoTracking()
                .Select(e => $"{e.Metadata}|{e.EntityType}|{e.IpAddress}|{e.UserAgent}")
                .ToListAsync());

        foreach (var marker in new[] { ParentMarker, ChildMarker, ConcernMarker, "Undermarch" })
        {
            Assert.DoesNotContain(marker, written, StringComparison.Ordinal);
        }
    }
}
