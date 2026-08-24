using Microsoft.Data.SqlClient;

/*
 * Grants an Azure managed identity access to the database.
 *
 * WHY A TOOL AND NOT A PORTAL CLICK:
 *
 * Azure SQL is configured for Entra-only authentication — there is no SQL login and no
 * password anywhere (DECISIONS.md D028). A managed identity therefore needs a CONTAINED
 * DATABASE USER, created with `CREATE USER ... FROM EXTERNAL PROVIDER`. That statement can
 * only be run by the Entra admin, over an authenticated connection, against the database
 * itself — there is no `az` command for it and no portal blade.
 *
 * Doing it by hand means the grant exists in someone's memory. A blown-away database
 * should be a re-run (presearch §12.3), so it lives here.
 *
 * The roles granted are deliberately narrow:
 *   db_datareader / db_datawriter  — read and write rows
 *   db_ddladmin                    — apply EF migrations
 *
 * NOT db_owner. And the audit table is locked down separately below: the application must
 * never be able to UPDATE or DELETE an audit row (docs/SECURITY.md).
 *
 * Usage:
 *   dotnet run -- "<server>.database.windows.net" "<database>" "<managed-identity-name>"
 */

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "usage: dotnet run -- <server-fqdn> <database> <managed-identity-name>");
    return 1;
}

var (server, database, identityName) = (args[0], args[1], args[2]);

var connectionString =
    $"Server=tcp:{server},1433;Database={database};" +
    "Authentication=Active Directory Default;Encrypt=True;Connection Timeout=120;";

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
Console.WriteLine($"Connected to {database} on {server} as the Entra admin.");

/*
 * Idempotent: safe to re-run.
 *
 * The identity name is quoted rather than parameterised because T-SQL does not permit a
 * parameter in a CREATE USER principal name. It is validated against a strict pattern
 * first — Azure resource names cannot contain a quote, so a name that passes this cannot
 * carry an injection payload.
 */
if (!System.Text.RegularExpressions.Regex.IsMatch(identityName, @"^[A-Za-z0-9\-_]{1,128}$"))
{
    Console.Error.WriteLine($"Refusing to use '{identityName}' as a principal name.");
    return 1;
}

var statements = new[]
{
    $"""
     IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '{identityName}')
     BEGIN
         CREATE USER [{identityName}] FROM EXTERNAL PROVIDER;
     END
     """,

    $"ALTER ROLE db_datareader ADD MEMBER [{identityName}];",
    $"ALTER ROLE db_datawriter ADD MEMBER [{identityName}];",

    // Required so the app can apply EF migrations at deploy time.
    $"ALTER ROLE db_ddladmin ADD MEMBER [{identityName}];",

    /*
     * The audit log is append-only, enforced by permission rather than by convention.
     *
     * db_datawriter grants UPDATE and DELETE on every table; these DENY statements take
     * them back for AuditEvents specifically. DENY beats GRANT in SQL Server, so this
     * holds even though the role membership remains.
     *
     * A breach nobody can scope is worse than a breach — and an audit log the application
     * can rewrite is exactly that.
     */
    $"""
     IF OBJECT_ID('dbo.AuditEvents', 'U') IS NOT NULL
     BEGIN
         DENY UPDATE ON dbo.AuditEvents TO [{identityName}];
         DENY DELETE ON dbo.AuditEvents TO [{identityName}];
     END
     """,
};

foreach (var sql in statements)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    await command.ExecuteNonQueryAsync();
    Console.WriteLine($"  ok: {sql.ReplaceLineEndings(" ")[..Math.Min(70, sql.Length)]}…");
}

Console.WriteLine($"\n{identityName} can now read and write, and CANNOT alter the audit log.");
return 0;
