using Microsoft.EntityFrameworkCore;
using Practice.Application.Consultations;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Consultations;
using Practice.Domain.Patients;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Consultations;

/// <summary>
/// Public intake, and the inbox it arrives in.
///
/// TWO AUDIENCES ON ONE ROUTE PREFIX, AND THE SPLIT IS THE POINT. <c>POST /</c> is reached
/// by a parent with no account; everything else here is reached by Michelle through a
/// session, is scoped by the global query filter, and answers 404 for an enquiry belonging
/// to another provider exactly as it does for one that does not exist (D052).
///
/// AN ENQUIRY IS READ LIKE PATIENT DATA EVEN THOUGH IT IS NOT PHI. A child's first name
/// beside a parent's description of that child's difficulties is the same category of
/// information whatever the regulation calls it, and it becomes PHI the moment Michelle
/// acts on it — so the reads below are audited, on the endpoints the product actually
/// calls rather than on one that merely looks like the read endpoint (D065).
///
/// THIS IS THE ONE ROUTE IN THE API THAT DOES NOT REQUIRE A PROVIDER. Every other endpoint
/// opens with <c>if (provider.ProviderId is null) return Results.Unauthorized()</c>; this
/// one cannot, because the caller is a parent who has never heard of this system. That
/// makes it the only place where an anonymous stranger's bytes reach a database write, and
/// three things follow from it:
///
///   1. THE PROVIDER IS RESOLVED HERE, NEVER SUPPLIED. The X-Provider-Id header is ignored
///      on this route — not merely unused, ignored — because honouring it would let an
///      anonymous caller choose whose records to write into. See ResolveSoleProviderAsync.
///
///   2. EVERY FIELD IS VALIDATED AND BOUNDED SERVER-SIDE. The BFF validates too, and that
///      is a convenience for the parent rather than a control: nothing here may be skipped
///      because a browser already checked (docs/SECURITY.md).
///
///   3. THE ADDRESS IS NEVER STORED, ONLY ITS HASH. The BFF computes it — it is the tier
///      that can see a client address at all — and hands it over already digested, using
///      the same value it keys its rate limiter on (`web/lib/rate-limit.ts`). One
///      derivation, two uses; a second hashing scheme would correlate with neither.
/// </summary>
public static class ConsultationEndpoints
{
    public static IEndpointRouteBuilder MapConsultationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/consultation-requests").WithTags("Consultations");

        // Anonymous. Everything below it takes a provider from the forwarded session.
        group.MapPost("/", SubmitConsultationRequest);

        group.MapGet("/", ListConsultationRequests);
        group.MapGet("/{publicId:guid}", GetConsultationRequest);
        group.MapPost("/{publicId:guid}/contacted", MarkContacted);
        group.MapPost("/{publicId:guid}/declined", DeclineConsultationRequest);
        group.MapPost("/{publicId:guid}/convert", ConvertConsultationRequest);

        return app;
    }

    /*
     * WHAT A PARENT IS TOLD WHEN THE PRACTICE CANNOT ACCEPT THE ENQUIRY.
     *
     * 503, not 500 and not 200. The BFF turns this into "we could not record that, please
     * call" and keeps everything the parent typed. Answering 200 would be the worse
     * failure by a distance: a family told "thank you, we will be in touch" whose enquiry
     * was never stored does not follow up, and nobody ever finds out.
     */
    private const string NoProviderToReceiveIt =
        "The practice cannot accept requests at the moment.";

    private static async Task<IResult> SubmitConsultationRequest(
        SubmitConsultationRequest request,
        PracticeDbContext db,
        IAuditWriter audit,
        IConsultationNotifier notifier,
        UncancellableWriteDeadline deadline,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!Enum.TryParse<PreferredContactMethod>(
                request.PreferredContactMethod, ignoreCase: true, out var preferredContact))
        {
            /*
             * Parsed from a STRING, by NAME.
             *
             * A `PreferredContactMethod` typed directly on the request record would be
             * bound by System.Text.Json from a raw integer, and an integer cast into an
             * enum is not checked by the runtime — so `{"preferredContactMethod": 99}`
             * would arrive as a legal value of that type and persist as 99.
             *
             * This branch is NOT the whole of that guard, and saying so matters: TryParse
             * accepts a NUMERIC string too, so "99" gets past here as (PreferredContactMethod)99.
             * What refuses it is Enum.IsDefined inside ConsultationRequest.Submit, where
             * the rule belongs — the aggregate is reachable from callers this endpoint
             * knows nothing about. This branch exists so an unknown NAME is answered with
             * a sentence naming the field, rather than an aggregate message written for a
             * developer.
             */
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["preferredContactMethod"] =
                    ["Choose Email, Phone, or Either."],
            });
        }

        /*
         * A cheap first pass, so an enquiry that has nowhere to go costs one indexed query
         * rather than a validation pass and a transaction.
         *
         * NOT the answer. The answer is taken inside the write below, for the reason on
         * AtomicWrites: the body runs more than once, and "there is exactly one clinician
         * who could receive this" is a conclusion about the Providers table rather than a
         * value. Held out here and used inside, it would be a statement about a database
         * that may have moved on — the same shape as the discard that validated one note
         * and deleted another.
         */
        var providerId = await ResolveSoleProviderAsync(db, ct);

        if (providerId is null)
        {
            return Results.Problem(
                detail: NoProviderToReceiveIt,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var submittedAtUtc = clock.GetUtcNow().UtcDateTime;

        ConsultationRequest Build(long recipientId) => ConsultationRequest.Submit(
            recipientId,
            request.ParentName,
            request.Email,
            request.Phone,
            request.ChildFirstName,
            request.ChildAgeMonths,
            request.Concerns,
            preferredContact,
            request.SourceIpHash,
            submittedAtUtc);

        try
        {
            /*
             * Built once here purely to VALIDATE, and thrown away.
             *
             * A bad submission must answer 400 without opening a transaction — otherwise
             * the cheapest way to hold a connection open against a scale-to-zero container
             * is to post rubbish at it. The instance this produces is deliberately not
             * kept: the one that gets saved is constructed inside the write below, because
             * the helper's contract says an entity may not cross that boundary.
             *
             * The recipient it is built against is the first pass's answer, which is fine
             * here — nothing about the aggregate's bounds depends on WHOSE row it is.
             */
            _ = Build(providerId.Value);
        }
        catch (ArgumentException ex)
        {
            /*
             * The aggregate's own bounds, surfaced as 400 with the field that failed.
             *
             * These messages are written for a developer reading a BFF log, never for a
             * parent — the sentences the parent sees come from the form's own validation —
             * and none of them echoes a submitted value back.
             */
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [ex.ParamName ?? "request"] = [ex.Message],
            });
        }

        /*
         * THE ROW AND ITS AUDIT ENTRY COMMIT TOGETHER, OR NEITHER DOES.
         *
         * The audit row is the only record that an anonymous write was attempted at all —
         * there is no session, no actor, and nobody to ask afterwards — so a design where
         * the enquiry lands and the audit save fails would lose exactly the evidence a
         * submission flood consists of.
         *
         * WriteAtomicallyAsync rather than two saves, for the reasons on the helper: the
         * transaction lives inside the retrying execution strategy, the change tracker is
         * cleared on every attempt, the retry loop gets the caller's token, and the commit
         * runs on CancellationToken.None (D075). Its contract on this block is that the
         * block RUNS MORE THAN ONCE — so the entity is constructed here rather than
         * carried across attempts, the audit event is built inside, and the question of
         * who receives the enquiry is asked inside.
         */
        var publicId = Guid.Empty;
        var nobodyToReceiveIt = false;

        await db.WriteAtomicallyAsync(async attempt =>
        {
            // Reset per attempt: a conclusion from a previous attempt is exactly what the
            // helper's contract says may not survive into this one.
            nobodyToReceiveIt = false;

            /*
             * RE-RESOLVED, against the Providers table as it stands now.
             *
             * The first pass happened before the transaction, and a second clinician
             * activated in between makes "the sole active provider" a question nobody has
             * answered — which D078 refuses rather than guesses. Held out from the first
             * pass, the enquiry would commit against whoever was sole a moment earlier and
             * land in front of nobody.
             */
            var recipientId = await ResolveSoleProviderAsync(db, attempt);

            if (recipientId is null)
            {
                nobodyToReceiveIt = true;
                return;
            }

            /*
             * A FRESH ENTITY PER ATTEMPT, and the id read back OUT rather than fixed in.
             *
             * Re-adding an instance a previous attempt already saved is the documented
             * hazard: the failed save leaves it carrying a store-generated key, and the
             * next Add asks SQL Server for an explicit identity insert. Submit() is pure
             * and cheap, so building a new one is the honest way to satisfy the contract.
             *
             * The PublicId therefore differs between a discarded attempt and the one that
             * commits, which is why it is assigned OUT of the lambda instead of in: the
             * caller must be told the id that is actually in the table, and nothing else
             * ever saw the abandoned one.
             */
            var attempted = Build(recipientId.Value);

            db.ConsultationRequests.Add(attempted);
            await db.SaveChangesAsync(attempt);

            publicId = attempted.PublicId;

            /*
             * Metadata is a fixed vocabulary and opaque values only.
             *
             * The source hash is here as well as on the row because the two answer
             * different questions at different times: the row answers "who should Michelle
             * ring", and it is the row a retention policy eventually deletes; the audit
             * entry answers "did four hundred of these arrive from one place last
             * Tuesday", and it is never purged (docs/SECURITY.md §Data retention). A hash
             * is not a visitor identifier — that is the whole reason the raw address is
             * not stored anywhere in this application.
             *
             * ipAddress is deliberately LEFT NULL on this row. AuditEvent has a column for
             * it and other endpoints fill it, but doing so here would put the raw address
             * in the table next door and quietly undo the decision the hash exists to
             * implement.
             */
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.ConsultationRequestReceived, AuditOutcome.Success,
                providerId: recipientId,
                entityType: nameof(ConsultationRequest),
                entityPublicId: attempted.PublicId,
                metadata:
                    $"source=public-form;sourceIpHash={attempted.SourceIpHash ?? "none"}"));
        }, ct);

        // The same 503 the first pass would have given, decided a moment later. Nothing was
        // written, so nothing is announced and the parent is told to ring.
        if (nobodyToReceiveIt)
        {
            return Results.Problem(
                detail: NoProviderToReceiveIt,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        /*
         * NOTIFIED AFTER THE COMMIT, AND NOT INSIDE IT.
         *
         * Inside the transaction body it would be sent again on every retry — the body is
         * re-run, and an email is not something a rollback takes back. After the commit it
         * is sent only for an enquiry that actually exists, which is the direction that
         * matters: a duplicate notification is noise, and a notification for a row that
         * rolled back sends Michelle to look for something that is not there.
         *
         * A failure here does NOT fail the request. The enquiry is committed and the
         * parent's confirmation is truthful; telling them it did not work because a
         * mailbox was unreachable would be a lie in the other direction, and they would
         * submit again. The cost is real and stated: a notification lost this way is not
         * retried, and Michelle finds the enquiry when she next signs in.
         *
         * AND IT IS BOUNDED, WHICH IT WAS NOT. IConsultationNotifier takes no
         * CancellationToken for the same reason IAuditWriter takes none — with one present
         * CA2016 makes every call site forward the request's, and the analyser would
         * enforce the defect (D075, D079) — but unlike an audit write it was on NO bound at
         * all. That was invisible while the implementation wrote a log line and would have
         * stopped being invisible the day the real mail transport landed: a network call to
         * somebody else's infrastructure, on no token, after the request bound has already
         * fired, silently moving DatabaseTimeouts.Ceiling — the number the BFF's
         * API_TIMEOUT_MS is sized against.
         *
         * WaitAsync rather than a token handed to the notifier, because the seam has
         * nowhere to put one, and because what needs bounding is when THIS TIER ANSWERS. A
         * transport abandoned here keeps running in the background and is not cancelled;
         * that is the honest limit of this bound and it is the right one for a
         * notification, where the enquiry is already committed and the send is best effort.
         * It would be exactly the wrong bound on a commit, which is why AtomicWrites does
         * not have one.
         */
        try
        {
            await notifier.NotifyAsync(publicId).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            /*
             * ABANDONED AT THE CEILING, AND THE PARENT IS STILL TOLD THE TRUTH.
             *
             * Falling through to the 201 is the whole point. The row is committed by now,
             * and web/lib/api/consultations.ts reads !response.ok as {stored: false} — so
             * letting this escape would tell a family their enquiry was not recorded when
             * it was, which is the defect D086 and D090 exist to prevent, reached through a
             * different door.
             *
             * NO AUDIT ROW HERE, deliberately: an audit write needs the same grace this
             * notification has just exhausted, so it would throw on an already-cancelled
             * token and the request would 500 on the way to recording that something did
             * not happen. Named as a gap rather than papered over — a notification lost
             * this way leaves nothing behind, and WORK_QUEUE 4.6 owns the alerting that
             * would notice a notification path which has stopped working.
             */
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*
             * A SEPARATE EVENT TYPE, not a Failure outcome on the arrival.
             *
             * The enquiry did arrive — the transaction above committed — so a
             * ConsultationRequestReceived row with Outcome.Failure would say the opposite
             * of what happened, and a year of rows gets counted by somebody who was not
             * here. This is its own question: has the notification path ever worked. A
             * silently failing notifier looks exactly like a working one, which is the
             * same defect WORK_QUEUE 4.6 exists to catch on audio deletion.
             *
             * The exception itself is deliberately not recorded. Metadata never carries
             * anything free-form (docs/SECURITY.md) — a mail provider's error text can
             * echo a recipient address or a subject line back, and this is the one table
             * guaranteed never to be purged.
             */
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.ConsultationNotificationFailed, AuditOutcome.Failure,
                providerId: providerId,
                entityType: nameof(ConsultationRequest), entityPublicId: publicId,
                metadata: "source=public-form"));
        }

        return Results.Created(
            $"/consultation-requests/{publicId}", new SubmittedConsultationRequest(publicId));
    }


    // ------------------------------------------------------------------ the inbox

    /*
     * WHAT MICHELLE READS WHEN A TRANSITION IS REFUSED.
     *
     * Both sentences describe what the row IS rather than which method threw, and neither
     * recommends an action the API would then refuse (D076). A closed enquiry is not a
     * malfunction — it is a rule the reader needs to understand — so it crosses the BFF as
     * a 409 with its wording intact, the same treatment a goal that is already closed gets.
     */
    private const string AlreadyAPatient =
        "This enquiry has already become a patient. Open the patient record instead.";

    private const string AlreadyDeclined =
        "This enquiry was declined and is kept as it was. Nothing further can be recorded "
        + "against it.";

    /// <summary>
    /// The moves the inbox offers. Named so the refusal below can mirror the aggregate's
    /// own rules rather than approximating them with one stricter set.
    /// </summary>
    private enum InboxTransition
    {
        Contacted,
        Converted,
        Declined,
    }

    /// <summary>
    /// The triage list: everything still open first, and within a status the newest first.
    ///
    /// THE ORDER IS A PRODUCT DECISION. Michelle opens this between houses to find the
    /// families nobody has replied to; sorted by arrival alone, a new enquiry sits under a
    /// year of declined ones. ConsultationStatus is numbered New, Contacted, Converted,
    /// Declined for exactly this reason, and its values are fixed.
    ///
    /// THE SUMMARY DOES NOT CARRY WHAT THE PARENT WROTE. Triage needs a name, an age, and
    /// how long they have been waiting; the description of a child's difficulties belongs
    /// to the detail read, which is where the disclosure is audited as one. A list
    /// carrying it would be a second, larger disclosure of the same content — which is
    /// precisely the shape D065 found on note history.
    /// </summary>
    private static async Task<IResult> ListConsultationRequests(
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct,
        string? status = null)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var query = db.ConsultationRequests.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            /*
             * A filter nobody defined is refused, never silently ignored.
             *
             * Ignoring it renders "nothing to answer" for a practice with a full inbox and
             * looks like it worked. Enum.IsDefined as well as TryParse, because TryParse
             * accepts a NUMERIC string — "99" would otherwise arrive as a legal value of
             * this type and match no row, which is the same wrong screen by a different
             * route (the guard ConsultationRequest.Submit makes on the way in).
             */
            if (!Enum.TryParse<ConsultationStatus>(status, ignoreCase: true, out var wanted)
                || !Enum.IsDefined(wanted))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Choose New, Contacted, Converted, or Declined."],
                });
            }

            query = query.Where(c => c.Status == wanted);
        }

        /*
         * Columns in SQL, enum names in memory.
         *
         * Translating `Status.ToString()` into the query is what turned "no cue level was
         * recorded" into an empty string on the goals projection. The two-step keeps the
         * column list narrow and lets the naming happen where it behaves.
         */
        var rows = await query
            .OrderBy(c => c.Status).ThenByDescending(c => c.SubmittedAtUtc)
            .Select(c => new
            {
                c.PublicId,
                c.ParentName,
                c.ChildFirstName,
                c.ChildAgeMonths,
                c.PreferredContactMethod,
                c.Status,
                c.SubmittedAtUtc,
                /*
                 * The converted patient as a GUID, resolved in the query.
                 *
                 * The row stores the clustered key — a real foreign key is the only thing
                 * that keeps this link honest — and a clustered key never crosses the
                 * wire. The subquery is filtered like every other read of Patients, so a
                 * link that somehow pointed outside this provider's caseload resolves to
                 * null rather than confirming that the row exists.
                 */
                ConvertedPatientPublicId = db.Patients
                    .Where(p => p.Id == c.ConvertedPatientId)
                    .Select(p => (Guid?)p.PublicId)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var results = rows
            .Select(c => new ConsultationRequestSummary(
                c.PublicId, c.ParentName, c.ChildFirstName, c.ChildAgeMonths,
                c.PreferredContactMethod.ToString(), c.Status.ToString(),
                c.SubmittedAtUtc, c.ConvertedPatientPublicId))
            .ToList();

        /*
         * THE LIST IS A DISCLOSURE, AND THE ROW SAYS HOW BIG A ONE.
         *
         * Every entry carries a parent's name and a child's first name. "Somebody opened
         * the inbox" cannot tell one enquiry from forty apart afterwards, and afterwards
         * is the only time this table is read — the same argument D065 makes for
         * `versions=n` on a note history. A count is not content; nothing the parent typed
         * touches AuditEvents.
         *
         * EntityPublicId is null because a list has no single subject, which is also what
         * distinguishes these rows from the detail reads below.
         */
        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.ConsultationRequestViewed, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ConsultationRequest),
            metadata: $"scope=list;count={results.Count};status={status ?? "all"}"));

        return Results.Ok(results);
    }

    /// <summary>
    /// One enquiry, in full — and the only endpoint that returns what the parent wrote.
    ///
    /// THIS IS THE ENDPOINT THE DETAIL VIEW CALLS, and therefore the one that has to carry
    /// the audit row. D065 is the finding that a sibling endpoint auditing correctly is a
    /// control on paper once the product stops calling it; keeping the disclosure and its
    /// record in the same handler is what makes that unable to drift apart again.
    ///
    /// The row is written AFTER the read resolved and only when something was disclosed. A
    /// row on a 404 would say a stranger saw an enquiry they were refused, and would put
    /// somebody who read nothing into a count of who read this family's words.
    /// </summary>
    private static async Task<IResult> GetConsultationRequest(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var enquiry = await db.ConsultationRequests.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PublicId == publicId, ct);

        if (enquiry is null) return Results.NotFound();

        var convertedPatient = await ConvertedPatientPublicIdAsync(db, enquiry, ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.ConsultationRequestViewed, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ConsultationRequest), entityPublicId: publicId,
            metadata: "scope=detail"));

        return Results.Ok(new ConsultationRequestDetail(
            enquiry.PublicId,
            enquiry.ParentName,
            enquiry.Email,
            enquiry.Phone,
            enquiry.ChildFirstName,
            enquiry.ChildAgeMonths,
            enquiry.Concerns,
            enquiry.PreferredContactMethod.ToString(),
            enquiry.Status.ToString(),
            enquiry.SubmittedAtUtc,
            convertedPatient));
    }

    /// <summary>Michelle has replied. Idempotent, as the aggregate is.</summary>
    private static Task<IResult> MarkContacted(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct) =>
        ApplyTransitionAsync(
            publicId, db, provider, audit,
            InboxTransition.Contacted, enquiry => enquiry.MarkContacted(), ct);

    /// <summary>
    /// Not going ahead — the family moved, or the practice is not the right fit.
    ///
    /// A transition, never a delete. The enquiry stays exactly as the parent wrote it:
    /// "who did we turn away, and when" is a question about the practice, and a deleted
    /// row answers it with silence.
    /// </summary>
    private static Task<IResult> DeclineConsultationRequest(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct) =>
        ApplyTransitionAsync(
            publicId, db, provider, audit,
            InboxTransition.Declined, enquiry => enquiry.Decline(), ct);

    /// <summary>
    /// The enquiry became a patient.
    ///
    /// THE CHILD'S FIRST NAME COMES OFF THE ENQUIRY; THE SURNAME AND DATE OF BIRTH ARE
    /// ASKED FOR. The public form collects a first name and an age in months and nothing
    /// else about the child, deliberately — so a conversion cannot be a pure copy, and
    /// deriving a date of birth from an age in months would put a value in the field every
    /// early-intervention decision hangs on that nobody actually stated.
    ///
    /// ONE TRANSACTION, because the halves are worthless apart. A patient created with the
    /// enquiry still saying New is the state that produces a SECOND record for the same
    /// child on the next tap — on a caseload where the duplicate silently collects half
    /// the sessions and neither record is the whole story.
    /// </summary>
    private static async Task<IResult> ConvertConsultationRequest(
        Guid publicId,
        ConvertConsultationRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        /*
         * The cheap first pass, and NOT the answer — the same shape as the intake write
         * above. An ordinary refusal costs one indexed read rather than a transaction, and
         * everything it concludes is asked again inside against the row being written.
         */
        var existing = await db.ConsultationRequests.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PublicId == publicId, ct);

        if (existing is null) return Results.NotFound();

        var early = RefusalToTransition(existing, InboxTransition.Converted);
        if (early is not null) return Results.Conflict(new { message = early });

        // Matches CreatePatient exactly. Patient.Create uses it only as the upper bound on
        // a plausible birthdate, and two paths creating a patient must not disagree about
        // what "today" is.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var gone = false;
        string? refusal = null;
        Dictionary<string, string[]>? invalid = null;
        ConsultationRequestSummary? converted = null;

        /*
         * A LOCAL FUNCTION rather than an inline lambda, for the reason DiscardDraft gives
         * for the same shape: the call can be wrapped — or unwrapped — without
         * re-indenting the body, which is what makes "remove the transaction and see
         * whether the patient survives" a change a reviewer can actually run.
         */
        async Task ConvertTheEnquiry(CancellationToken attempt)
        {
            // Reset per attempt: every one of these is a conclusion, and the helper's
            // contract says a conclusion from a previous attempt may not survive into this
            // one.
            gone = false;
            refusal = null;
            invalid = null;
            converted = null;

            /*
             * RE-READ. The entity above is detached by the time this runs — the change
             * tracker is cleared at the top of every attempt.
             */
            var enquiry = await db.ConsultationRequests
                .SingleOrDefaultAsync(c => c.PublicId == publicId, attempt);

            if (enquiry is null)
            {
                gone = true;
                return;
            }

            /*
             * ASKED AGAIN, OF THE ROW BEING WRITTEN. This is the guard; the pass above is
             * the optimisation.
             *
             * Michelle taps Convert on her phone while the tap she made a moment earlier
             * on the tablet is still in flight. Decided outside and applied inside, both
             * requests pass their own check and the child ends up with two records — the
             * D081 shape, with a duplicated clinical record at the end of it rather than a
             * deleted one.
             */
            refusal = RefusalToTransition(enquiry, InboxTransition.Converted);
            if (refusal is not null) return;

            /*
             * CONSTRUCTED HERE, on each attempt, and VALIDATED here as well.
             *
             * Fresh per attempt because an entity a previous attempt saved carries a
             * store-generated key, and re-adding it asks SQL Server for an explicit
             * identity insert rather than a new row (D075).
             *
             * Validated here because the first name feeding it comes off the RE-READ row
             * rather than the one checked outside. Nothing in this API edits that column,
             * so the two agree today — but "today" is not a control, and answering 500
             * where the early pass answers 400 is exactly the failure this repetition
             * exists to avoid.
             */
            Patient patient;
            try
            {
                patient = Patient.Create(
                    provider.ProviderId.Value,
                    enquiry.ChildFirstName,
                    request.LastName,
                    request.DateOfBirth,
                    today);
            }
            catch (ArgumentException ex)
            {
                invalid = new Dictionary<string, string[]>
                {
                    [ex.ParamName ?? "request"] = [ex.Message],
                };
                return;
            }

            db.Patients.Add(patient);

            // Saved before ConvertTo, because the link is the identity the database
            // assigns and there is no earlier moment at which it exists.
            await db.SaveChangesAsync(attempt);

            enquiry.ConvertTo(patient.Id);
            await db.SaveChangesAsync(attempt);

            converted = SummaryOf(enquiry, patient.PublicId);

            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.ConsultationRequestUpdated, AuditOutcome.Success,
                providerId: provider.ProviderId,
                entityType: nameof(ConsultationRequest), entityPublicId: publicId,
                // Opaque ids and a fixed word. The child's name is on the row this points
                // at, and AuditEvents is the table that is never purged.
                metadata: $"action=converted;patient={patient.PublicId}"));
        }

        await db.WriteAtomicallyAsync(ConvertTheEnquiry, ct);

        if (gone) return Results.NotFound();
        if (invalid is not null) return Results.ValidationProblem(invalid);
        if (refusal is not null) return Results.Conflict(new { message = refusal });

        return Results.Ok(converted);
    }

    /// <summary>
    /// The shared shape of the two status moves: re-read, re-check, apply, audit, commit.
    ///
    /// ONE BODY RATHER THAN TWO COPIES, for the reason NoteEndpoints.RefusalToDiscard
    /// gives: two copies are two things to keep in step, and the copy that drifts is the
    /// one standing between a request and a row it should not have changed.
    ///
    /// The transition and its audit row are one transaction. They are a single row's
    /// status and the only record of who moved it — a save that lands without the other is
    /// either an unexplained state change or a claim about one that never happened.
    /// </summary>
    private static async Task<IResult> ApplyTransitionAsync(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        InboxTransition transition,
        Action<ConsultationRequest> apply,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        // The cheap first pass. Not the answer — see the re-check inside.
        var existing = await db.ConsultationRequests.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PublicId == publicId, ct);

        if (existing is null) return Results.NotFound();

        var early = RefusalToTransition(existing, transition);
        if (early is not null) return Results.Conflict(new { message = early });

        var gone = false;
        string? refusal = null;
        ConsultationRequestSummary? moved = null;

        // A local function for the same reason as ConvertTheEnquiry above.
        async Task MoveTheEnquiry(CancellationToken attempt)
        {
            gone = false;
            refusal = null;
            moved = null;

            var enquiry = await db.ConsultationRequests
                .SingleOrDefaultAsync(c => c.PublicId == publicId, attempt);

            if (enquiry is null)
            {
                gone = true;
                return;
            }

            // The guard, against the row actually being changed (D081).
            refusal = RefusalToTransition(enquiry, transition);
            if (refusal is not null) return;

            apply(enquiry);
            await db.SaveChangesAsync(attempt);

            /*
             * Resolved rather than assumed absent.
             *
             * Neither move that reaches here can leave a converted enquiry —
             * RefusalToTransition refuses both of them on a converted row — so this is
             * null on every path today. It is a query rather than a literal because that
             * is a fact about the CURRENT refusal rules, and a summary that silently
             * dropped the link the day those rules change is not the sort of thing anybody
             * notices.
             */
            var convertedPatient = await ConvertedPatientPublicIdAsync(db, enquiry, attempt);

            moved = SummaryOf(enquiry, convertedPatient);

            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.ConsultationRequestUpdated, AuditOutcome.Success,
                providerId: provider.ProviderId,
                entityType: nameof(ConsultationRequest), entityPublicId: publicId,
                metadata: $"action={transition.ToString().ToLowerInvariant()}"));
        }

        await db.WriteAtomicallyAsync(MoveTheEnquiry, ct);

        if (gone) return Results.NotFound();
        if (refusal is not null) return Results.Conflict(new { message = refusal });

        return Results.Ok(moved);
    }

    /// <summary>
    /// Why this enquiry cannot make this move — the sentence Michelle reads — or null when
    /// it can.
    ///
    /// IT MIRRORS THE AGGREGATE RATHER THAN APPROXIMATING IT, and the target is a parameter
    /// for exactly that reason. ConsultationRequest.Decline refuses only a CONVERTED
    /// enquiry; MarkContacted and ConvertTo refuse both closed states. One stricter rule
    /// covering all three would refuse a second tap on Decline that the aggregate allows —
    /// a rule the endpoint invented and nothing else in the system holds, which is the
    /// mirror image of D076's defect, where the endpoint recommended a door the aggregate
    /// had locked.
    ///
    /// Written here as well as in the aggregate on the D064 principle: the rule lives in
    /// more than one place so that loosening one does not remove it.
    /// </summary>
    private static string? RefusalToTransition(
        ConsultationRequest enquiry, InboxTransition target) => enquiry.Status switch
        {
            // A child on the caseload. Every move would contradict a clinical record that
            // already exists, declining included.
            ConsultationStatus.Converted => AlreadyAPatient,

            // Declining twice is not a different state. Reopening a declined enquiry is.
            ConsultationStatus.Declined when target is not InboxTransition.Declined =>
                AlreadyDeclined,

            _ => null,
        };

    /// <summary>
    /// The patient an enquiry became, as a public id — or null if it became none.
    ///
    /// Filtered like every other read of Patients, so a link pointing outside this
    /// provider's caseload resolves to null rather than confirming the row exists.
    /// </summary>
    private static async Task<Guid?> ConvertedPatientPublicIdAsync(
        PracticeDbContext db, ConsultationRequest enquiry, CancellationToken ct)
    {
        if (enquiry.ConvertedPatientId is null) return null;

        return await db.Patients.AsNoTracking()
            .Where(p => p.Id == enquiry.ConvertedPatientId)
            .Select(p => (Guid?)p.PublicId)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// The triage view of a row. Carries no Concerns, by construction — see
    /// <see cref="ConsultationRequestSummary"/>.
    /// </summary>
    private static ConsultationRequestSummary SummaryOf(
        ConsultationRequest enquiry, Guid? convertedPatientPublicId) =>
        new(
            enquiry.PublicId,
            enquiry.ParentName,
            enquiry.ChildFirstName,
            enquiry.ChildAgeMonths,
            enquiry.PreferredContactMethod.ToString(),
            enquiry.Status.ToString(),
            enquiry.SubmittedAtUtc,
            convertedPatientPublicId);

    /// <summary>
    /// Answers "whose enquiry is this" for a submission that arrived with no session.
    ///
    /// CLAUDE.md requires a ProviderId on every domain row from day one, and a public form
    /// has none to offer. The practice is one clinician, so the answer is available as
    /// data: THE SOLE ACTIVE PROVIDER. Resolved server-side from the Providers table —
    /// which carries no query filter, because it is not patient data and resolving a
    /// provider is the step that arms the filter for everything else.
    ///
    /// IT REFUSES WHEN THE ANSWER IS AMBIGUOUS, and that is the point rather than a gap.
    /// With two active clinicians, "who receives a parent's enquiry" is a routing decision
    /// a human has to make — intake coordinator, specialty, rota — and `ORDER BY Id` would
    /// answer it silently, land every family on whoever was seeded first, and be found out
    /// months later. Returning nothing produces a 503, a log line, and a parent told to
    /// call, which is a loud failure on the day the second clinician is added rather than
    /// a quiet wrong answer forever. Same shape as resolvePracticeContact throwing in
    /// production rather than shipping a placeholder phone number.
    ///
    /// Inactive providers are excluded for the same reason the middleware excludes them: a
    /// clinician who has stopped practising still owns their historical records and must
    /// not be handed new ones.
    /// </summary>
    private static async Task<long?> ResolveSoleProviderAsync(
        PracticeDbContext db, CancellationToken ct)
    {
        var active = await db.Providers
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Id)
            // Two is enough to know the answer is ambiguous; there is no reason to read
            // the rest of the table to find that out.
            .Take(2)
            .Select(p => p.Id)
            .ToListAsync(ct);

        return active.Count == 1 ? active[0] : null;
    }
}

/// <summary>
/// What the BFF posts. Every field is a primitive the caller cannot smuggle a type through.
/// </summary>
/// <param name="ChildAgeMonths">
/// Bounded by the aggregate. The practice's own "birth to five" rule lives on the form,
/// where a parent outside it gets an explanation instead of a validation error.
/// </param>
/// <param name="PreferredContactMethod">
/// A STRING, matched against the enum by name. See the parse in the handler for why an
/// enum-typed property here would accept `99`.
/// </param>
/// <param name="SourceIpHash">
/// SHA-256 hex of the submitting address, computed by the BFF. Never the address itself —
/// the aggregate refuses anything not shaped like a digest.
/// </param>
public sealed record SubmitConsultationRequest(
    string ParentName,
    string Email,
    string? Phone,
    string ChildFirstName,
    short ChildAgeMonths,
    string Concerns,
    string PreferredContactMethod,
    string? SourceIpHash);

/// <summary>
/// The opaque public id, and nothing else.
///
/// The response deliberately does not echo the submission back. A parent's words go into
/// the database and come out through an authenticated session; there is no reason for them
/// to make a second trip across the network on the way to a confirmation page that shows
/// none of them.
/// </summary>
public sealed record SubmittedConsultationRequest(Guid PublicId);

/// <summary>
/// One row of the inbox — enough to triage, and NOT what the parent wrote.
///
/// THE ABSENCE OF `Concerns` IS THE CONTROL, not a rendering choice. A description of a
/// child's difficulties is the most sensitive thing on this row, and an inbox needs a name,
/// an age and how long the family has been waiting. Keeping it off here means exactly one
/// endpoint discloses it — the one that records the disclosure — where a list carrying it
/// would be a second, larger, unaudited read of the same content one fetch away (D065).
/// </summary>
/// <param name="ConvertedPatientPublicId">
/// The child's record, once this enquiry became one. A GUID: the row stores the clustered
/// key because a real foreign key is the only thing that keeps the link honest, and no
/// clustered key crosses the wire (CLAUDE.md conventions).
/// </param>
public sealed record ConsultationRequestSummary(
    Guid PublicId,
    string ParentName,
    string ChildFirstName,
    short ChildAgeMonths,
    string PreferredContactMethod,
    string Status,
    DateTime SubmittedAtUtc,
    Guid? ConvertedPatientPublicId);

/// <summary>
/// The whole enquiry, including what the parent wrote about their child.
///
/// Returned by one endpoint, which audits every read of it.
/// </summary>
public sealed record ConsultationRequestDetail(
    Guid PublicId,
    string ParentName,
    string Email,
    string? Phone,
    string ChildFirstName,
    short ChildAgeMonths,
    string Concerns,
    string PreferredContactMethod,
    string Status,
    DateTime SubmittedAtUtc,
    Guid? ConvertedPatientPublicId);

/// <summary>
/// What a conversion needs that the enquiry does not already hold.
///
/// The public form asks a first name and an age in months, on purpose — so a surname and a
/// date of birth have to be typed. NOT derived from the age: every early-intervention
/// decision hangs on age in months, and a birthdate computed from a parent's rounded
/// estimate is a value nobody stated sitting in the field that matters most.
/// </summary>
public sealed record ConvertConsultationRequest(string LastName, DateOnly DateOfBirth);
