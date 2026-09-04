namespace Aftermath.Logs;

/// <summary>
/// One log line, decomposed by whichever matcher won: a compiled log4net pattern or
/// <see cref="FallbackLogParser"/>. Every field but <see cref="Message"/> is optional — the
/// compiler must treat every capture as optional (Phase 3 GOTCHA: gdpr-service carries no
/// trace_id at all, micro-mobility's file appender carries no logger).
/// </summary>
public sealed record LogLineFields
{
    /// <summary>"HH:mm:ss.fff" — present when the pattern used %date{HH:mm:ss.fff}. The date
    /// itself comes from the filename, never from this field.</summary>
    public string? TimeOfDay { get; init; }

    /// <summary>The full local "yyyy-MM-dd HH:mm:ss,fff" — present only for the two patterns
    /// that use bare %date. These do not need the filename-date/rollover machinery at all.</summary>
    public string? FullLocalDateTime { get; init; }

    /// <summary>The %property{…} value, whatever the property was named (AcmeLogPrefix in
    /// 68 patterns, SessionID in 18). Matched generically — see hard constraint 3: this is a
    /// live session token on web services and MUST be pseudonymised, never redacted by name.</summary>
    public string? Correlation { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Thread { get; init; }

    public string? Level { get; init; }

    public string? Logger { get; init; }

    /// <summary>Set only by <see cref="FallbackLogParser"/>: the raw text of every leading
    /// bracketed group, unparsed. Its role (thread id? session token? both concatenated?) is
    /// unknown, so Phase 5's Redactor must blank this entire segment rather than pseudonymise
    /// it — pseudonymising something that might not be a correlation id at all would be a
    /// guess dressed up as data.</summary>
    public string? LeadingBracketSegment { get; init; }

    public required string Message { get; init; }
}
