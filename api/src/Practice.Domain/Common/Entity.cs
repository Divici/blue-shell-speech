namespace Practice.Domain.Common;

/// <summary>
/// Base for every domain entity.
///
/// Two identifiers, deliberately (docs/DATA_MODEL.md):
///
///   Id       bigint identity, the clustered key. Narrow and sequential, which is what
///            SQL Server wants for a clustered index.
///   PublicId uniqueidentifier. The ONLY identifier that may appear in a URL, an API
///            payload, or an email.
///
/// A GUID clustered key fragments the index badly on insert; NEWSEQUENTIALID fixes the
/// fragmentation but makes the value partly guessable, defeating the reason we wanted a
/// GUID. Splitting the two roles gets both properties. The cost is one lookup on
/// public-id resolution and the discipline never to leak Id.
/// </summary>
public abstract class Entity
{
    public long Id { get; protected set; }

    public Guid PublicId { get; protected set; } = Guid.NewGuid();

    /// <summary>UTC always. Rendered America/New_York; never stored that way.</summary>
    public DateTime CreatedAtUtc { get; protected set; } = DateTime.UtcNow;

    public string? CreatedBy { get; protected set; }

    /// <summary>Maps to SQL Server rowversion for optimistic concurrency.</summary>
    public byte[]? RowVersion { get; protected set; }
}
