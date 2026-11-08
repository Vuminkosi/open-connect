namespace OpenConnect.Navigator.Diagnostics;

/// <summary>Test helper that detects recognizable sensitive canaries without retaining or logging them.</summary>
public static class RedactionCheck
{
    public static bool ContainsSensitiveContent(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.Contains('@') || value.Contains('<') || value.Contains('>') ||
               value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("audio:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("transcript:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("data:", StringComparison.OrdinalIgnoreCase);
    }
}
