using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Auth;

/// <summary>
/// Populates the per-request provider context from the forwarded public id.
///
/// Runs before anything that touches patient data, so the global query filter is armed for
/// the whole request. A request with no header, or an unknown one, leaves the context null
/// — and a null provider matches NO rows rather than all of them.
/// </summary>
public sealed class ProviderContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IProviderContext providerContext,
        PracticeDbContext db)
    {
        var header = context.Request.Headers[RequestProviderContext.HeaderName].ToString();

        if (!string.IsNullOrWhiteSpace(header)
            && Guid.TryParse(header, out var publicId)
            && providerContext is RequestProviderContext resolvable)
        {
            /*
             * Resolved by PUBLIC id, then used as an internal id.
             *
             * The internal key never leaves the server, so a caller cannot supply one —
             * and a caller who guesses a GUID still has to guess a real one, which is the
             * property opaque identifiers exist for (docs/DATA_MODEL.md).
             *
             * Providers table has no query filter: it is not patient data, and resolving
             * the current provider is precisely the step that arms the filter.
             */
            var providerId = await db.Providers
                .AsNoTracking()
                .Where(p => p.PublicId == publicId && p.IsActive)
                .Select(p => (long?)p.Id)
                .SingleOrDefaultAsync(context.RequestAborted);

            resolvable.Resolve(providerId);
        }

        await next(context);
    }
}
