namespace Aftermath.Correlation;

using Aftermath.Contracts;

/// <summary>
/// A change event ranked by proximity to the incident, not asserted as its explanation. Global
/// rule 3: the tool never asserts causation, only proximity and coincidence — so this type's
/// name and its members deliberately avoid the vocabulary that would claim otherwise, and
/// <see cref="Label"/> is the only wording that ever reaches a rendered document. "changed
/// nearby" is a fact this tool can support; a stronger claim would not be.
/// </summary>
public sealed record Suspect
{
    public required TimelineEvent Event { get; init; }

    public required double Score { get; init; }

    public string Label => "changed nearby";
}
