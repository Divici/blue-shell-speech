namespace Practice.Domain.Consultations;

/// <summary>
/// How the parent asked to be reached.
///
/// Explicit values, never reordered: these persist as integers, and renumbering would
/// silently rewrite the meaning of every historical row.
/// </summary>
public enum PreferredContactMethod
{
    Email = 1,
    Phone = 2,

    /// <summary>Either is fine. Still requires a phone number to have been left.</summary>
    Either = 3,
}
