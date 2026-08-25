using Practice.Domain.Common;

namespace Practice.Domain.Goals;

/// <summary>
/// A treatment goal.
///
/// These matter beyond record-keeping: the dictation pipeline needs to know what is
/// currently being targeted, because extraction classifies what Michelle says against the
/// patient's ACTIVE goals rather than inventing categories (presearch §5.4, §7.4).
/// </summary>
public sealed class Goal : Entity
{
    private Goal() { }

    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    /// <summary>The goal in the clinician's own words. PHI.</summary>
    public string GoalText { get; private set; } = string.Empty;

    public GoalDomain Domain { get; private set; }

    /// <summary>
    /// e.g. "80% accuracy over 3 consecutive sessions".
    ///
    /// FREE TEXT, deliberately. presearch §5.4 warns against building a universal clinical
    /// goal language: parsing that sentence into a rule engine is a project in itself, and
    /// whatever grammar it produced would be wrong for the next goal.
    /// </summary>
    public string? TargetCriteria { get; private set; }

    public CueLevel? CueLevelExpected { get; private set; }

    public GoalStatus Status { get; private set; } = GoalStatus.Active;

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    /// <summary>Only meaningful when Domain is AAC. Nullable, and not overmodelled.</summary>
    public AacModality? AacModality { get; private set; }

    public string? AacDeviceNotes { get; private set; }

    public static Goal Create(
        long providerId,
        long patientId,
        string goalText,
        GoalDomain domain,
        DateOnly startDate,
        string? targetCriteria = null,
        CueLevel? cueLevelExpected = null,
        AacModality? aacModality = null,
        string? aacDeviceNotes = null)
    {
        if (providerId <= 0) throw new ArgumentException("A goal needs a provider.", nameof(providerId));
        if (patientId <= 0) throw new ArgumentException("A goal needs a patient.", nameof(patientId));

        /*
         * AAC details only make sense on an AAC goal.
         *
         * Silently accepting a modality on an articulation goal would produce a record
         * that reads as clinically meaningful and is not — and the dictation pipeline
         * reads these fields when deciding how to interpret what Michelle said.
         */
        if (domain != GoalDomain.Aac && (aacModality is not null || aacDeviceNotes is not null))
        {
            throw new ArgumentException(
                "AAC details belong on an AAC goal.", nameof(aacModality));
        }

        return new Goal
        {
            ProviderId = providerId,
            PatientId = patientId,
            GoalText = Guard.MaxLength(Guard.NotBlank(goalText, "goalText"), 1000, "goalText"),
            Domain = domain,
            StartDate = startDate,
            TargetCriteria = Normalise(targetCriteria, 500),
            CueLevelExpected = cueLevelExpected,
            AacModality = aacModality,
            AacDeviceNotes = Normalise(aacDeviceNotes, 500),
        };
    }

    /// <summary>
    /// The goal is achieved.
    ///
    /// A met goal is closed, not deleted: it is part of the record of what therapy
    /// accomplished, and progress over time is the thing families and payers ask about.
    /// </summary>
    public void MarkMet(DateOnly on)
    {
        EnsureOpen();
        Status = GoalStatus.Met;
        EndDate = on;
    }

    /// <summary>Stopped without being achieved — priorities changed, or it was the wrong goal.</summary>
    public void Discontinue(DateOnly on)
    {
        EnsureOpen();
        Status = GoalStatus.Discontinued;
        EndDate = on;
    }

    /// <summary>Paused. Still on the plan, not currently targeted.</summary>
    public void PutOnHold()
    {
        EnsureOpen();
        Status = GoalStatus.OnHold;
    }

    public void Resume()
    {
        if (Status is GoalStatus.Met or GoalStatus.Discontinued)
        {
            throw new InvalidOperationException(
                "A closed goal cannot be resumed. Write a new goal instead — the record of what was achieved stays intact.");
        }

        Status = GoalStatus.Active;
        EndDate = null;
    }

    public void Revise(string goalText, string? targetCriteria, CueLevel? cueLevel)
    {
        EnsureOpen();
        GoalText = Guard.MaxLength(Guard.NotBlank(goalText, "goalText"), 1000, "goalText");
        TargetCriteria = Normalise(targetCriteria, 500);
        CueLevelExpected = cueLevel;
    }

    /// <summary>True when the dictation pipeline should consider this goal.</summary>
    public bool IsCurrentlyTargeted => Status == GoalStatus.Active;

    private void EnsureOpen()
    {
        if (Status is GoalStatus.Met or GoalStatus.Discontinued)
        {
            throw new InvalidOperationException(
                "This goal is closed. Reopening would rewrite the record of what happened.");
        }
    }

    private static string? Normalise(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Guard.MaxLength(value.Trim(), max, "value");
}

/// <summary>
/// Explicit values, never reordered — persisted as integers.
/// </summary>
public enum GoalDomain
{
    Articulation = 1,
    ReceptiveLanguage = 2,
    ExpressiveLanguage = 3,
    SocialCommunication = 4,
    Fluency = 5,
    Feeding = 6,

    /// <summary>Augmentative and alternative communication. Confirmed in scope.</summary>
    Aac = 7,
}

/// <summary>
/// How much help the child needs.
///
/// Ordered from most to least independent, because "improving" means moving UP this list —
/// and a note that records a cue level without that ordering cannot show progress.
/// </summary>
public enum CueLevel
{
    Independent = 1,
    Visual = 2,
    Gestural = 3,
    Verbal = 4,
    Tactile = 5,
    HandOverHand = 6,
}

public enum GoalStatus
{
    Active = 1,
    Met = 2,
    Discontinued = 3,
    OnHold = 4,
}

public enum AacModality
{
    /// <summary>A speech-generating device or app.</summary>
    HighTech = 1,

    /// <summary>Picture boards, communication books.</summary>
    LowTech = 2,

    PECS = 3,
    Sign = 4,
    Hybrid = 5,
}
