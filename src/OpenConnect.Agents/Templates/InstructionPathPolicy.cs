namespace OpenConnect.Agents.Templates;

// Templates are portable between hosts, so reject both Windows and Unix rooted
// paths and traversal syntax regardless of the current operating system.
internal static class InstructionPathPolicy
{
    internal static bool IsSafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        return !Path.IsPathRooted(path) && !normalized.StartsWith('/') &&
            !normalized.Contains(':') && !normalized.Contains('\0') &&
            !normalized.Split('/').Contains("..", StringComparer.Ordinal);
    }
}
