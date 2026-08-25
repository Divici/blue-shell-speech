using Microsoft.EntityFrameworkCore;
using Practice.Application.Consultations;
using Practice.Domain.Auditing;
using Practice.Domain.Consultations;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Consultations;

/// <summary>
/// The public intake form's only endpoint.
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

        group.MapPost("/", SubmitConsultationRequest);

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
         */
        try
        {
            await notifier.NotifyAsync(publicId);
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
