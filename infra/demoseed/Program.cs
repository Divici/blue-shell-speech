using BlueShell.DemoSeed;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Practice.Infrastructure.Persistence;

/*
 * Fills a database with a synthetic caseload so the product can be shown working.
 *
 * WHY A STANDALONE TOOL AND NOT A SEEDER IN THE APPLICATION:
 *
 * ProviderSeeder is an IHostedService that runs on every start and decides what to do from
 * configuration. That shape is right for the ONE row it writes — an account with no
 * password in the tree and no way to create one otherwise — and it is the wrong shape for
 * this. A hosted service gated on an environment variable is a production code path that
 * happens to be switched off, and the distance between "off" and "on" is one line in a
 * container app's configuration blade, typed by someone who is not thinking about it.
 *
 * This project is referenced by nothing that ships. The deployed image is built from
 * Practice.Api and its transitive closure; DemoSeed is not in that closure and Practice.Api
 * does not know it exists. There is no flag to flip, no variable to set, and no endpoint to
 * reach — getting fictional children into a production database requires adding a project
 * reference, rebuilding the image, and deploying it. That is the same argument
 * infra/dbgrant makes about itself, and the reasoning is recorded in DECISIONS.md D099.
 *
 * The second guard is at run time, because the first one cannot see a human with the source
 * tree and the wrong connection string in their shell: the tool REFUSES a database that
 * holds any patient or enquiry it did not write, before it writes anything. See
 * DemoSeeder.RefusalReasonAsync.
 *
 * Usage, from PowerShell on Windows:
 *
 *   $env:BLUESHELL_MIGRATIONS_CONNECTION = "<the same string dotnet ef database update used>"
 *   dotnet run --project infra/demoseed -- <database-name>
 *
 * The database name is a required argument and must match the connection string's own
 * Initial Catalog. It is not redundant: it is how a stale variable left over from an
 * earlier session becomes a refusal instead of a write.
 *
 * Exit codes: 0 seeded (or already seeded), 1 misuse or misconfiguration, 2 refused.
 */

const string ConnectionVariable = "BLUESHELL_MIGRATIONS_CONNECTION";

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine(
        "usage: dotnet run --project infra/demoseed -- <database-name>\n"
        + $"       with {ConnectionVariable} set to that database's connection string.");
    return 1;
}

var expectedDatabase = args[0].Trim();
var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"{ConnectionVariable} is not set. Nothing to connect to.");
    return 1;
}

SqlConnectionStringBuilder target;
try
{
    target = new SqlConnectionStringBuilder(connectionString);
}
catch (ArgumentException ex)
{
    // The message, never the string itself — a connection string can carry a credential.
    Console.Error.WriteLine($"{ConnectionVariable} is not a valid connection string: {ex.Message}");
    return 1;
}

/*
 * THE NAMED TARGET.
 *
 * An environment variable persists across sessions and does not announce what it points
 * at. Requiring the operator to type the database name, and refusing when it does not
 * match, converts "I forgot what that variable was set to" from a silent write into a
 * message. It is a small guard and it is honest about its size: it stops the wrong
 * database, not a determined operator.
 */
if (!string.Equals(target.InitialCatalog, expectedDatabase, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"Refusing: {ConnectionVariable} points at '{target.InitialCatalog}', "
        + $"and you asked to seed '{expectedDatabase}'.");
    return 2;
}

Console.WriteLine($"Target: {target.InitialCatalog} on {target.DataSource}");

var options = new DbContextOptionsBuilder<PracticeDbContext>()
    .UseSqlServer(connectionString, sql =>
    {
        // The same reasoning DesignTimeDbContextFactory gives: the free Azure SQL offer
        // auto-pauses on idle, so the first connection of the day fails as a transient
        // error while the database resumes. Without this, a first run looks like flakiness.
        sql.EnableRetryOnFailure(maxRetryCount: 10, TimeSpan.FromSeconds(20), null);
        sql.CommandTimeout(180);
    })
    .Options;

/*
 * Whose caseload, before anything else.
 *
 * Every patient table carries a global query filter keyed on IProviderContext, and a null
 * provider matches NOTHING (D051) — so nothing can be read or checked until the provider is
 * known. Resolution runs through its own unarmed context (Providers is unfiltered, being
 * the thing the filter is made of), and the seeder then builds its own armed one.
 */
var resolution = await DemoSeeder.ResolveSoleActiveProviderAsync(options, CancellationToken.None);

if (resolution.Refusal is not null)
{
    Console.Error.WriteLine(resolution.Refusal);
    return 2;
}

await using var seeder = new DemoSeeder(
    options, resolution.ProviderId!.Value, resolution.DisplayName);

var refusal = await seeder.RefusalReasonAsync(CancellationToken.None);
if (refusal is not null)
{
    Console.Error.WriteLine(refusal);
    return 2;
}

var report = await seeder.SeedAsync(DateTime.UtcNow, CancellationToken.None);

if (report.Total == 0)
{
    Console.WriteLine("Already seeded. Nothing to do.");
    return 0;
}

Console.WriteLine($"  patients   {report.Patients}");
Console.WriteLine($"  guardians  {report.Guardians}");
Console.WriteLine($"  addresses  {report.Addresses}");
Console.WriteLine($"  goals      {report.Goals}");
Console.WriteLine($"  visits     {report.Visits}");
Console.WriteLine($"  notes      {report.Notes}");
Console.WriteLine($"  enquiries  {report.Enquiries}");
Console.WriteLine($"\nSeeded {report.Total} synthetic rows. Every name in them is invented.");

return 0;
