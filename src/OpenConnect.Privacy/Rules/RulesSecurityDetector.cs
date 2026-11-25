using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Tokenization;

namespace OpenConnect.Privacy.Rules;

/// <summary>Represents a custom pattern rule for detecting sensitive content.</summary>
/// <param name="EntityType">The entity type label.</param>
/// <param name="Pattern">The regex pattern to match.</param>
public sealed record CustomDetectorRule(string EntityType, Regex Pattern);

/// <summary>Deterministic local rules detector for PII, secrets, and checksum-validated entities.</summary>
public sealed class RulesSecurityDetector : ISecurityDetector
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex EmailRegex = new(@"(?<![\p{L}\p{M}\p{N}._%+\-])[\p{L}\p{M}\p{N}._%+\-]+@[\p{L}\p{N}](?:[\p{L}\p{M}\p{N}\-]*[\p{L}\p{M}\p{N}])?(?:\.[\p{L}\p{N}](?:[\p{L}\p{M}\p{N}\-]*[\p{L}\p{M}\p{N}])?)+", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PhoneRegex = new(@"(?<![\w+])\+?(?:\d{1,3}[-.\s])?\(?\d{3}\)?[-.\s]\d{3}[-.\s]\d{4}\b", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex InternationalPhoneRegex = new(@"(?<![\w+])\+[1-9](?:[ .()\-]*[0-9]){7,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Ipv4Regex = new(@"(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Ipv6Regex = new(@"(?<![\w:])(?:[0-9a-f]{0,4}:){2,7}(?:(?:[0-9]{1,3}\.){3}[0-9]{1,3}|[0-9a-f]{0,4})(?![\w:.])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MacRegex = new(@"(?<![0-9a-f:])(?:[0-9a-f]{2}:){5}[0-9a-f]{2}(?![0-9a-f:])|(?<![0-9a-f-])(?:[0-9a-f]{2}-){5}[0-9a-f]{2}(?![0-9a-f-])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SocialSecurityNumberRegex = new(@"(?<![0-9])(?!(?:000|666|9[0-9]{2})-)[0-9]{3}-(?!00-)[0-9]{2}-(?!0000(?![0-9]))[0-9]{4}(?![0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PotentialCardRegex = new(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SecretRegex = new(@"(?i)\b(?:bearer\s+[a-z0-9_\-\.]{20,}|ghp_[a-z0-9]{36}|sk-[a-z0-9]{20,}|xox[baprs]-[0-9a-z]{10,}|-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PrivateKeyRegex = new(@"-----BEGIN (?<kind>(?:[A-Z]+ )?PRIVATE KEY)-----[\s\S]*?(?:-----END \k<kind>-----|\z)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AuthorizationHeaderRegex = new(@"(?im)\bauthorization[ \t]*:[ \t]*(?<value>[^\r\n]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LabeledSecretRegex = new(@"(?im)\b(?:password|passwd|passphrase|authorization|api[\s_\-]*key|client[\s_\-]*secret|access[\s_\-]*token|refresh[\s_\-]*token|(?:aws[\s_\-]*)?(?:secret[\s_\-]*(?:access[\s_\-]*)?key|access[\s_\-]*key[\s_\-]*id))""?[ \t]*[:=][ \t]*(?:""(?<value>(?:\\(?:[^\r\n]|(?=\r?$))|[^\\""\r\n])+)(?:""|(?=\r?$))|(?!"")(?<value>[^\r\n""}]+))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LabeledPiiRegex = new(@"(?im)\b(?:full[\s_\-]*name|first[\s_\-]*name|last[\s_\-]*name|given[\s_\-]*name|family[\s_\-]*name|date[\s_\-]*of[\s_\-]*birth|birth[\s_\-]*date|dob|social[\s_\-]*security[\s_\-]*number|ssn|national[\s_\-]*id|passport[\s_\-]*(?:number|no)|driv(?:er(?:'s)?|ing)[\s_\-]*licen[cs]e(?:[\s_\-]*(?:number|no))?|tax[\s_\-]*(?:identification|id)(?:[\s_\-]*(?:number|no))?|bank[\s_\-]*account(?:[\s_\-]*(?:number|no))?|medical[\s_\-]*record(?:[\s_\-]*(?:number|no))?|(?:ip|mac)[\s_\-]*address|device[\s_\-]*(?:id|identifier)|imei|latitude|longitude|street[\s_\-]*address|home[\s_\-]*address|(?:e[\s_\-]*mail|telephone|phone|mobile)(?:[\s_\-]*(?:address|number|no))?)""?[ \t]*[:=][ \t]*(?:""(?<value>(?:\\(?:[^\r\n]|(?=\r?$))|[^\\""\r\n])+)(?:""|(?=\r?$))|(?!"")(?<value>[^\r\n""}]+))", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PrivacyTokenRegex = new(@"\A\[OC_(?:[0-9A-F]{32}|EMAIL|PHONE|PAYMENT_CARD|PERSONAL_DATA|SECRET|OTHER)\]\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly TimeSpan DefaultTokenTtl = TimeSpan.FromMinutes(30);

    private readonly ITokenVault? _vault;
    private readonly IReadOnlyList<CustomDetectorRule> _customRules;
    private readonly TimeSpan _tokenTtl;

    /// <summary>Initializes a new instance of <see cref="RulesSecurityDetector"/>.</summary>
    /// <param name="vault">Optional token vault to tokenize detected entities.</param>
    /// <param name="customRules">Optional custom regex rules.</param>
    /// <param name="tokenTtl">Lifetime of tokens stored in the vault. Defaults to 30 minutes, matching <see cref="PrivacyOptions.DefaultTokenTtl"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokenTtl"/> is not positive.</exception>
    public RulesSecurityDetector(ITokenVault? vault = null, IReadOnlyList<CustomDetectorRule>? customRules = null, TimeSpan? tokenTtl = null)
    {
        var ttl = tokenTtl ?? DefaultTokenTtl;
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tokenTtl), "Token TTL must be positive.");
        _tokenTtl = ttl;
        _vault = vault;
        _customRules = customRules?.Select(rule =>
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(rule.Pattern);
            var timeout = rule.Pattern.MatchTimeout == Regex.InfiniteMatchTimeout || rule.Pattern.MatchTimeout > RegexTimeout
                ? RegexTimeout : rule.Pattern.MatchTimeout;
            return new CustomDetectorRule(rule.EntityType, new Regex(rule.Pattern.ToString(), rule.Pattern.Options, timeout));
        }).ToArray() ?? Array.Empty<CustomDetectorRule>();
    }

    /// <inheritdoc />
    public async ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Length == 0)
        {
            return new DetectorResult(true, string.Empty, Array.Empty<EntitySpan>());
        }
        var hasInvisibleSeparators = content.AsSpan().IndexOfAny("\u200b\u200c\u200d\u2060\ufeff".AsSpan()) >= 0;
        if (hasInvisibleSeparators && content.Length > 1_000_000)
            throw new InvalidOperationException("detector_limit_exceeded");

        var matches = new List<RawEntityMatch>();
        void AddMatch(RawEntityMatch match)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= 20000) throw new InvalidOperationException("detector_limit_exceeded");
            matches.Add(match);
        }

        // 1. Secrets
        foreach (Match match in PrivateKeyRegex.Matches(content))
        {
            AddMatch(new RawEntityMatch(match.Index, match.Length, "SECRET", match.Value));
        }
        foreach (Match match in SecretRegex.Matches(content))
        {
            AddMatch(new RawEntityMatch(match.Index, match.Length, "SECRET", match.Value));
        }
        foreach (Match match in AuthorizationHeaderRegex.Matches(content))
        {
            var value = match.Groups["value"];
            AddMatch(new RawEntityMatch(value.Index, value.Length, "SECRET", value.Value));
        }
        foreach (Match match in LabeledSecretRegex.Matches(content))
        {
            var value = match.Groups["value"];
            AddMatch(new RawEntityMatch(value.Index, value.Length, "SECRET", value.Value));
        }

        // 2. Email
        foreach (Match match in EmailRegex.Matches(content))
        {
            AddMatch(new RawEntityMatch(match.Index, match.Length, "EMAIL", match.Value));
        }

        // 3. Payment cards (with Luhn checksum validation)
        foreach (Match match in PotentialCardRegex.Matches(content))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length is >= 13 and <= 19 && PassesLuhnCheck(digitsOnly))
            {
                AddMatch(new RawEntityMatch(match.Index, match.Length, "PAYMENT_CARD", match.Value));
            }
        }

        // 4. Phone numbers
        foreach (Match match in PhoneRegex.Matches(content))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length is >= 7 and <= 15)
            {
                AddMatch(new RawEntityMatch(match.Index, match.Length, "PHONE", match.Value));
            }
        }

        foreach (Match match in InternationalPhoneRegex.Matches(content))
        {
            AddMatch(new RawEntityMatch(match.Index, match.Length, "PHONE", match.Value));
        }

        foreach (Match match in Ipv4Regex.Matches(content))
        {
            if (IPAddress.TryParse(match.Value, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
                AddMatch(new RawEntityMatch(match.Index, match.Length, "PERSONAL_DATA", match.Value));
        }

        foreach (Match match in Ipv6Regex.Matches(content))
        {
            if (IPAddress.TryParse(match.Value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
                AddMatch(new RawEntityMatch(match.Index, match.Length, "PERSONAL_DATA", match.Value));
        }

        foreach (Match match in MacRegex.Matches(content))
            AddMatch(new RawEntityMatch(match.Index, match.Length, "PERSONAL_DATA", match.Value));

        foreach (Match match in SocialSecurityNumberRegex.Matches(content))
            AddMatch(new RawEntityMatch(match.Index, match.Length, "PERSONAL_DATA", match.Value));

        // Inspect a bounded view without common invisible separators, while retaining
        // original UTF-16 offsets so the source bytes are redacted rather than the view.
        if (hasInvisibleSeparators)
        {
            var normalized = new StringBuilder(content.Length);
            var positions = new List<int>(content.Length);
            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] is '\u200b' or '\u200c' or '\u200d' or '\u2060' or '\ufeff') continue;
                normalized.Append(content[i]);
                positions.Add(i);
            }
            var view = normalized.ToString();
            void AddNormalized(Regex pattern, string category, Func<string, bool>? validate = null)
            {
                foreach (Match match in pattern.Matches(view))
                {
                    if (validate is not null && !validate(match.Value)) continue;
                    var start = positions[match.Index];
                    var end = positions[match.Index + match.Length - 1] + 1;
                    AddMatch(new RawEntityMatch(start, end - start, category, content.Substring(start, end - start)));
                }
            }
            AddNormalized(EmailRegex, "EMAIL");
            AddNormalized(InternationalPhoneRegex, "PHONE");
            AddNormalized(PhoneRegex, "PHONE", value => value.Count(char.IsDigit) is >= 7 and <= 15);
            AddNormalized(PotentialCardRegex, "PAYMENT_CARD", value =>
            {
                var digits = new string(value.Where(char.IsDigit).ToArray());
                return digits.Length is >= 13 and <= 19 && PassesLuhnCheck(digits);
            });
            AddNormalized(SocialSecurityNumberRegex, "PERSONAL_DATA");
            AddNormalized(Ipv4Regex, "PERSONAL_DATA", value => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork);
            AddNormalized(Ipv6Regex, "PERSONAL_DATA", value => IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6);
            AddNormalized(MacRegex, "PERSONAL_DATA");
            AddNormalized(SecretRegex, "SECRET");
        }

        // Labeled fields cover values whose format cannot safely identify their meaning alone.
        foreach (Match match in LabeledPiiRegex.Matches(content))
        {
            var value = match.Groups["value"];
            if (!PrivacyTokenRegex.IsMatch(value.Value))
                AddMatch(new RawEntityMatch(value.Index, value.Length, "PERSONAL_DATA", value.Value));
        }

        // 5. Custom rules
        foreach (var rule in _customRules)
        {
            foreach (Match match in rule.Pattern.Matches(content))
            {
                AddMatch(new RawEntityMatch(match.Index, match.Length, rule.EntityType, match.Value));
            }
        }

        if (matches.Count == 0)
        {
            return new DetectorResult(true, content, Array.Empty<EntitySpan>());
        }

        // Deterministically sort and deduplicate overlapping spans (earliest start, then longest length)
        matches.Sort((a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : b.Length.CompareTo(a.Length);
        });

        var nonOverlapping = new List<RawEntityMatch>();
        var categories = new List<HashSet<string>>();
        var lastEnd = -1;
        foreach (var m in matches)
        {
            if (m.Start >= lastEnd)
            {
                nonOverlapping.Add(m);
                categories.Add(new HashSet<string>(StringComparer.Ordinal) { m.EntityType });
                lastEnd = m.Start + m.Length;
            }
            else
            {
                categories[^1].Add(m.EntityType);
                if (m.Start + m.Length > lastEnd)
                {
                    var previous = nonOverlapping[^1];
                    lastEnd = m.Start + m.Length;
                    nonOverlapping[^1] = new RawEntityMatch(previous.Start,
                        lastEnd - previous.Start, previous.EntityType, content.Substring(previous.Start, lastEnd - previous.Start));
                }
            }
        }

        // Build sanitized content and calculate Unicode code-point offsets
        var sb = new StringBuilder();
        var detectedSpans = new List<EntitySpan>();
        var currentIndex = 0;
        var offsetCursor = 0;
        var scalarOffset = 0;
        var scope = new PrivacyScope(context.Tenant, context.Principal, context.Session, "redaction");

        for (var i = 0; i < nonOverlapping.Count; i++)
        {
            var entity = nonOverlapping[i];
            if (entity.Start > currentIndex)
            {
                sb.Append(content, currentIndex, entity.Start - currentIndex);
            }

            string replacement;
            if (_vault is not null)
            {
                replacement = await _vault.StoreAsync(entity.RawValue, entity.EntityType, scope, _tokenTtl, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                replacement = $"[OC_{entity.EntityType}]";
            }

            while (offsetCursor < entity.Start)
            {
                if (!char.IsLowSurrogate(content[offsetCursor]) || offsetCursor == 0 ||
                    !char.IsHighSurrogate(content[offsetCursor - 1])) scalarOffset++;
                offsetCursor++;
            }
            var startOffset = scalarOffset;
            var length = CountUnicodeCodePoints(content, entity.Start, entity.Length);
            foreach (var category in categories[i].OrderBy(value => value, StringComparer.Ordinal))
                detectedSpans.Add(new EntitySpan(startOffset, length, category));

            sb.Append(replacement);
            currentIndex = entity.Start + entity.Length;
        }

        if (currentIndex < content.Length)
        {
            sb.Append(content, currentIndex, content.Length - currentIndex);
        }

        return new DetectorResult(true, sb.ToString(), detectedSpans);
    }

    /// <summary>Calculates the number of Unicode code points (scalars) in the specified string segment.</summary>
    public static int CountUnicodeCodePoints(string str, int startIndex, int length)
    {
        if (string.IsNullOrEmpty(str) || length <= 0)
            return 0;
        var count = 0;
        var end = startIndex + length;
        for (var i = startIndex; i < end; i++)
        {
            if (char.IsHighSurrogate(str[i]) && i + 1 < end && char.IsLowSurrogate(str[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    /// <summary>Validates a number string using the standard Luhn checksum algorithm.</summary>
    public static bool PassesLuhnCheck(string digits)
    {
        if (string.IsNullOrWhiteSpace(digits))
            return false;
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is < '0' or > '9')
                return false;
            var n = c - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private sealed record RawEntityMatch(int Start, int Length, string EntityType, string RawValue);
}
