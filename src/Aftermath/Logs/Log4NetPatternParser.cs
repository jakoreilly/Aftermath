namespace Aftermath.Logs;

using System.Text.RegularExpressions;

/// <summary>
/// Compiles one log4net conversionPattern string into a matcher, without ever building a
/// regex from runtime data (hard constraint 8 forbids <c>new Regex(…)</c> everywhere in this
/// tool, including here). Instead each of the nine supported directives gets its own
/// compile-time <see cref="GeneratedRegex"/> fragment; <see cref="Compile"/> walks the
/// pattern text and assembles an ordered list of literal-text and directive segments that
/// <see cref="CompiledLogPattern.TryMatch"/> replays against a log line, matching literals by
/// plain string comparison and directives by anchoring the matching fragment at the current
/// position with <c>\G</c>.
///
/// <see cref="Compile"/> is deliberately split into <see cref="Tokenize"/>,
/// <see cref="TranslateToken"/> and <see cref="Assemble"/> — three named private methods — to
/// stay under Sonar S3776 cognitive complexity. Do not inline them back.
/// </summary>
public static partial class Log4NetPatternParser
{
    // %date{HH:mm:ss.fff}, %property{AcmeLogPrefix}, %4thread, %-5level, %logger{3} …
    // The pattern text is a log4net conversionPattern, not attacker input, but every Regex in
    // this tool still carries the S6444 timeout regardless of source.
    [GeneratedRegex(@"%(?<flags>-?\d*)(?<name>[A-Za-z]+)(\{(?<arg>[^}]*)\})?",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Directive();

    [GeneratedRegex(@"\G(?<time>\d{2}:\d{2}:\d{2}\.\d{3})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TimeToken();

    [GeneratedRegex(@"\G(?<date>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullDateToken();

    // %property{NAME} — matched generically regardless of NAME. Hard constraint 3/4: redact
    // by role, never by name.
    [GeneratedRegex(@"\G(?<correlation>[^\]]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CorrelationToken();

    [GeneratedRegex(@"\G(?<trace_id>[0-9a-fA-F]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TraceIdToken();

    [GeneratedRegex(@"\G(?<span_id>[0-9a-fA-F]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpanIdToken();

    [GeneratedRegex(@"\G\s*(?<thread>\d+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThreadToken();

    [GeneratedRegex(@"\G(?<level>[A-Z]+)\s*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LevelToken();

    [GeneratedRegex(@"\G(?<logger>\S+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LoggerToken();

    [GeneratedRegex(@"\G(?<message>.*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MessageToken();

    /// <summary>
    /// Returns null when the pattern uses a directive outside the supported token table — the
    /// caller must fall back to <see cref="FallbackLogParser"/> in that case, with
    /// <c>Confidence.Inferred</c> and the caveat "log pattern not fully recognised".
    /// </summary>
    public static CompiledLogPattern? Compile(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        IReadOnlyList<RawToken> raw = Tokenize(pattern);
        var segments = new List<Segment>(raw.Count);
        foreach (RawToken token in raw)
        {
            Segment? segment = TranslateToken(token);
            if (segment is null)
            {
                return null;
            }

            segments.Add(segment);
        }

        return Assemble(pattern, segments);
    }

    /// <summary>Splits the raw pattern text into literal runs and %directive tokens.</summary>
    private static IReadOnlyList<RawToken> Tokenize(string pattern)
    {
        var tokens = new List<RawToken>();
        int last = 0;

        foreach (Match m in Directive().Matches(pattern))
        {
            if (m.Index > last)
            {
                tokens.Add(RawToken.OfLiteral(pattern[last..m.Index]));
            }

            tokens.Add(RawToken.OfDirective(m.Groups["name"].Value, NullIfEmpty(m.Groups["arg"])));
            last = m.Index + m.Length;
        }

        if (last < pattern.Length)
        {
            tokens.Add(RawToken.OfLiteral(pattern[last..]));
        }

        return tokens;
    }

    /// <summary>
    /// Maps one raw token to a segment, or null when the directive is not in the supported
    /// table (§3.1). %newline is supported but contributes nothing — it is dropped as an
    /// empty literal rather than treated as unsupported.
    /// </summary>
    private static Segment? TranslateToken(RawToken token)
    {
        if (!token.IsDirective)
        {
            // GOTCHA: %-5level renders left-justified — "INFO " (4 chars + 1 pad) vs "ERROR"
            // (already 5) — so the run of whitespace actually separating it from the next
            // token varies at runtime, while the pattern text between %-5level and the next
            // directive is always exactly one literal space. LevelToken's own trailing \s*
            // absorbs that padding, so a purely-whitespace literal segment must not ALSO
            // demand an exact character count here — it is matched as "skip any whitespace",
            // never as a fixed string. There is nowhere else in the estate's 22 patterns where
            // a literal segment is pure whitespace, so this affects only the level/logger gap.
            return IsAllWhitespace(token.Text!)
                ? new Segment(SegmentKind.Whitespace)
                : new Segment(SegmentKind.Literal, token.Text);
        }

        return token.Name!.ToLowerInvariant() switch
        {
            "date" when string.Equals(token.Arg, "HH:mm:ss.fff", StringComparison.Ordinal) => new Segment(SegmentKind.Time),
            "date" when token.Arg is null => new Segment(SegmentKind.FullDate),
            "property" when token.Arg is not null => new Segment(SegmentKind.Correlation),
            "x" when string.Equals(token.Arg, "trace_id", StringComparison.Ordinal) => new Segment(SegmentKind.TraceId),
            "x" when string.Equals(token.Arg, "span_id", StringComparison.Ordinal) => new Segment(SegmentKind.SpanId),
            "thread" => new Segment(SegmentKind.Thread),
            "level" => new Segment(SegmentKind.Level),
            "logger" => new Segment(SegmentKind.Logger),
            "message" => new Segment(SegmentKind.Message),
            "newline" => new Segment(SegmentKind.Literal, string.Empty),
            _ => null,
        };
    }

    private static CompiledLogPattern Assemble(string rawPattern, IReadOnlyList<Segment> segments) =>
        new(rawPattern, segments);

    private static string? NullIfEmpty(Group group) => group.Success && group.Length > 0 ? group.Value : null;

    private static bool IsAllWhitespace(string text) => text.Length > 0 && text.All(char.IsWhiteSpace);

    /// <summary>One literal run or one recognised %directive from the raw pattern text.</summary>
    private sealed record RawToken
    {
        public required bool IsDirective { get; init; }

        public string? Text { get; init; }

        public string? Name { get; init; }

        public string? Arg { get; init; }

        public static RawToken OfLiteral(string text) => new() { IsDirective = false, Text = text };

        public static RawToken OfDirective(string name, string? arg) => new() { IsDirective = true, Name = name, Arg = arg };
    }

    internal enum SegmentKind { Literal, Whitespace, Time, FullDate, Correlation, TraceId, SpanId, Thread, Level, Logger, Message }

    // Internal rather than private: CompiledLogPattern's constructor takes a list of these,
    // and a constructor's accessibility may not exceed that of the types in its signature.
    internal sealed record Segment(SegmentKind Kind, string? Literal = null);

    /// <summary>
    /// A pattern compiled to an ordered list of segments. Matching replays the segments against
    /// one line: literal segments are compared by substring equality, directive segments by the
    /// matching <c>\G</c>-anchored fragment from <see cref="Log4NetPatternParser"/>. Every
    /// capture is optional by construction — a pattern that never carried %X{trace_id} simply
    /// has no Correlation-less… no TraceId segment, and <see cref="TryMatch"/> leaves that field
    /// null rather than requiring it.
    /// </summary>
    public sealed class CompiledLogPattern
    {
        private readonly IReadOnlyList<Segment> segments;

        internal CompiledLogPattern(string rawPattern, IReadOnlyList<Segment> segments)
        {
            this.RawPattern = rawPattern;
            this.segments = segments;
            this.HasTraceId = segments.Any(s => s.Kind == SegmentKind.TraceId);
            this.HasSpanId = segments.Any(s => s.Kind == SegmentKind.SpanId);
            this.HasLogger = segments.Any(s => s.Kind == SegmentKind.Logger);
            this.HasCorrelation = segments.Any(s => s.Kind == SegmentKind.Correlation);
        }

        /// <summary>The conversionPattern this was compiled from — carried into the winning
        /// event's Caveat when a service had more than one candidate pattern.</summary>
        public string RawPattern { get; }

        public bool HasTraceId { get; }

        public bool HasSpanId { get; }

        public bool HasLogger { get; }

        public bool HasCorrelation { get; }

        public bool TryMatch(string line, out LogLineFields fields)
        {
            int pos = 0;
            string? time = null, fullDate = null, correlation = null, traceId = null, spanId = null;
            string? thread = null, level = null, logger = null, message = null;

            foreach (Segment seg in this.segments)
            {
                if (seg.Kind == SegmentKind.Literal)
                {
                    string lit = seg.Literal!;
                    if (pos + lit.Length > line.Length || string.CompareOrdinal(line, pos, lit, 0, lit.Length) != 0)
                    {
                        fields = null!;
                        return false;
                    }

                    pos += lit.Length;
                    continue;
                }

                if (seg.Kind == SegmentKind.Whitespace)
                {
                    while (pos < line.Length && char.IsWhiteSpace(line[pos]))
                    {
                        pos++;
                    }

                    continue;
                }

                Match m = MatchDirective(seg.Kind, line, pos);
                if (!m.Success)
                {
                    fields = null!;
                    return false;
                }

                switch (seg.Kind)
                {
                    case SegmentKind.Time: time = m.Groups["time"].Value; break;
                    case SegmentKind.FullDate: fullDate = m.Groups["date"].Value; break;
                    case SegmentKind.Correlation: correlation = m.Groups["correlation"].Value; break;
                    case SegmentKind.TraceId: traceId = m.Groups["trace_id"].Value; break;
                    case SegmentKind.SpanId: spanId = m.Groups["span_id"].Value; break;
                    case SegmentKind.Thread: thread = m.Groups["thread"].Value; break;
                    case SegmentKind.Level: level = m.Groups["level"].Value; break;
                    case SegmentKind.Logger: logger = m.Groups["logger"].Value; break;
                    case SegmentKind.Message: message = m.Groups["message"].Value; break;
                }

                pos += m.Length;
            }

            if (message is null)
            {
                fields = null!;
                return false;
            }

            fields = new LogLineFields
            {
                TimeOfDay = time,
                FullLocalDateTime = fullDate,
                Correlation = NullIfEmptyString(correlation),
                TraceId = NullIfEmptyString(traceId),
                SpanId = NullIfEmptyString(spanId),
                Thread = thread,
                Level = level,
                Logger = logger,
                Message = message,
            };
            return true;
        }

        private static string? NullIfEmptyString(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private static Match MatchDirective(SegmentKind kind, string line, int pos) => kind switch
        {
            SegmentKind.Time => TimeToken().Match(line, pos),
            SegmentKind.FullDate => FullDateToken().Match(line, pos),
            SegmentKind.Correlation => CorrelationToken().Match(line, pos),
            SegmentKind.TraceId => TraceIdToken().Match(line, pos),
            SegmentKind.SpanId => SpanIdToken().Match(line, pos),
            SegmentKind.Thread => ThreadToken().Match(line, pos),
            SegmentKind.Level => LevelToken().Match(line, pos),
            SegmentKind.Logger => LoggerToken().Match(line, pos),
            SegmentKind.Message => MessageToken().Match(line, pos),
            _ => throw new InvalidOperationException($"Unreachable segment kind '{kind}'."),
        };
    }
}
