using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Worker;

/// <summary>Validated UTF-16 range converted from a Unicode scalar span.</summary>
public readonly record struct Utf16TextSpan(int Start, int Length, int ScalarStart, int ScalarLength);

/// <summary>
/// Strict Unicode scalar (code point) to .NET UTF-16 offset converter and finding bounds validator.
/// Rejects invalid, out-of-bounds, surrogate-splitting, or non-finite findings before content is touched.
/// </summary>
public static class UnicodeScalarOffsetConverter
{
    /// <summary>
    /// Builds a lookup table mapping each Unicode scalar index <c>0..scalarCount</c> to its UTF-16 code-unit index.
    /// Rejects malformed UTF-16 lone surrogates in the input string.
    /// </summary>
    public static int[] BuildScalarToUtf16Map(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var offsets = new List<int>(content.Length + 1);
        var utf16Index = 0;
        while (utf16Index < content.Length)
        {
            var ch = content[utf16Index];
            if (char.IsHighSurrogate(ch))
            {
                if (utf16Index + 1 >= content.Length || !char.IsLowSurrogate(content[utf16Index + 1]))
                    throw new ArgumentException("Input content contains an unpaired high surrogate.", nameof(content));
                offsets.Add(utf16Index);
                utf16Index += 2;
            }
            else if (char.IsLowSurrogate(ch))
            {
                throw new ArgumentException("Input content contains an unpaired low surrogate.", nameof(content));
            }
            else
            {
                offsets.Add(utf16Index);
                utf16Index += 1;
            }
        }
        offsets.Add(utf16Index);
        return offsets.ToArray();
    }

    /// <summary>Converts a half-open Unicode scalar range <c>[scalarStart, scalarEnd)</c> into a validated UTF-16 span.</summary>
    public static bool TryConvertScalarRangeToUtf16(int[] scalarToUtf16Map, int scalarStart, int scalarEnd, out Utf16TextSpan span)
    {
        ArgumentNullException.ThrowIfNull(scalarToUtf16Map);
        var scalarCount = scalarToUtf16Map.Length - 1;
        if (scalarStart < 0 || scalarEnd <= scalarStart || scalarEnd > scalarCount)
        {
            span = default;
            return false;
        }

        var utf16Start = scalarToUtf16Map[scalarStart];
        var utf16End = scalarToUtf16Map[scalarEnd];
        if (utf16Start < 0 || utf16End <= utf16Start)
        {
            span = default;
            return false;
        }

        span = new Utf16TextSpan(utf16Start, utf16End - utf16Start, scalarStart, scalarEnd - scalarStart);
        return true;
    }

    /// <summary>
    /// Validates all text-span findings against the source text's Unicode scalar bounds and finite confidence range.
    /// Returns <see langword="false"/> if any finding is invalid, out-of-bounds, or non-finite.
    /// </summary>
    public static bool TryValidateTextFindings(
        string content,
        IReadOnlyList<SecurityFinding> findings,
        out IReadOnlyList<(SecurityFinding Finding, Utf16TextSpan Utf16Span)> validated)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(findings);

        if (findings.Count > 20_000)
        {
            validated = Array.Empty<(SecurityFinding, Utf16TextSpan)>();
            return false;
        }

        int[] map;
        try
        {
            map = BuildScalarToUtf16Map(content);
        }
        catch (ArgumentException)
        {
            validated = Array.Empty<(SecurityFinding, Utf16TextSpan)>();
            return false;
        }

        var result = new List<(SecurityFinding, Utf16TextSpan)>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding is null ||
                string.IsNullOrWhiteSpace(finding.DetectorId) ||
                string.IsNullOrWhiteSpace(finding.Category) ||
                !double.IsFinite(finding.Confidence) ||
                finding.Confidence < 0.0 ||
                finding.Confidence > 1.0)
            {
                validated = Array.Empty<(SecurityFinding, Utf16TextSpan)>();
                return false;
            }

            if (finding.Kind != SecurityFindingKind.TextSpan)
                continue;

            if (!finding.Start.HasValue || !finding.End.HasValue ||
                !TryConvertScalarRangeToUtf16(map, finding.Start.Value, finding.End.Value, out var utf16Span))
            {
                validated = Array.Empty<(SecurityFinding, Utf16TextSpan)>();
                return false;
            }

            result.Add((finding, utf16Span));
        }

        validated = result;
        return true;
    }

    /// <summary>Validates image region findings against declared image dimensions and finite confidence range.</summary>
    public static bool ValidateImageFindings(SecurityImageDimensions? dimensions, IReadOnlyList<SecurityFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (dimensions is null || dimensions.Width <= 0 || dimensions.Height <= 0)
            return false;
        if (findings.Count > 20_000)
            return false;

        foreach (var finding in findings)
        {
            if (finding is null ||
                string.IsNullOrWhiteSpace(finding.DetectorId) ||
                string.IsNullOrWhiteSpace(finding.Category) ||
                !double.IsFinite(finding.Confidence) ||
                finding.Confidence < 0.0 ||
                finding.Confidence > 1.0)
            {
                return false;
            }

            if (finding.Kind != SecurityFindingKind.ImageRegion)
                continue;

            if (finding.Region is not { } region ||
                region.X < 0 || region.Y < 0 ||
                region.Width <= 0 || region.Height <= 0 ||
                (long)region.X + region.Width > dimensions.Width ||
                (long)region.Y + region.Height > dimensions.Height)
            {
                return false;
            }
        }

        return true;
    }
}
