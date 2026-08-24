using System.Reflection;
using System.Xml.Linq;

namespace Practice.Application.Tests;

/// <summary>
/// This project previously contained no tests at all.
///
/// `dotnet test` reported it as passing, and CI displayed "api · build, test: success"
/// across three test projects, one of which asserted nothing. An empty test project is
/// indistinguishable from a passing one in that output.
///
/// These hold the dependency direction docs/ARCHITECTURE.md claims for the Application
/// layer.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// Project references are read from the .csproj, NOT from
    /// <c>Assembly.GetReferencedAssemblies()</c>.
    ///
    /// That distinction matters and is easy to get wrong. GetReferencedAssemblies lists
    /// what the compiler actually emitted a reference to — assemblies whose types are
    /// *used*. A project can declare a ProjectReference to Infrastructure, use nothing
    /// from it yet, and the reflection-based check passes while the architectural
    /// violation is already committed and waiting for its first `using`.
    ///
    /// Reading the project file catches the declaration, which is where the boundary is
    /// actually drawn.
    /// </summary>
    private static List<string> ProjectReferencesOf(string projectName)
    {
        var csproj = Path.Combine(RepoRoot(), "src", projectName, $"{projectName}.csproj");
        Assert.True(File.Exists(csproj), $"Could not find {csproj}");

        return XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            /*
             * Normalise separators BEFORE splitting.
             *
             * MSBuild writes Windows-style paths — "..\Practice.Domain\Practice.Domain.csproj"
             * — regardless of the host OS. On Windows, Path.GetFileNameWithoutExtension
             * splits on the backslash and returns "Practice.Domain". On Linux a backslash
             * is an ordinary filename character, so it returns the entire relative path
             * and the assertion fails.
             *
             * This passed on the developer's machine and failed in CI, which is exactly
             * the shape of bug that makes a green local build untrustworthy.
             */
            .Select(include => include.Replace('\\', '/'))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>Walks up from the test binaries until it finds the solution file.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Practice.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Application orchestrates use cases. It may depend on Domain and on abstractions it
    /// defines itself — never on the concrete infrastructure implementing them, and never
    /// on the web host.
    ///
    /// This is what keeps ITranscriptionService and IClinicalNoteGenerationService
    /// swappable (presearch §8.1). If Application could reference Infrastructure,
    /// "change the AI vendor" would stop being a configuration change.
    /// </summary>
    [Fact]
    public void Application_does_not_reference_infrastructure_or_the_web_host()
    {
        string[] forbidden = ["Practice.Infrastructure", "Practice.Api"];

        var violations = ProjectReferencesOf("Practice.Application")
            .Where(forbidden.Contains)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Practice.Application must not reference: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Application_builds_on_the_domain()
    {
        Assert.Contains("Practice.Domain", ProjectReferencesOf("Practice.Application"));
    }

    /// <summary>
    /// The Domain must reference nothing at all.
    ///
    /// Practice.Domain.Tests asserts this reflectively, which catches *usage*. This
    /// catches the declaration, so a ProjectReference added "for later" fails immediately
    /// rather than the first time someone writes a `using`.
    /// </summary>
    [Fact]
    public void Domain_declares_no_project_references()
    {
        Assert.Empty(ProjectReferencesOf("Practice.Domain"));
    }
}
