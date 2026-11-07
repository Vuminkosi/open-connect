namespace OpenConnect.Agents.Templates;

/// <summary>Adapts the Core instruction loader without introducing an Agents-to-Core dependency.</summary>
/// <remarks>
/// The host composes this adapter with Core, for example:
/// <c>new InstructionLoaderAdapter(async (path, ct) =&gt; { var loader = new OpenConnect.Core.Runtime.InstructionLoader(root); var loaded = await loader.LoadAsync(path, ct); return (loaded.Content, loaded.Revision); })</c>.
/// Core remains responsible for root containment, symlink checks, UTF-8 validation, size limits and hashing.
/// </remarks>
public sealed class InstructionLoaderAdapter
{
    private readonly Func<string, CancellationToken, Task<(string Content, string Revision)>> _loader;

    /// <summary>Creates an adapter around the host's secure instruction loader.</summary>
    /// <param name="loader">Host-provided Core loader delegate.</param>
    public InstructionLoaderAdapter(Func<string, CancellationToken, Task<(string Content, string Revision)>> loader) =>
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>Loads a safe relative instruction path using the host-provided loader.</summary>
    /// <param name="path">Relative instruction path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Instruction content and its revision.</returns>
    public Task<(string Content, string Revision)> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(path) || path.StartsWith('/') || segments.Contains("..", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Instruction path must be a safe relative path.");
        return _loader(path, cancellationToken);
    }
}
