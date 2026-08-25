using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Patients;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Patients;

/// <summary>
/// Patient records.
///
/// TWO RULES GOVERN EVERY ENDPOINT HERE:
///
/// 1. Scoping is not optional. The global query filter restricts every read to the
///    current provider, and a null provider matches nothing. Nothing in this file
///    calls IgnoreQueryFilters().
///
/// 2. A record belonging to another provider returns 404, never 403. A 403 confirms the
///    record exists, which turns a permission check into an enumeration oracle
///    (docs/TEST_STRATEGY.md).
/// </summary>
public static class PatientEndpoints
{
    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/patients").WithTags("Patients");

        group.MapGet("/", ListPatients);
        group.MapGet("/{publicId:guid}", GetPatient);
        group.MapPost("/", CreatePatient);
        group.MapPut("/{publicId:guid}", UpdatePatient);
        group.MapPost("/{publicId:guid}/discharge", DischargePatient);
        group.MapPost("/{publicId:guid}/guardians", AddGuardian);
        group.MapPut("/{publicId:guid}/guardians/{guardianPublicId:guid}", UpdateGuardian);
        group.MapPost("/{publicId:guid}/addresses", AddAddress);
        group.MapPut("/{publicId:guid}/addresses/{addressPublicId:guid}", CorrectAddress);

        return app;
    }

    /*
     * WHO MAY RECEIVE A CHILD'S RECORDS IS ANSWERED, NEVER ASSUMED.
     *
     * HasLegalAuthority is a bit and has no room for "nobody said". System.Text.Json turns
     * an absent bool into false, so a body that never mentions it would be persisted as a
     * decision that this parent may NOT have the file — indistinguishable afterwards from
     * someone actually deciding that. The request type is bool? and a null is refused, so
     * the difference survives at the only layer that can still see it.
     *
     * Deliberately NOT inferred from IsPrimaryContact. A stepparent can be the adult who
     * brings the child every week and hold no authority to consent; a non-custodial parent
     * can hold the authority and never appear at a session. Custody is not an edge case in
     * paediatrics, and a record released to the wrong adult is a breach.
     */
    private const string AuthorityNotStated =
        "Say whether this person may receive the child's records. It is a separate question "
        + "from who the primary contact is, and it is not implied by being one.";

    private static async Task<IResult> ListPatients(
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct,
        string? search = null,
        bool includeDischarged = false)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var query = db.Patients.AsNoTracking();

        if (!includeDischarged)
        {
            query = query.Where(p => p.Status != PatientStatus.Discharged);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            /*
             * Prefix search on both names.
             *
             * This is the interaction Always Encrypted would have broken (D012):
             * deterministic encryption supports equality only, and nobody looks a child up
             * by typing their exact full surname.
             */
            var term = search.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.LastName, term + "%") ||
                EF.Functions.Like(p.FirstName, term + "%"));
        }

        var patients = await query
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Select(p => new PatientSummary(
                p.PublicId, p.FirstName, p.LastName, p.DateOfBirth, p.Status.ToString()))
            .ToListAsync(ct);

        return Results.Ok(patients);
    }

    private static async Task<IResult> GetPatient(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients
            .AsNoTracking()
            .Include(p => p.Guardians)
            .Include(p => p.Addresses)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        /*
         * READ ACCESS IS AUDITED.
         *
         * Under HIPAA, access to ePHI is an auditable event — not just modification. Most
         * homegrown systems log only writes and discover the gap during an investigation,
         * when the question is "who looked at this record" and there is no answer.
         */
        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientViewed,
            AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(Patient),
            entityPublicId: patient.PublicId,
            ipAddress: http.Connection.RemoteIpAddress?.ToString()));

        return Results.Ok(PatientDetail.From(patient));
    }

    private static async Task<IResult> CreatePatient(
        CreatePatientRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        Patient patient;
        try
        {
            patient = Patient.Create(
                provider.ProviderId.Value,
                request.FirstName,
                request.LastName,
                request.DateOfBirth,
                DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                request.ClinicalSummary);
        }
        catch (ArgumentException ex)
        {
            // Domain invariants surface as 400 with the reason. These messages are written
            // for the clinician — "that date of birth looks like a typo" — and contain no
            // patient data of their own.
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        db.Patients.Add(patient);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientCreated, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(Patient), entityPublicId: patient.PublicId));

        return Results.Created(
            $"/patients/{patient.PublicId}", PatientDetail.From(patient));
    }

    private static async Task<IResult> UpdatePatient(
        Guid publicId,
        UpdatePatientRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients.SingleOrDefaultAsync(p => p.PublicId == publicId, ct);
        if (patient is null) return Results.NotFound();

        try
        {
            patient.Rename(request.FirstName, request.LastName);
            patient.UpdateClinicalSummary(request.ClinicalSummary);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientUpdated, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(Patient), entityPublicId: patient.PublicId));

        return Results.Ok(PatientDetail.From(patient));
    }

    /// <summary>Discharge, not delete. Clinical rows are never removed.</summary>
    private static async Task<IResult> DischargePatient(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients.SingleOrDefaultAsync(p => p.PublicId == publicId, ct);
        if (patient is null) return Results.NotFound();

        patient.Discharge();
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientUpdated, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(Patient), entityPublicId: patient.PublicId,
            metadata: "action=discharged"));

        return Results.Ok(PatientDetail.From(patient));
    }

    private static async Task<IResult> AddGuardian(
        Guid publicId,
        AddGuardianRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        if (request.HasLegalAuthority is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hasLegalAuthority"] = [AuthorityNotStated],
            });
        }

        var patient = await db.Patients
            .Include(p => p.Guardians)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        Guardian guardian;
        try
        {
            guardian = patient.AddGuardian(
                request.FirstName, request.LastName, request.Relationship,
                request.Phone, request.Email,
                request.IsPrimaryContact, request.HasLegalAuthority.Value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return InvariantRefused(ex);
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(GuardianAudit(
            provider.ProviderId, patient, guardian, "guardian-added"));

        return Results.Ok(PatientDetail.From(patient));
    }

    /// <summary>
    /// Edits a guardian already on the record.
    ///
    /// Two identifiers, both re-resolved server-side: the patient through the global query
    /// filter, and the guardian through the aggregate's own collection. The second is not
    /// redundant — both rows can belong to the same provider, so the filter cannot tell a
    /// guardian id from another of Michelle's patients apart from one on this patient, and
    /// without the aggregate scoping the lookup the edit would land on the wrong child's
    /// record.
    /// </summary>
    private static async Task<IResult> UpdateGuardian(
        Guid publicId,
        Guid guardianPublicId,
        UpdateGuardianRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        if (request.HasLegalAuthority is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["hasLegalAuthority"] = [AuthorityNotStated],
            });
        }

        var patient = await db.Patients
            .Include(p => p.Guardians)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        Guardian? guardian;
        try
        {
            guardian = patient.UpdateGuardian(
                guardianPublicId,
                request.FirstName, request.LastName, request.Relationship,
                request.Phone, request.Email,
                request.IsPrimaryContact, request.HasLegalAuthority.Value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return InvariantRefused(ex);
        }

        // A guardian on somebody else's record is 404, identically to one that never
        // existed. The response cannot become an oracle for which ids are real (D052).
        if (guardian is null) return Results.NotFound();

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(GuardianAudit(
            provider.ProviderId, patient, guardian, "guardian-updated"));

        return Results.Ok(PatientDetail.From(patient));
    }

    /// <summary>Recording a MOVE. The previous address of the same type is closed, not removed.</summary>
    private static async Task<IResult> AddAddress(
        Guid publicId,
        AddAddressRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients
            .Include(p => p.Addresses)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        PatientAddress address;
        try
        {
            address = patient.AddAddress(
                request.Line1, request.Line2, request.City, request.State, request.PostalCode,
                request.AddressType, request.Notes,
                request.EffectiveFrom ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return InvariantRefused(ex);
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AddressAudit(
            provider.ProviderId, patient, address, "address-added"));

        return Results.Ok(PatientDetail.From(patient));
    }

    /// <summary>
    /// Fixing a TYPO. One row changes in place; nothing is superseded and no dates move.
    ///
    /// Separate from AddAddress because the two are different events with different
    /// consequences. A move must keep the old address — an appointment last spring happened
    /// there. A typo must not, because the family never lived at the mistyped address, and
    /// recording one as the other either invents a move or erases a real one.
    /// </summary>
    private static async Task<IResult> CorrectAddress(
        Guid publicId,
        Guid addressPublicId,
        CorrectAddressRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients
            .Include(p => p.Addresses)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        PatientAddress? address;
        try
        {
            address = patient.CorrectAddress(
                addressPublicId,
                request.Line1, request.Line2, request.City,
                request.State, request.PostalCode, request.Notes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return InvariantRefused(ex);
        }

        if (address is null) return Results.NotFound();

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AddressAudit(
            provider.ProviderId, patient, address, "address-corrected"));

        return Results.Ok(PatientDetail.From(patient));
    }

    /// <summary>
    /// A domain invariant refused the write, surfaced as 400 with the aggregate's own
    /// wording — sentences written for a clinician, carrying no patient data of their own.
    /// </summary>
    private static IResult InvariantRefused(Exception ex) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [(ex as ArgumentException)?.ParamName ?? "request"] = [ex.Message],
        });

    /*
     * WHAT THE AUDIT LOG SAYS ABOUT A GUARDIAN.
     *
     * "Who was allowed to receive this child's records, and when did that change" is the
     * question this table exists to answer about a custody arrangement, and it is asked
     * after the fact — when nobody can reconstruct the row's history from the row.
     *
     * Fixed vocabulary and opaque ids only, the same shape the refused-delete reasons use
     * (D071). No names, no phone numbers, no relationship: Metadata must never carry
     * content, because the audit log is the table most likely to leave the building.
     */
    private static AuditEvent GuardianAudit(
        long? providerId, Patient patient, Guardian guardian, string action) =>
        AuditEvent.Record(
            AuditEventType.PatientUpdated, AuditOutcome.Success,
            providerId: providerId,
            entityType: nameof(Patient), entityPublicId: patient.PublicId,
            metadata: $"action={action};guardian={guardian.PublicId};legalAuthority="
                + (guardian.HasLegalAuthority ? "granted" : "withheld")
                + ";primaryContact=" + (guardian.IsPrimaryContact ? "yes" : "no"));

    private static AuditEvent AddressAudit(
        long? providerId, Patient patient, PatientAddress address, string action) =>
        AuditEvent.Record(
            AuditEventType.PatientUpdated, AuditOutcome.Success,
            providerId: providerId,
            entityType: nameof(Patient), entityPublicId: patient.PublicId,
            metadata: $"action={action};address={address.PublicId};type={address.AddressType}");
}

// --------------------------------------------------------------------- DTOs

public sealed record PatientSummary(
    Guid PublicId, string FirstName, string LastName, DateOnly DateOfBirth, string Status);

public sealed record GuardianDto(
    Guid PublicId, string FirstName, string LastName, string Relationship,
    string? Phone, string? Email, bool IsPrimaryContact, bool HasLegalAuthority);

/// <summary>
/// The effective dates travel with the address because the record is VERSIONED: a move
/// closes the previous row rather than overwriting it, and the page has to be able to say
/// which address a visit last spring happened at. IsCurrent alone cannot answer that.
/// </summary>
public sealed record AddressDto(
    Guid PublicId, string Line1, string? Line2, string City, string State,
    string PostalCode, string AddressType, string? Notes, bool IsCurrent,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record PatientDetail(
    Guid PublicId,
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string Status,
    string? ClinicalSummary,
    IReadOnlyList<GuardianDto> Guardians,
    IReadOnlyList<AddressDto> Addresses)
{
    public static PatientDetail From(Patient patient) => new(
        patient.PublicId,
        patient.FirstName,
        patient.LastName,
        patient.DateOfBirth,
        patient.Status.ToString(),
        patient.ClinicalSummary,
        patient.Guardians.Select(g => new GuardianDto(
            g.PublicId, g.FirstName, g.LastName, g.Relationship,
            g.Phone, g.Email, g.IsPrimaryContact, g.HasLegalAuthority)).ToList(),
        patient.Addresses.Select(a => new AddressDto(
            a.PublicId, a.Line1, a.Line2, a.City, a.State, a.PostalCode,
            a.AddressType.ToString(), a.Notes, a.IsCurrent,
            a.EffectiveFrom, a.EffectiveTo)).ToList());
}

public sealed record CreatePatientRequest(
    string FirstName, string LastName, DateOnly DateOfBirth, string? ClinicalSummary);

public sealed record UpdatePatientRequest(
    string FirstName, string LastName, string? ClinicalSummary);

/// <summary>
/// <paramref name="HasLegalAuthority"/> is NULLABLE so that "nobody said" is a distinct
/// state on the way in, and refused. The column is a bit; a missing bool deserialises to
/// false, which would record a decision that this parent may not have their child's file
/// and leave no way to tell it from someone making that decision.
/// </summary>
public sealed record AddGuardianRequest(
    string FirstName, string LastName, string Relationship,
    string? Phone, string? Email, bool IsPrimaryContact, bool? HasLegalAuthority);

/// <inheritdoc cref="AddGuardianRequest"/>
public sealed record UpdateGuardianRequest(
    string FirstName, string LastName, string Relationship,
    string? Phone, string? Email, bool IsPrimaryContact, bool? HasLegalAuthority);

public sealed record AddAddressRequest(
    string Line1, string? Line2, string City, string State, string PostalCode,
    AddressType AddressType, string? Notes, DateOnly? EffectiveFrom);

/// <summary>
/// A correction carries no AddressType and no dates, because it is not a move. The type
/// decides what supersedes what and the dates decide which address a past visit happened
/// at; letting a typo fix move either would rewrite history under a note that refers to it.
/// </summary>
public sealed record CorrectAddressRequest(
    string Line1, string? Line2, string City, string State, string PostalCode, string? Notes);
