namespace OpenConnect.Navigator.OperationalBrowser;

public interface IBrowserSessionTerminator
{
    Task TerminateAsync(IsolatedBrowserSession session, CancellationToken cancellationToken);
}

/// <summary>Creates one fresh profile directory per job. No browser process or profile reuse is performed here.</summary>
public sealed class IsolatedBrowserSessionFactory
{
    /// <summary>Stops the browser before deleting its job-scoped profile. Any failure is surfaced in the receipt.</summary>
    public async Task<BrowserAuditReceipt> CloseAsync(BrowserJobSpec job, IsolatedBrowserSession session,
        IBrowserSessionTerminator terminator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminator);
        try
        {
            await terminator.TerminateAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new BrowserAuditReceipt(job.ExecutionId, session.SessionId, DateTimeOffset.UtcNow,
                ProfileDeleted: false, CleanupRequiresOperatorAlert: true);
        }
        return await CleanupAsync(job, session, cancellationToken).ConfigureAwait(false);
    }

    public IsolatedBrowserSession Create(BrowserJobSpec job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.ExecutionId) || string.IsNullOrWhiteSpace(job.TenantScope) ||
            string.IsNullOrWhiteSpace(job.ServicePrincipalRef)) throw new ArgumentException("Invalid browser job.", nameof(job));
        var path = Path.Combine(Path.GetTempPath(), "openconnect-browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new IsolatedBrowserSession(Guid.NewGuid().ToString("N"), path, job.ServicePrincipalRef);
    }

    public async Task<BrowserAuditReceipt> CleanupAsync(BrowserJobSpec job, IsolatedBrowserSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(session);
        var deleted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openconnect-browser")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(session.ProfileDir);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Profile path is outside managed temp root.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            deleted = !Directory.Exists(target);
        }
        catch (Exception) { }
        await Task.CompletedTask.ConfigureAwait(false);
        return new BrowserAuditReceipt(job.ExecutionId, session.SessionId, DateTimeOffset.UtcNow, deleted,
            CleanupRequiresOperatorAlert: !deleted);
    }
}
