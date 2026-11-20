using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Docs.Cloud;

internal static class CloudIdentityFingerprint
{
    public static string Compute(CloudSubject subject) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{subject.Issuer.Length}:{subject.Issuer}{subject.SubjectId}")));
}
