namespace Practice.Domain.Consultations;

/// <summary>
/// Where a public enquiry has got to (docs/DATA_MODEL.md).
///
/// Explicit values, never reordered: these persist as integers, and renumbering would
/// silently rewrite the meaning of every historical row.
/// </summary>
public enum ConsultationStatus
{
    /// <summary>Arrived, nobody has replied yet. Every row starts here.</summary>
    New = 1,

    Contacted = 2,

    /// <summary>Became a patient. ConvertedPatientId says which one.</summary>
    Converted = 3,

    Declined = 4,
}
