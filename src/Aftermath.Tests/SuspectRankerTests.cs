namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Sources;

public sealed class SuspectRankerTests
{
    private static readonly DateTimeOffset IncidentAt = new(2026, 7, 17, 13, 0, 0, TimeSpan.Zero);

    private static readonly IncidentWindow Window = new()
    {
        AtUtc = IncidentAt,
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    private static TimelineEvent Ev(DateTimeOffset at, string service, EventKind kind, string summary = "x") => new()
    {
        At = at,
        Kind = kind,
        Confidence = Confidence.Observed,
        Service = service,
        Summary = summary,
        Provenance = $"{service}@{at:O}#{Guid.NewGuid()}",
    };

    private static Timeline Build(params TimelineEvent[] events) =>
        TimelineBuilder.Build([SourceResult.Ok("test", events, "test")], Window);

    [Fact]
    public void A_deploy_30_minutes_before_outscores_a_release_20_hours_before()
    {
        TimelineEvent deploy = Ev(IncidentAt.AddMinutes(-30), "core-service", EventKind.Deploy);
        TimelineEvent release = Ev(IncidentAt.AddHours(-20), "core-service", EventKind.Release);

        IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(Build(deploy, release));

        double deployScore = suspects.Single(s => s.Event.Kind == EventKind.Deploy).Score;
        double releaseScore = suspects.Single(s => s.Event.Kind == EventKind.Release).Score;
        Assert.True(deployScore > releaseScore);
    }

    [Fact]
    public void A_commit_never_outscores_a_same_distance_release()
    {
        DateTimeOffset at = IncidentAt.AddHours(-1);
        TimelineEvent commit = Ev(at, "core-service", EventKind.Commit);
        TimelineEvent release = Ev(at, "billing-service", EventKind.Release);

        IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(Build(commit, release));

        double commitScore = suspects.Single(s => s.Event.Kind == EventKind.Commit).Score;
        double releaseScore = suspects.Single(s => s.Event.Kind == EventKind.Release).Score;
        Assert.True(commitScore < releaseScore);
    }

    [Fact]
    public void Blast_radius_lifts_a_score_when_three_other_services_error()
    {
        DateTimeOffset at = IncidentAt.AddMinutes(-30);
        TimelineEvent isolatedRelease = Ev(at, "core-service", EventKind.Release);
        double isolatedScore = SuspectRanker.Rank(Build(isolatedRelease)).Single().Score;

        TimelineEvent releaseWithBlastRadius = Ev(at, "core-service", EventKind.Release);
        TimelineEvent[] otherErrors =
        [
            Ev(IncidentAt.AddMinutes(-10), "accountservice", EventKind.LogError, "e1"),
            Ev(IncidentAt.AddMinutes(-9), "billing-service", EventKind.LogError, "e2"),
            Ev(IncidentAt.AddMinutes(-8), "micro-mobility", EventKind.LogError, "e3"),
        ];
        Timeline withErrors = Build([releaseWithBlastRadius, .. otherErrors]);
        double liftedScore = SuspectRanker.Rank(withErrors).Single(s => s.Event.Kind == EventKind.Release).Score;

        Assert.True(liftedScore > isolatedScore);
        Assert.Equal(isolatedScore * 1.75, liftedScore, precision: 6);
    }

    [Fact]
    public void An_error_in_the_same_service_does_not_count_toward_its_own_blast_radius()
    {
        DateTimeOffset at = IncidentAt.AddMinutes(-30);
        TimelineEvent release = Ev(at, "core-service", EventKind.Release);
        TimelineEvent ownError = Ev(IncidentAt.AddMinutes(-5), "core-service", EventKind.LogError, "self");

        Suspect suspect = SuspectRanker.Rank(Build(release, ownError)).Single(s => s.Event.Kind == EventKind.Release);

        // proximity(1.0) * kindWeight(Release, 0.8) * blastRadius(1.0, no OTHER service errored)
        Assert.Equal(0.8, suspect.Score, precision: 6);
    }

    [Fact]
    public void An_event_after_the_incident_scores_at_the_lowest_tier()
    {
        TimelineEvent after = Ev(IncidentAt.AddMinutes(30), "core-service", EventKind.Deploy);

        Suspect suspect = SuspectRanker.Rank(Build(after)).Single();

        Assert.Equal(0.1, suspect.Score, precision: 6); // proximity 0.1 * kindWeight 1.0 * blastRadius 1.0
    }

    [Fact]
    public void Suspects_are_ordered_by_score_descending()
    {
        TimelineEvent low = Ev(IncidentAt.AddHours(-20), "core-service", EventKind.Commit);
        TimelineEvent high = Ev(IncidentAt.AddMinutes(-10), "core-service", EventKind.Deploy);

        IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(Build(low, high));

        Assert.Equal(EventKind.Deploy, suspects[0].Event.Kind);
        Assert.Equal(EventKind.Commit, suspects[1].Event.Kind);
    }

    [Fact]
    public void Label_never_asserts_causation()
    {
        Suspect suspect = SuspectRanker.Rank(Build(Ev(IncidentAt.AddMinutes(-10), "core-service", EventKind.Deploy))).Single();

        Assert.Equal("changed nearby", suspect.Label);
    }

    [Fact]
    public void Non_change_events_are_not_ranked()
    {
        TimelineEvent error = Ev(IncidentAt.AddMinutes(-10), "core-service", EventKind.LogError, "boom");

        IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(Build(error));

        Assert.Empty(suspects);
    }
}
