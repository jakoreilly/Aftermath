namespace Aftermath.Rendering;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Applied at the single output boundary (hard constraint 2): every event is redacted before
/// rendering, and the fully rendered document is redacted once more as a final safety net.
/// There is no way to disable this and no flag anywhere in this tool that would let one.
///
/// Order matters (§5.1): pseudonymise first, so correlation survives, then redact everything
/// else. The hash is SHA-256(salt + value) truncated to 4 hex chars, with one random salt per
/// <see cref="Redactor"/> instance (one per run), so an identifier reads identically twice in
/// one document and differently across two runs.
/// </summary>
public sealed partial class Redactor
{
    private readonly byte[] salt;

    public Redactor(byte[]? salt = null) => this.salt = salt ?? RandomNumberGenerator.GetBytes(16);

    [GeneratedRegex(@"PT-Account-(?<id>\d+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PtAccountPattern();

    [GeneratedRegex(@"OopCustomer-(?<id>\d+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OopCustomerPattern();

    [GeneratedRegex(@"\b(password|pwd)\s*=\s*[^;""'\s]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PasswordPattern();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bdbx_[A-Za-z0-9]{8,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DbExplorerTokenPattern();

    // The table's own catch-all TLD (\.[\w.-]+) matches provenance strings this tool builds
    // itself, like "core-service@v1.14.0" or "core-service@1.15.0" — a release tag, not an
    // email. Every real TLD is alphabetic, so requiring that for the final label keeps every
    // genuine email address while excluding a version number that happens to follow an "@".
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[A-Za-z]{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b\d{2,3}-[A-Z]{1,2}-\d{1,6}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VrmPattern();

    // Deliberately no card-prefix requirement — test PANs and non-Visa/Mastercard schemes
    // would slip through one. Luhn (RedactPans, below) is what keeps this from also eating
    // order references, transaction ids and epoch timestamps.
    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PanCandidatePattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IbanPattern();

    // LedgerLogPrefixMiddleware falls back to a GUID only when Pz-Authorisation is
    // absent — a random correlation id, not attacker-controlled data, so it is left visible.
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareGuidPattern();

    public string Hash4(string value)
    {
        byte[] input = [.. this.salt, .. Encoding.UTF8.GetBytes(value)];
        return Convert.ToHexString(SHA256.HashData(input))[..4].ToLowerInvariant();
    }

    /// <summary>
    /// Pseudonymises <see cref="TimelineEvent.CorrelationPrefix"/> first, then generic-redacts
    /// Summary/Detail/Caveat. GOTCHA (§3.1/§5.1): a fallback-parsed event's CorrelationPrefix
    /// IS the raw leading bracket segment (Logs/FallbackLogParser.cs) — its role is unknown, so
    /// it is blanked wholesale rather than pseudonymised. Losing a correlation field is an
    /// acceptable cost; emitting a live session token because the fallback parser could not
    /// name the field is not.
    /// </summary>
    public TimelineEvent RedactEvent(TimelineEvent e)
    {
        const string fallbackMarker = "log pattern not fully recognised";
        bool fallbackParsed = e.Caveat?.Contains(fallbackMarker, StringComparison.Ordinal) == true;

        string? caveat = e.Caveat is null ? null : this.Apply(e.Caveat);
        string? correlation;

        if (fallbackParsed && e.CorrelationPrefix is not null)
        {
            correlation = "[log prefix redacted]";
            const string wholesale = "log prefix redacted wholesale — pattern not recognised";
            caveat = caveat is null ? wholesale : $"{caveat}; {wholesale}";
        }
        else
        {
            correlation = this.PseudonymiseCorrelation(e.CorrelationPrefix);
        }

        return e with
        {
            Summary = this.Apply(e.Summary),
            Detail = e.Detail is null ? null : this.Apply(e.Detail),
            Caveat = caveat,
            CorrelationPrefix = correlation,
        };
    }

    /// <summary>The single output boundary itself (constraint 2's anchor): every generic
    /// pattern, applied in table order. Safe to call twice — every replacement is stable under
    /// re-application, so calling this once more over the fully rendered document is a
    /// harmless final safety net rather than a source of double-redaction artefacts.</summary>
    public string Apply(string text)
    {
        string s = this.RedactAccountIds(text);
        s = PasswordPattern().Replace(s, m => $"{m.Groups[1].Value}=[REDACTED]");
        s = BearerPattern().Replace(s, "Bearer [REDACTED]");
        s = DbExplorerTokenPattern().Replace(s, "[REDACTED]");
        s = EmailPattern().Replace(s, "[EMAIL]");
        s = VrmPattern().Replace(s, "[VRM]");
        s = RedactPans(s);
        s = IbanPattern().Replace(s, "[IBAN]");
        return s;
    }

    /// <summary>Every %property{…} capture is a correlation field, redacted by ROLE not by
    /// name (hard constraint 3/4) — "not a bare GUID" is the only exemption.</summary>
    private string? PseudonymiseCorrelation(string? correlation)
    {
        if (string.IsNullOrEmpty(correlation) || BareGuidPattern().IsMatch(correlation))
        {
            return correlation;
        }

        return PtAccountPattern().IsMatch(correlation) || OopCustomerPattern().IsMatch(correlation)
            ? this.RedactAccountIds(correlation)
            : $"[session-#{this.Hash4(correlation)}]";
    }

    /// <summary>Account ids are load-bearing (constraint 4) — hash the digits, keep the label,
    /// so grouping survives instead of collapsing every account onto one token.</summary>
    private string RedactAccountIds(string text)
    {
        string s = PtAccountPattern().Replace(text, m => $"PT-Account-#{this.Hash4(m.Groups["id"].Value)}");
        return OopCustomerPattern().Replace(s, m => $"OopCustomer-#{this.Hash4(m.Groups["id"].Value)}");
    }

    private static string RedactPans(string text) =>
        PanCandidatePattern().Replace(text, m =>
        {
            string digitsOnly = string.Concat(m.Value.Where(char.IsDigit));
            return digitsOnly.Length is >= 13 and <= 19 && IsLuhnValid(digitsOnly) ? "[PAN]" : m.Value;
        });

    private static bool IsLuhnValid(string digits)
    {
        int sum = 0;
        bool doubleThisDigit = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleThisDigit)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleThisDigit = !doubleThisDigit;
        }

        return sum % 10 == 0;
    }
}
