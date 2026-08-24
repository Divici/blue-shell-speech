using System.Reflection;
using Practice.Domain;

namespace Practice.Domain.Tests;

/// <summary>
/// docs/ARCHITECTURE.md states: "Practice.Domain references nothing."
///
/// That claim is what lets clinical invariants — note immutability, the amendment rule,
/// the no-fabricated-numbers rule — be tested without a database and be unavoidable from
/// any caller. A claim in a document decays; this test is what keeps it true.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(IDomainAssemblyMarker).Assembly;

    /// <summary>
    /// Anything here would drag infrastructure into the domain and make the invariants
    /// dependent on a framework being present and configured correctly.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.SqlClient",
        "Microsoft.Azure",
        "Azure.",
        "Practice.Application",
        "Practice.Infrastructure",
        "Practice.Api",
    ];

    [Fact]
    public void Domain_references_no_infrastructure()
    {
        var violations = Domain
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(
                p => name.StartsWith(p, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Practice.Domain must reference no infrastructure. Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        var external = Domain
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name =>
                !name.StartsWith("System", StringComparison.Ordinal) &&
                !name.Equals("netstandard", StringComparison.Ordinal) &&
                !name.Equals("mscorlib", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            external.Length == 0,
            $"Practice.Domain should depend only on the BCL. Found: {string.Join(", ", external)}");
    }
}
