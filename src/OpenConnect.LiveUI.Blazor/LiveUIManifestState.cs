using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

public sealed class LiveUIManifestState(LiveUIManifestValidator validator) : ILiveUIManifestProvider
{
    private readonly object _gate = new();
    private LiveUIManifest? _manifest;

    public void SetManifest(LiveUIManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = validator.Validate(manifest);
        if (errors.Count != 0)
            throw new ArgumentException($"The LiveUI manifest is invalid ({errors[0]}).", nameof(manifest));
        lock (_gate) _manifest = manifest;
    }

    public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_manifest);
    }

    internal bool TryReplace(LiveUIManifest expected, LiveUIManifest replacement)
    {
        if (validator.Validate(replacement).Count != 0) return false;
        lock (_gate)
        {
            if (!ReferenceEquals(_manifest, expected)) return false;
            _manifest = replacement;
            return true;
        }
    }
}
