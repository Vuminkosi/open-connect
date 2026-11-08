using OpenConnect.Navigator;
using OpenConnect.Navigator.Playwright;
using System.Text.Json;

namespace OpenConnect.Navigator.OperationalBrowser;

public sealed class BrowserJobBoundary(EgressGuard egress)
{
    public async Task<(bool Allowed, string DenialReason)> ValidateAsync(BrowserJobSpec job, Uri destination, int step, long bytes,
        CancellationToken cancellationToken = default)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.ExecutionId) || string.IsNullOrWhiteSpace(job.TenantScope) ||
            string.IsNullOrWhiteSpace(job.ServicePrincipalRef) || string.IsNullOrWhiteSpace(job.PolicyRevision) ||
            string.IsNullOrWhiteSpace(job.IdempotencyKey) || job.AllowedOrigins is not { Count: > 0 } ||
            job.ResourceBudget is null || job.ResourceBudget.MaxMs is <= 0 or > 300_000 ||
            job.ResourceBudget.MaxSteps is <= 0 or > 25 || job.ResourceBudget.MaxBytes is <= 0 or > 10_000_000 ||
            job.Deadline <= DateTimeOffset.UtcNow || step < 0 || step >= job.ResourceBudget.MaxSteps ||
            bytes < 0 || bytes > job.ResourceBudget.MaxBytes) return (false, "invalid_job");
        if (job.AllowedOrigins.Any(o => !IsCanonicalOrigin(o))) return (false, "invalid_origin_policy");
        return await egress.IsAllowedAsync(destination, job.AllowedOrigins, cancellationToken).ConfigureAwait(false)
            ? (true, string.Empty) : (false, "egress_denied");
    }

    public static bool IsAllowedTool(NavigatorActionType action, JsonElement args, Uri origin, bool screenshotConsent,
        out string toolName, out JsonElement normalizedArguments, out string denialReason) =>
        PlaywrightToolAllowlist.TryResolve(action, args, origin, screenshotConsent, out toolName,
            out normalizedArguments, out denialReason);

    private static bool IsCanonicalOrigin(Uri origin) => origin is { IsAbsoluteUri: true } &&
        origin.Scheme == Uri.UriSchemeHttps && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" &&
        string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment) && origin.Port == 443;
}
