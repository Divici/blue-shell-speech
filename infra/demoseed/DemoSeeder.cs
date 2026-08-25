using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Common;
using Practice.Domain.Consultations;
using Practice.Domain.Goals;
using Practice.Domain.Patients;
using Practice.Domain.Scheduling;
using Practice.Infrastructure.Persistence;

namespace BlueShell.DemoSeed;

/// <summary>
/// Writes <see cref="DemoRoster"/> into a database, through the domain aggregates.
///
/// NOT raw INSERT, anywhere, for anything. Every row here goes through the same
/// constructor or method the API calls, so every invariant the aggregates hold is held on
/// the way in: the AAC fields only exist on an AAC goal, at most one guardian is the
/// primary contact, a superseded address keeps its dates, an appointment start is UTC or it
/// throws, a note is signed rather than switched to Signed, and an amendment is a new row
/// that supersedes an untouched one. Seeding around them would produce a database that
/// looks like the product's and is not, and the first thing it would hide is a broken
/// invariant.
///
/// IDEMPOTENT. Every phase looks for what it is about to write and skips what is already
/// there, so a second run creates nothing and throws nothing. The lookups run through the
/// context's provider query filter, so "already there" means "already there for THIS
/// provider" — which is what the tenancy rule means everywhere else in this system.
///
/// IT OWNS ITS OWN CONTEXT, armed for the provider it was handed. Taking one from a caller
/// would make the two able to disagree, and a seeder whose lookups run under a different
/// provider than its writes answers "no, that is not here yet" about rows that are — so a
/// second run duplicates the entire roster into a caseload it was never pointed at. Built
/// this way the mismatch is not guarded against, it is unrepresentable.
///
/// IT WRITES NO <c>AuditEvent</c> ROWS, deliberately. An audit row asserts that a person
/// did a thing at a time, and the audit log is the one table most likely to be exported,
/// shipped to a SIEM, or read by a third party during an investigation
/// (docs/DATA_MODEL.md). Demo history is invented, and inventing an audit trail to go with
/// it is the one kind of synthetic data that would actively mislead whoever reads it. The
/// consequence is stated rather than hidden: a seeded database has signed notes with no
/// <c>NoteSigned</c> row behind them, and that asymmetry is the correct one.
/// </summary>
public sealed class DemoSeeder : IAsyncDisposable
{
    private readonly PracticeDbContext _db;
    private readonly long _providerId;
    private readonly string _signedBy;

    /// <param name="options">
    /// How to reach the database. The seeder builds its own <see cref="PracticeDbContext"/>
    /// from this rather than accepting one, so the query filter and the provider it writes
    /// are the same provider by construction.
    /// </param>
    /// <param name="providerId">Whose caseload this is. Resolved by
    /// <see cref="ResolveSoleActiveProviderAsync"/>, never chosen.</param>
    /// <param name="signedBy">
    /// The name a signed note is attributed to — the provider's own <c>DisplayName</c>, the
    /// same value <c>NoteEndpoints.SignNote</c> uses.
    /// </param>
    public DemoSeeder(
        DbContextOptions<PracticeDbContext> options, long providerId, string? signedBy)
    {
        if (providerId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerId), "A demo seed belongs to a provider.");
        }

        _db = new PracticeDbContext(options, new FixedProviderContext(providerId));
        _providerId = providerId;
        _signedBy = string.IsNullOrWhiteSpace(signedBy)
            ? DemoRoster.FallbackSigner
            : signedBy.Trim();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    /// <summary>
    /// Which provider the seed belongs to, or why the tool must not guess.
    ///
    /// The same rule the public consultation form runs (D078): resolve the SOLE ACTIVE
    /// provider, and refuse when the answer is ambiguous rather than taking the lowest id.
    /// A tool that picked one would write a caseload of invented children into whichever
    /// clinician's records happened to sort first.
    /// </summary>
    public static async Task<ProviderResolution> ResolveSoleActiveProviderAsync(
        DbContextOptions<PracticeDbContext> options, CancellationToken ct)
    {
        /*
         * A NULL provider context, because there is nothing to arm it with yet.
         *
         * Providers carries no query filter — it is not patient data, and the filter is
         * armed by resolving a provider in the first place. Every other table would be
         * empty through this context, which is the safe direction to fail (D051).
         */
        await using var unarmed = new PracticeDbContext(options, new FixedProviderContext(null));

        var active = await unarmed.Providers
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.Id, p.DisplayName })
            .ToListAsync(ct);

        return active.Count switch
        {
            1 => new ProviderResolution(active[0].Id, active[0].DisplayName, null),

            0 => new ProviderResolution(null, null,
                "This database has no active provider. Start the API once with "
                + "Seed:ProviderEmail and Seed:ProviderPassword configured, or sign in, "
                + "before seeding demo data."),

            _ => new ProviderResolution(null, null,
                $"This database has {active.Count.ToString(CultureInfo.InvariantCulture)} "
                + "active providers, so whose caseload this demo data belongs to is a "
                + "question a person has to answer. Refusing rather than picking one."),
        };
    }

    /// <summary>
    /// Why this database must not be seeded, or null if it may be.
    ///
    /// THIS IS THE RUN-TIME HALF OF THE PRODUCTION GUARD (DECISIONS.md D099). The
    /// structural half is that nothing in the deployed image references this project at
    /// all; this is the half that catches what the structure cannot — a human with the
    /// source tree, a connection string, and the wrong one in their shell.
    ///
    /// The rule: <b>this tool writes only into a database that holds nothing it did not
    /// write.</b> One patient or one enquiry outside the roster and it refuses, before the
    /// first insert. A database with a real caseload therefore aborts, and it aborts on the
    /// safest available signal — the PRESENCE of records, rather than the absence of a flag
    /// somebody could have forgotten to set.
    ///
    /// COUNTS ONLY in the message. Naming the rows it found would print PHI to a terminal
    /// and a scrollback buffer, which is precisely what the refusal exists to protect.
    /// </summary>
    public async Task<string?> RefusalReasonAsync(CancellationToken ct)
    {
        var roster = DemoRoster.Patients
            .Select(p => $"{p.FirstName}{p.LastName}")
            .ToHashSet(StringComparer.Ordinal);

        // Projected to two columns and compared in memory: the roster is a client-side set,
        // and a hand-built SQL predicate over eight first/last-name pairs buys nothing.
        var strangers = (await _db.Patients
                .AsNoTracking()
                .Select(p => new { p.FirstName, p.LastName })
                .ToListAsync(ct))
            .Count(p => !roster.Contains($"{p.FirstName}{p.LastName}"));

        var rosterEmails = DemoRoster.Enquiries
            .Select(e => e.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var strangerEnquiries = (await _db.ConsultationRequests
                .AsNoTracking()
                .Select(c => c.Email)
                .ToListAsync(ct))
            .Count(email => !rosterEmails.Contains(email));

        if (strangers == 0 && strangerEnquiries == 0) return null;

        return
            $"Refusing to write. This database already holds {Describe(strangers, "patient", "patients")} "
            + $"and {Describe(strangerEnquiries, "consultation enquiry", "consultation enquiries")} "
            + "that this tool did not create. It seeds only a database containing nothing but "
            + "its own demo roster, because it cannot tell an invented record from a real one — "
            + "and getting that wrong writes fictional children into somebody's caseload.";

        static string Describe(int n, string singular, string plural) =>
            $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? singular : plural)}";
    }

    /// <summary>
    /// Applies the roster. Returns what it CREATED — a second run reports zeros.
    /// </summary>
    /// <param name="nowUtc">
    /// The instant the run happens at. Injected rather than read from the clock so a test
    /// can seed a fixed day: the calendar is built relative to the practice-local date of
    /// this instant, and whether a visit is over or still ahead is decided against it.
    /// </param>
    public async Task<DemoSeedReport> SeedAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The seed instant must be UTC.", nameof(nowUtc));
        }

        var today = PracticeTime.LocalDateOf(nowUtc);

        var patients = await SeedPatientsAsync(today, ct);
        var (guardians, addresses) = await SeedGuardiansAndAddressesAsync(today, ct);
        var goals = await SeedGoalsAsync(today, ct);
        var visits = await SeedVisitsAsync(today, nowUtc, ct);
        var notes = await SeedNotesAsync(today, nowUtc, ct);
        var enquiries = await SeedEnquiriesAsync(nowUtc, ct);

        return new DemoSeedReport(patients, guardians, addresses, goals, visits, notes, enquiries);
    }

    // ------------------------------------------------------------------- patients

    private async Task<int> SeedPatientsAsync(DateOnly today, CancellationToken ct)
    {
        var existing = await _db.Patients
            .Select(p => new { p.FirstName, p.LastName })
            .ToListAsync(ct);

        var created = 0;

        foreach (var spec in DemoRoster.Patients)
        {
            if (existing.Any(p =>
                    string.Equals(p.FirstName, spec.FirstName, StringComparison.Ordinal)
                    && string.Equals(p.LastName, spec.LastName, StringComparison.Ordinal)))
            {
                continue;
            }

            var patient = Patient.Create(
                _providerId, spec.FirstName, spec.LastName, spec.DateOfBirth, today,
                spec.ClinicalSummary);

            // Status is a transition on the aggregate, never a column assignment, because
            // discharge stamps a date with it. Writing the column would produce a
            // discharged patient nobody discharged.
            switch (spec.Status)
            {
                case PatientStatus.Inactive: patient.SetInactive(); break;
                case PatientStatus.Discharged: patient.Discharge(); break;
                default: break;
            }

            _db.Patients.Add(patient);
            created++;
        }

        await _db.SaveChangesAsync(ct);
        return created;
    }

    // --------------------------------------------------- guardians and addresses

    /*
     * A separate phase because both hang off Patient.Id, which is a database identity and
     * is therefore 0 until the patient has been saved. Guardian.Create stamps the id it is
     * handed; run in one phase, every guardian in the roster would carry PatientId = 0.
     */
    private async Task<(int Guardians, int Addresses)> SeedGuardiansAndAddressesAsync(
        DateOnly today, CancellationToken ct)
    {
        var patients = await LoadRosterPatientsAsync(ct);
        var guardians = 0;
        var addresses = 0;

        foreach (var spec in DemoRoster.Patients)
        {
            if (!patients.TryGetValue(spec.LastName, out var patient)) continue;

            foreach (var g in spec.Guardians)
            {
                if (patient.Guardians.Any(existing =>
                        string.Equals(existing.FirstName, g.FirstName, StringComparison.Ordinal)
                        && string.Equals(existing.LastName, g.LastName, StringComparison.Ordinal)))
                {
                    continue;
                }

                // Through the ROOT, which is what keeps "at most one primary contact" true:
                // promoting one demotes the others, and that invariant spans the patient.
                patient.AddGuardian(
                    g.FirstName, g.LastName, g.Relationship, g.Phone, g.Email,
                    g.IsPrimaryContact, g.HasLegalAuthority);

                guardians++;
            }

            // Oldest first. AddAddress supersedes the current address of the same type, so
            // out of order the family would end up living at the address they left.
            foreach (var a in spec.Addresses.OrderByDescending(a => a.DaysAgoEffective))
            {
                if (patient.Addresses.Any(existing =>
                        existing.AddressType == a.AddressType
                        && string.Equals(existing.Line1, a.Line1, StringComparison.Ordinal)))
                {
                    continue;
                }

                patient.AddAddress(
                    a.Line1, a.Line2, a.City, a.State, a.PostalCode, a.AddressType, a.Notes,
                    today.AddDays(-a.DaysAgoEffective));

                addresses++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (guardians, addresses);
    }

    // ---------------------------------------------------------------------- goals

    private async Task<int> SeedGoalsAsync(DateOnly today, CancellationToken ct)
    {
        var patients = await LoadRosterPatientsAsync(ct);
        var existing = await _db.Goals
            .Select(g => new { g.PatientId, g.GoalText })
            .ToListAsync(ct);

        var created = 0;

        foreach (var spec in DemoRoster.Patients)
        {
            if (!patients.TryGetValue(spec.LastName, out var patient)) continue;

            foreach (var g in spec.Goals)
            {
                if (existing.Any(e => e.PatientId == patient.Id
                        && string.Equals(e.GoalText, g.GoalText, StringComparison.Ordinal)))
                {
                    continue;
                }

                /*
                 * The AAC arguments are passed for every goal, and are null on all but the
                 * AAC ones. Goal.Create refuses the combination and
                 * CK_Goals_AacFieldsOnlyOnAacGoals refuses it again — so a roster entry that
                 * put a modality on an articulation goal fails here rather than producing a
                 * row that reads as clinically meaningful and is not (D062).
                 */
                var goal = Goal.Create(
                    _providerId, patient.Id, g.GoalText, g.Domain,
                    today.AddDays(-g.StartedDaysAgo),
                    g.TargetCriteria, g.CueLevelExpected, g.AacModality, g.AacDeviceNotes);

                // A closed goal is closed by the transition that closes it, so Status and
                // EndDate can never disagree.
                var closedOn = today.AddDays(-(g.StartedDaysAgo / 3));

                switch (g.Outcome)
                {
                    case DemoGoalOutcome.Met: goal.MarkMet(closedOn); break;
                    case DemoGoalOutcome.Discontinued: goal.Discontinue(closedOn); break;
                    case DemoGoalOutcome.OnHold: goal.PutOnHold(); break;
                    default: break;
                }

                _db.Goals.Add(goal);
                created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return created;
    }

    // --------------------------------------------------------------------- visits

    private async Task<int> SeedVisitsAsync(DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var patients = await LoadRosterPatientsAsync(ct);
        var existing = await _db.Appointments.ToListAsync(ct);
        var added = new List<Appointment>();

        foreach (var spec in DemoRoster.Visits)
        {
            if (!patients.TryGetValue(spec.PatientLastName, out var patient)) continue;

            var startUtc = ToUtc(today.AddDays(spec.DayOffset), spec.LocalStart);

            if (existing.Any(a => a.PatientId == patient.Id && a.StartUtc == startUtc)) continue;

            // The session address of the day, so the card can show where to drive. Null is
            // legitimate — a phone consultation happens nowhere.
            var addressId = patient.Addresses
                .Where(a => a.AddressType == AddressType.Session && a.IsCurrent)
                .Select(a => (long?)a.Id)
                .FirstOrDefault();

            var appointment = Appointment.Schedule(
                _providerId, patient.Id, spec.AppointmentType, startUtc, spec.DurationMinutes,
                addressId, spec.TravelBlockMinutes, spec.Notes);

            switch (spec.Outcome)
            {
                case DemoVisitOutcome.Cancelled:
                    appointment.Cancel(spec.CancellationReason);
                    break;

                case DemoVisitOutcome.NoShow:
                    appointment.MarkNoShow();
                    break;

                // Nothing went wrong, so the visit is complete if it is over and untouched
                // if it has not happened yet. Mileage lands with the completion, which is
                // where the day view's total comes from.
                case DemoVisitOutcome.AsScheduled when appointment.EndUtc <= nowUtc:
                    appointment.Complete(spec.Mileage);
                    break;

                default:
                    break;
            }

            _db.Appointments.Add(appointment);
            added.Add(appointment);
        }

        AssertTheDayIsPossible(added, existing);

        await _db.SaveChangesAsync(ct);
        return added.Count;
    }

    /*
     * The fixture is checked against the rule the API enforces, not against a reading of
     * the timetable.
     *
     * Appointment.ConflictsWith counts the travel block as occupied time, so two visits
     * forty minutes apart on opposite sides of the county DO conflict even though the
     * calendar shows a gap. A seeded day the scheduling endpoint would have refused with a
     * 409 is a demo of a product that does not exist, so this throws rather than shipping
     * an impossible day.
     */
    private static void AssertTheDayIsPossible(
        IReadOnlyList<Appointment> added, IReadOnlyList<Appointment> existing)
    {
        var all = existing.Concat(added).ToList();

        foreach (var candidate in added)
        {
            foreach (var other in all)
            {
                if (ReferenceEquals(candidate, other)) continue;
                if (!candidate.ConflictsWith(other)) continue;

                // Times only. The message can reach a terminal, a scrollback buffer, or a
                // CI log, and a patient name has no business in any of them.
                throw new InvalidOperationException(
                    "The demo calendar conflicts with itself once travel time is counted: "
                    + $"{candidate.StartUtc:O} (+{candidate.TravelBlockMinutes ?? 0}m travel) "
                    + $"overlaps {other.StartUtc:O}. Fix DemoRoster.Visits.");
            }
        }
    }

    // ---------------------------------------------------------------------- notes

    private async Task<int> SeedNotesAsync(DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var patients = await LoadRosterPatientsAsync(ct);
        var appointments = await _db.Appointments.ToListAsync(ct);
        var documented = await _db.ClinicalNotes.Select(n => n.AppointmentId).ToListAsync(ct);

        var created = 0;

        foreach (var spec in DemoRoster.Visits)
        {
            if (spec.Note is null) continue;
            if (!patients.TryGetValue(spec.PatientLastName, out var patient)) continue;

            var startUtc = ToUtc(today.AddDays(spec.DayOffset), spec.LocalStart);
            var visit = appointments.SingleOrDefault(
                a => a.PatientId == patient.Id && a.StartUtc == startUtc);

            if (visit is null) continue;

            // Idempotency and the invariant in one check: the filtered unique index permits
            // exactly one CURRENT note per visit, so "this visit already has any note at
            // all" is the only safe precondition for adding one.
            if (documented.Contains(visit.Id)) continue;

            /*
             * The seeder is held to the documentation gate the product holds Michelle to.
             *
             * A note can only be started on a visit that happened and has begun (D064).
             * Every note-bearing visit in the roster is a day or more in the past, so this
             * never fires — which is exactly why it is an assertion rather than a skip. If
             * it ever fires, the roster has drifted and the demo would otherwise ship a
             * note on a visit the product itself refuses to document.
             */
            var blocked = visit.DocumentationBlockedReason(nowUtc);
            if (blocked is not null)
            {
                throw new InvalidOperationException(
                    $"DemoRoster puts a note on a visit the product would refuse: {blocked}");
            }

            created += SeedOneNote(spec.Note, patient.Id, visit);
            await _db.SaveChangesAsync(ct);

            if (spec.Note.Amendment is not null)
            {
                created += SeedAmendment(spec.Note.Amendment, visit);
                await _db.SaveChangesAsync(ct);
            }
        }

        return created;
    }

    private int SeedOneNote(DemoNote spec, long patientId, Appointment visit)
    {
        var origin = spec.Origin == DemoNoteOrigin.DictationAssisted
            ? NoteOrigin.DictationAssisted
            : NoteOrigin.Manual;

        var note = ClinicalNote.CreateDraft(_providerId, patientId, visit.Id, origin);
        note.UpdateContent(spec.Subjective, spec.Objective, spec.Assessment, spec.Plan);
        _db.ClinicalNotes.Add(note);

        // Signed by SIGNING it. The status, the signature, the timestamp and the SHA-256
        // content hash are one act; a row assembled column by column would carry a hash
        // that does not describe its own content, and VerifyIntegrity would say so.
        if (spec.State == DemoNoteState.Signed)
        {
            note.Sign(_signedBy, visit.EndUtc.AddMinutes(25));
        }

        return 1;
    }

    /*
     * The amendment, and why the demo needs one at all.
     *
     * Amend() flips v1 to Amended with IsCurrent = false and returns a NEW row carrying
     * v1's content, a version number of 2, and a pointer back to it. v1 keeps every byte,
     * its signature and its hash included. Both rows go in ONE SaveChanges, exactly as
     * NoteEndpoints.AmendNote does — split across two, the filtered unique index sees
     * either two current notes for the visit or none.
     */
    private int SeedAmendment(DemoAmendment spec, Appointment visit)
    {
        var v1 = _db.ClinicalNotes.Local.Single(n => n.AppointmentId == visit.Id && n.IsCurrent);

        var v2 = v1.Amend(spec.Reason);
        _db.ClinicalNotes.Add(v2);

        v2.UpdateContent(spec.Subjective, spec.Objective, spec.Assessment, spec.Plan);
        v2.Sign(_signedBy, visit.EndUtc.AddHours(20));

        return 1;
    }

    // ------------------------------------------------------------------ enquiries

    private async Task<int> SeedEnquiriesAsync(DateTime nowUtc, CancellationToken ct)
    {
        var patients = await LoadRosterPatientsAsync(ct);
        var existing = await _db.ConsultationRequests
            .Select(c => new { c.Email, c.ChildFirstName })
            .ToListAsync(ct);

        var created = 0;

        foreach (var spec in DemoRoster.Enquiries)
        {
            if (existing.Any(e =>
                    string.Equals(e.Email, spec.Email, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ChildFirstName, spec.ChildFirstName, StringComparison.Ordinal)))
            {
                continue;
            }

            var request = ConsultationRequest.Submit(
                _providerId, spec.ParentName, spec.Email, spec.Phone, spec.ChildFirstName,
                spec.ChildAgeMonths, spec.Concerns, spec.PreferredContactMethod,
                SyntheticSourceHash(spec.Email), nowUtc.AddDays(-spec.SubmittedDaysAgo));

            switch (spec.Status)
            {
                case ConsultationStatus.Contacted:
                    request.MarkContacted();
                    break;

                case ConsultationStatus.Declined:
                    request.Decline();
                    break;

                case ConsultationStatus.Converted when
                    spec.ConvertToPatientLastName is not null
                    && patients.TryGetValue(spec.ConvertToPatientLastName, out var converted):
                    // The status and the patient id are one call, because a row saying an
                    // enquiry became a patient without saying which one cannot be
                    // reconstructed by any later reader.
                    request.ConvertTo(converted.Id);
                    break;

                default:
                    break;
            }

            _db.ConsultationRequests.Add(request);
            created++;
        }

        await _db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// A digest shaped like the one the BFF computes, over a label rather than an address.
    ///
    /// <c>ConsultationRequest</c> refuses anything that is not 64 lowercase hex characters,
    /// which is what stops a raw address being passed straight into the column whose whole
    /// purpose is that it is not there. NOBODY SUBMITTED THESE ENQUIRIES, so there is no
    /// address to hash — hashing a constant label keeps the column's shape honest while
    /// leaving the value meaningless, and two rows from the same "source" still correlate
    /// the way the column exists to allow.
    /// </summary>
    private static string SyntheticSourceHash(string label) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"blue-shell-demo-seed:{label}")));

    // -------------------------------------------------------------------- helpers

    private async Task<Dictionary<string, Patient>> LoadRosterPatientsAsync(CancellationToken ct)
    {
        var lastNames = DemoRoster.Patients.Select(p => p.LastName).ToList();

        var patients = await _db.Patients
            .Include(p => p.Guardians)
            .Include(p => p.Addresses)
            .Where(p => lastNames.Contains(p.LastName))
            .ToListAsync(ct);

        // Keyed on surname, which is unique across the roster by construction. A duplicate
        // would make the visit and enquiry lookups silently pick one, so this throws.
        return patients.ToDictionary(p => p.LastName, StringComparer.Ordinal);
    }

    private static DateTime ToUtc(DateOnly localDate, TimeOnly localTime) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localDate.ToDateTime(localTime), DateTimeKind.Unspecified),
            PracticeTime.Zone);
}

/// <summary>Who the seed belongs to, or why the tool refuses to decide.</summary>
public sealed record ProviderResolution(long? ProviderId, string? DisplayName, string? Refusal);

/// <summary>
/// What a run CREATED. A second run against the same database reports zeros throughout, and
/// that is the observable form of the idempotency claim.
/// </summary>
public sealed record DemoSeedReport(
    int Patients,
    int Guardians,
    int Addresses,
    int Goals,
    int Visits,
    int Notes,
    int Enquiries)
{
    public int Total => Patients + Guardians + Addresses + Goals + Visits + Notes + Enquiries;
}
