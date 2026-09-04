namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Rendering;

public sealed class RedactorTests
{
    private const string LuhnValidVisa = "4111111111111111";

    private static TimelineEvent Ev(string summary, string? correlation = null, string? caveat = null, string? detail = null) => new()
    {
        At = DateTimeOffset.UtcNow,
        Kind = EventKind.LogError,
        Confidence = Confidence.Observed,
        Service = "svc",
        Summary = summary,
        Provenance = "svc.log:1",
        CorrelationPrefix = correlation,
        Caveat = caveat,
        Detail = detail,
    };

    [Fact]
    public void Apply_strips_password_bearer_email_pan_vrm_and_account_ids()
    {
        var redactor = new Redactor();
        string text = "password=hunter22; Authorization: Bearer abcDEF1234567; contact ops@Acme.ie; "
            + $"card {LuhnValidVisa}; reg 12-D-3456; PT-Account-8842";

        string redacted = redactor.Apply(text);

        Assert.DoesNotContain("hunter22", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcDEF1234567", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ops@Acme.ie", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(LuhnValidVisa, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("12-D-3456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("PT-Account-8842", redacted, StringComparison.Ordinal);
        Assert.Contains("[PAN]", redacted, StringComparison.Ordinal);
        Assert.Contains("[VRM]", redacted, StringComparison.Ordinal);
        Assert.Contains("[EMAIL]", redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer [REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("PT-Account-#", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Provenance_shaped_like_service_at_version_is_not_mistaken_for_an_email()
    {
        // Found running `draft` against the real workspace: "core-service@v1.14.0" — a git
        // release tag, exactly the shape TimelineEvent.Provenance uses for a Release event —
        // matched the table's own catch-all email TLD (\.[\w.-]+) and was replaced with
        // [EMAIL], corrupting every release's "Evidence" column in the rendered document.
        var redactor = new Redactor();

        Assert.Equal("core-service@v1.14.0", redactor.Apply("core-service@v1.14.0"));
        Assert.Equal("core-service@1.15.0", redactor.Apply("core-service@1.15.0"));
    }

    [Fact]
    public void Luhn_invalid_digit_runs_are_left_alone()
    {
        // A transaction id / order reference: 16 digits, but fails the Luhn check.
        var redactor = new Redactor();
        string redacted = redactor.Apply("order reference 1234567890123456");

        Assert.Contains("1234567890123456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("[PAN]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_account_id_hashes_identically_twice_in_one_run_and_differently_in_the_next()
    {
        var run1 = new Redactor();
        string first = run1.Apply("PT-Account-8842");
        string second = run1.Apply("PT-Account-8842");
        Assert.Equal(first, second);

        var run2 = new Redactor();
        string thirdRun = run2.Apply("PT-Account-8842");
        Assert.NotEqual(first, thirdRun);
    }

    [Fact]
    public void Bare_guid_correlation_is_left_untouched()
    {
        var redactor = new Redactor();
        const string guid = "3f9a1c2b-8d4e-4a11-9c3d-7e2f6b1a0d55";

        TimelineEvent redacted = redactor.RedactEvent(Ev("something happened", correlation: guid));

        Assert.Equal(guid, redacted.CorrelationPrefix);
    }

    [Fact]
    public void Opaque_non_guid_correlation_never_survives_redaction()
    {
        // The session-token test that matters most: a live Pz-Authorisation-derived token.
        var redactor = new Redactor();
        const string opaqueToken = "eyJhbGciOiJIUzI1NiJ9.abcdef";

        TimelineEvent redacted = redactor.RedactEvent(Ev("boom", correlation: opaqueToken));

        Assert.DoesNotContain(opaqueToken, redacted.CorrelationPrefix ?? string.Empty, StringComparison.Ordinal);
        Assert.StartsWith("[session-#", redacted.CorrelationPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_parsed_events_have_their_correlation_field_wholesale_redacted()
    {
        var redactor = new Redactor();
        const string opaqueToken = "eyJhbGciOiJIUzI1NiJ9.abcdef";
        TimelineEvent fallbackEvent = Ev(
            "boom",
            correlation: $"[{opaqueToken}] [   9]", // FallbackLogParser's LeadingBracketSegment shape
            caveat: "log pattern not fully recognised");

        TimelineEvent redacted = redactor.RedactEvent(fallbackEvent);

        Assert.DoesNotContain(opaqueToken, redacted.CorrelationPrefix ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("[log prefix redacted]", redacted.CorrelationPrefix);
        Assert.Contains("log prefix redacted wholesale — pattern not recognised", redacted.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactEvent_also_scrubs_summary_and_detail()
    {
        var redactor = new Redactor();
        TimelineEvent redacted = redactor.RedactEvent(Ev(
            $"card {LuhnValidVisa} declined",
            detail: "contact ops@Acme.ie"));

        Assert.DoesNotContain(LuhnValidVisa, redacted.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ops@Acme.ie", redacted.Detail, StringComparison.Ordinal);
    }
}
