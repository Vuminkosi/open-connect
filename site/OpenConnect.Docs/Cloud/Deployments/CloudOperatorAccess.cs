using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed class CloudOperatorAccess(IConfiguration configuration)
{
    public bool IsAllowed(ClaimsPrincipal principal)
    {
        var identity = configuration.GetSection("OpenConnect:Cloud:Identity");
        if (!CloudIdentity.IsEnabled(principal, identity)) return false;
        var subject = CloudSubjectResolver.FromPrincipal(principal, identity["Authority"]);
        if (subject is null) return false;
        return configuration.GetSection("OpenConnect:Cloud:Operators").GetChildren().Any(entry =>
            !string.IsNullOrWhiteSpace(entry["Issuer"]) && !string.IsNullOrWhiteSpace(entry["Subject"]) &&
            string.Equals(entry["Issuer"], subject.Issuer, StringComparison.Ordinal) &&
            string.Equals(entry["Subject"], subject.SubjectId, StringComparison.Ordinal));
    }
}
