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
        group.MapPost("/{publicId:guid}/addresses", AddAddress);

        return app;
    }

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
            ipAddress: http.Connection.RemoteIpAddress?.ToString()), ct);

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
            entityType: nameof(Patient), entityPublicId: patient.PublicId), ct);

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
            entityType: nameof(Patient), entityPublicId: patient.PublicId), ct);

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
            metadata: "action=discharged"), ct);

        return Results.Ok(PatientDetail.From(patient));
    }

    private static async Task<IResult> AddGuardian(
        Guid publicId,
        AddGuardianRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients
            .Include(p => p.Guardians)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        try
        {
            patient.AddGuardian(
                request.FirstName, request.LastName, request.Relationship,
                request.Phone, request.Email,
                request.IsPrimaryContact, request.HasLegalAuthority);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(PatientDetail.From(patient));
    }

    private static async Task<IResult> AddAddress(
        Guid publicId,
        AddAddressRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients
            .Include(p => p.Addresses)
            .SingleOrDefaultAsync(p => p.PublicId == publicId, ct);

        if (patient is null) return Results.NotFound();

        try
        {
            patient.AddAddress(
                request.Line1, request.Line2, request.City, request.State, request.PostalCode,
                request.AddressType, request.Notes,
                request.EffectiveFrom ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(PatientDetail.From(patient));
    }
}

// --------------------------------------------------------------------- DTOs

public sealed record PatientSummary(
    Guid PublicId, string FirstName, string LastName, DateOnly DateOfBirth, string Status);

public sealed record GuardianDto(
    Guid PublicId, string FirstName, string LastName, string Relationship,
    string? Phone, string? Email, bool IsPrimaryContact, bool HasLegalAuthority);

public sealed record AddressDto(
    Guid PublicId, string Line1, string? Line2, string City, string State,
    string PostalCode, string AddressType, string? Notes, bool IsCurrent);

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
            a.AddressType.ToString(), a.Notes, a.IsCurrent)).ToList());
}

public sealed record CreatePatientRequest(
    string FirstName, string LastName, DateOnly DateOfBirth, string? ClinicalSummary);

public sealed record UpdatePatientRequest(
    string FirstName, string LastName, string? ClinicalSummary);

public sealed record AddGuardianRequest(
    string FirstName, string LastName, string Relationship,
    string? Phone, string? Email, bool IsPrimaryContact, bool HasLegalAuthority);

public sealed record AddAddressRequest(
    string Line1, string? Line2, string City, string State, string PostalCode,
    AddressType AddressType, string? Notes, DateOnly? EffectiveFrom);
