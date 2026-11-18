using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

// Only simple whole-segment parameters are supported. Constraints, optional segments,
// catch-alls and ambiguous routes fail closed until their routing semantics are supported.
internal static class LiveUIRouteMatcher
{
    public static LiveUIRoute? Resolve(IReadOnlyList<LiveUIRoute> routes, string path)
    {
        LiveUIRoute? selected = null;
        var bestScore = -1;
        var ambiguous = false;
        foreach (var route in routes)
        {
            if (!TryMatch(route.Path, path, out var score)) continue;
            if (score > bestScore)
            {
                selected = route;
                bestScore = score;
                ambiguous = false;
            }
            else if (score == bestScore)
            {
                ambiguous = true;
            }
        }
        return ambiguous ? null : selected;
    }

    public static bool TryMatch(string template, string path, out int score)
    {
        score = 0;
        if (!IsSafePath(template, allowParameters: true) || !IsSafePath(path, allowParameters: false)) return false;
        var templateSegments = template.Split('/');
        var pathSegments = path.Split('/');
        if (templateSegments.Length != pathSegments.Length) return false;
        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < templateSegments.Length; index++)
        {
            var segment = templateSegments[index];
            var value = Uri.UnescapeDataString(pathSegments[index]);
            if (value.Contains('/') || value.Contains('\\') || value.Any(char.IsControl) || value is "." or "..") return false;
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var name = segment[1..^1];
                if (name.Length == 0 || !char.IsAsciiLetter(name[0]) ||
                    name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
                    !parameters.Add(name) || value.Length == 0) return false;
            }
            else
            {
                if (segment.Contains('{') || segment.Contains('}') ||
                    !string.Equals(Uri.UnescapeDataString(segment), value, StringComparison.OrdinalIgnoreCase)) return false;
                score++;
            }
        }
        return true;
    }

    private static bool IsSafePath(string path, bool allowParameters) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 2048 && path.StartsWith('/') &&
        !path.Contains("//", StringComparison.Ordinal) && !path.Contains('\\') &&
        !path.Contains('?') && !path.Contains('#') &&
        (allowParameters || !path.Contains('{') && !path.Contains('}'));
}
