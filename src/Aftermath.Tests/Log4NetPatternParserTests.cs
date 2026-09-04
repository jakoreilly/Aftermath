namespace Aftermath.Tests;

using Aftermath.Logs;

public sealed class Log4NetPatternParserTests
{
    /// <summary>
    /// The 22 distinct conversionPattern strings the real workspace's own ServiceManifest scan
    /// reports (verified 2026-09-04 via `Aftermath services --workspace
    /// c:\workspace\work`, union of every manifest's LogPatterns). This is more accurate than
    /// the Phase 0 shell grep, which undercounts at 21: it cannot see a &lt;conversionPattern&gt;
    /// whose value attribute is split across two lines (api-gateway2's Release syslog appender),
    /// and it counts two extra patterns from `service/Acme.FrontGate.Service`, a directory
    /// with no .gitlab-ci.yml that the scanner correctly does not treat as a manifested service.
    /// See the Phase 3 correction note in plan.md.
    /// </summary>
    public static readonly string[] EstatePatterns =
    [
        "%date %level %logger - %message%newline",
        "%date [%thread] %level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] %-5level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{AcmeLogPrefix}] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message",
        "%date{HH:mm:ss.fff} [%4thread] [%property{AcmeLogPrefix}] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{AcmeLogPrefix}] %-5level %logger{3} - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{SessionID}] %-3level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{SessionID}] %-5level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{SessionID}] %-5level %logger [tId: %X{trace_id}/sId: %X{span_id}] - %message%newline",
        "%date{HH:mm:ss.fff} [%4thread] [%property{SessionID}] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %X{trace_id} spanId: %X{span_id} - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger %X{trace_id} spanId: %X{span_id} - %message",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger - %message",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] [tId: %X{trace_id}/sId: %X{span_id}] %-5level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [tId: %X{trace_id}/sId: %X{span_id}] [%4thread] %-5level %logger  - %message%newline",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [tId: %X{trace_id}/sId: %X{span_id}] [%4thread] %-5level %logger - %message",
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [tId: %X{trace_id}/sId: %X{span_id}] [%4thread] %-5level %logger - %message%newline",
        "%date{HH:mm:ss.fff} [%property{SessionID}] [%4thread] %-5level %logger [tId: %X{trace_id}/sId: %X{span_id}] - %message%newline",
        "%date{HH:mm:ss.fff} [%thread] %-5level %logger [tId: %X{trace_id}/sId: %X{span_id}] - %message%newline",
    ];

    /// <summary>api-gateway2's FILE appender (log4net.Release.config:14-15) — no %newline.</summary>
    private const string ApiGateway2FilePattern =
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message";

    /// <summary>The SAME service's SYSLOG appender (…:20-21) — thread and property swapped.</summary>
    private const string ApiGateway2SyslogPattern =
        "%date{HH:mm:ss.fff} [%4thread] [%property{AcmeLogPrefix}] %-5level %logger traceId: %X{trace_id} spanId: %X{span_id} - %message%newline";

    /// <summary>gdpr-service: no OpenTelemetry at all, so no %X{trace_id}/%X{span_id}.</summary>
    private const string GdprServicePattern =
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger - %message%newline";

    /// <summary>micro-mobility's FILE appender: %logger accidentally replaced by %X{trace_id}.</summary>
    private const string MicroMobilityFilePattern =
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %X{trace_id} spanId: %X{span_id} - %message%newline";

    [Theory]
    [MemberData(nameof(AllEstatePatterns))]
    public void Every_estate_pattern_compiles(string pattern)
    {
        Log4NetPatternParser.CompiledLogPattern? compiled = Log4NetPatternParser.Compile(pattern);

        Assert.NotNull(compiled);
        Assert.Equal(pattern, compiled!.RawPattern);
    }

    public static IEnumerable<object[]> AllEstatePatterns() => EstatePatterns.Select(p => new object[] { p });

    [Fact]
    public void Falls_back_when_a_directive_is_not_in_the_supported_table()
    {
        // No pattern in the real estate needs this path (all 22 use only supported
        // directives), so this is a contrived pattern proving the escape hatch itself works.
        Log4NetPatternParser.CompiledLogPattern? compiled =
            Log4NetPatternParser.Compile("%date{HH:mm:ss.fff} [%aspnet-request] %-5level - %message%newline");

        Assert.Null(compiled);
    }

    [Fact]
    public void Compiled_pattern_extracts_every_field_by_role()
    {
        Log4NetPatternParser.CompiledLogPattern compiled =
            Log4NetPatternParser.Compile(EstatePatterns[15])!; // AcmeLogPrefix + traceId + spanId + newline

        bool ok = compiled.TryMatch(
            "14:32:07.123 [sess-9f8a] [0012] INFO  Acme.Worker traceId: abc123 spanId: def456 - order created",
            out LogLineFields fields);

        Assert.True(ok);
        Assert.Equal("14:32:07.123", fields.TimeOfDay);
        Assert.Equal("sess-9f8a", fields.Correlation);
        Assert.Equal("0012", fields.Thread);
        Assert.Equal("INFO", fields.Level);
        Assert.Equal("Acme.Worker", fields.Logger);
        Assert.Equal("abc123", fields.TraceId);
        Assert.Equal("def456", fields.SpanId);
        Assert.Equal("order created", fields.Message);
    }

    [Fact]
    public void Correlation_field_is_captured_regardless_of_whether_it_is_named_SessionID()
    {
        // Hard constraint 3/4: match %property{…} generically. SessionID (18 uses) must be
        // captured exactly like AcmeLogPrefix (68 uses) — both carry a session identifier.
        Log4NetPatternParser.CompiledLogPattern compiled =
            Log4NetPatternParser.Compile("%date{HH:mm:ss.fff} [%4thread] [%property{SessionID}] %-5level %logger - %message%newline")!;

        bool ok = compiled.TryMatch("09:00:00.000 [0003] [sid-77] INFO  SomeLogger - hello", out LogLineFields fields);

        Assert.True(ok);
        Assert.Equal("sid-77", fields.Correlation);
    }

    [Fact]
    public void Api_gateway2_GOTCHA_the_file_pattern_matches_a_file_formatted_line()
    {
        Log4NetPatternParser.CompiledLogPattern file = Log4NetPatternParser.Compile(ApiGateway2FilePattern)!;

        bool ok = file.TryMatch(
            "10:00:00.000 [sess-token-abc] [0007] INFO  SomeLogger traceId: abcd spanId: ef01 - message text",
            out LogLineFields fields);

        Assert.True(ok);
        Assert.Equal("sess-token-abc", fields.Correlation);
        Assert.Equal("0007", fields.Thread);
    }

    [Fact]
    public void Api_gateway2_GOTCHA_the_syslog_pattern_does_not_silently_match_a_file_formatted_line()
    {
        // The two appenders order [%4thread] and [%property{…}] differently. Blindly picking
        // LogPatterns[0] for every file risks assigning the session token to the thread field —
        // a redaction failure, not a parsing one. The candidate-set design must be able to tell
        // these apart: the wrong-order pattern must fail on a line the other pattern accepts.
        Log4NetPatternParser.CompiledLogPattern syslog = Log4NetPatternParser.Compile(ApiGateway2SyslogPattern)!;

        bool ok = syslog.TryMatch(
            "10:00:00.000 [sess-token-abc] [0007] INFO  SomeLogger traceId: abcd spanId: ef01 - message text",
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void Api_gateway2_GOTCHA_each_pattern_matches_its_own_appenders_line()
    {
        Log4NetPatternParser.CompiledLogPattern syslog = Log4NetPatternParser.Compile(ApiGateway2SyslogPattern)!;

        bool ok = syslog.TryMatch(
            "10:00:00.000 [0007] [sess-token-abc] INFO  SomeLogger traceId: abcd spanId: ef01 - message text",
            out LogLineFields fields);

        Assert.True(ok);
        Assert.Equal("0007", fields.Thread);
        Assert.Equal("sess-token-abc", fields.Correlation);
    }

    [Fact]
    public void GdprService_pattern_yields_an_event_with_no_trace_id_rather_than_zero_events()
    {
        Log4NetPatternParser.CompiledLogPattern compiled = Log4NetPatternParser.Compile(GdprServicePattern)!;

        Assert.False(compiled.HasTraceId);
        bool ok = compiled.TryMatch("11:00:00.000 [PT-Account-42] [0001] WARN  DataDeletionService - purge started", out LogLineFields fields);

        Assert.True(ok);
        Assert.Null(fields.TraceId);
        Assert.Null(fields.SpanId);
        Assert.Equal("purge started", fields.Message);
    }

    [Fact]
    public void MicroMobility_file_pattern_yields_an_event_with_no_logger_rather_than_zero_events()
    {
        Log4NetPatternParser.CompiledLogPattern compiled = Log4NetPatternParser.Compile(MicroMobilityFilePattern)!;

        Assert.False(compiled.HasLogger);
        bool ok = compiled.TryMatch("12:00:00.000 [sess-1] [0004] ERROR abc123 spanId: def456 - scooter offline", out LogLineFields fields);

        Assert.True(ok);
        Assert.Null(fields.Logger);
        Assert.Equal("abc123", fields.TraceId);
        Assert.Equal("scooter offline", fields.Message);
    }

    [Fact]
    public void Fallback_parser_captures_the_leading_bracket_segment_whole()
    {
        bool ok = FallbackLogParser.TryMatch(
            "13:45:00.000 [sess-opaque-token] [0009] ERROR something went wrong",
            out LogLineFields fields);

        Assert.True(ok);
        Assert.Equal("13:45:00.000", fields.TimeOfDay);
        Assert.Equal("[sess-opaque-token] [0009]", fields.LeadingBracketSegment);
        Assert.Equal("ERROR", fields.Level);
        Assert.Equal("something went wrong", fields.Message);
    }

    [Fact]
    public void Fallback_parser_rejects_a_line_with_no_leading_time()
    {
        bool ok = FallbackLogParser.TryMatch("not a log line at all", out _);

        Assert.False(ok);
    }
}
