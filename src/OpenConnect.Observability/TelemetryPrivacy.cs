using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Observability;

public enum TelemetryDataClassification { Public, Operational, Internal, PersonalData, Confidential, Credential, Unknown }
public enum TelemetrySanitizationAction { Allow, Drop, Redact, Mask, Pseudonymize }
public enum TelemetryPrivacyMode { Strict, Balanced, Custom }

public sealed record TelemetryAttribute(string Name, object? Value, TelemetryDataClassification Classification = TelemetryDataClassification.Unknown);

public interface ITelemetryDataClassifier { TelemetryDataClassification Classify(string name, object? value); }
public interface ITelemetryPrivacyPolicy { TelemetrySanitizationAction Decide(string name, TelemetryDataClassification classification); }
public interface ITelemetryPrivacyFilter { KeyValuePair<string, object?>[] Filter(IEnumerable<KeyValuePair<string, object?>> attributes); }
public interface ITelemetrySecretDetector { bool IsSecret(string name, object? value); }
public interface ITelemetryPiiDetector { bool IsPersonalData(string name, object? value); }
public interface ITelemetryPseudonymizer { string Pseudonymize(string domain, string tenantId, string value); }
public interface ITelemetryExportValidator { bool IsSafe(IEnumerable<KeyValuePair<string, object?>> attributes); }

public sealed class TelemetryPrivacyOptions
{
    public TelemetryPrivacyMode Mode { get; set; } = TelemetryPrivacyMode.Strict;
    public bool DropUnknown { get; set; } = true;
    public bool AllowPrompt { get; set; }
    public bool AllowCompletion { get; set; }
    public bool AllowExceptionMessages { get; set; }
    public bool EnableSecretDetection { get; set; } = true;
    public bool EnablePiiDetection { get; set; }
    public int MaxAttributeLength { get; set; } = 256;
}

/// <summary>Validation callable by dependency-injection adapters without binding policy to a host framework.</summary>
public sealed class TelemetryPrivacyOptionsValidator
{
    public void Validate(TelemetryPrivacyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));
        if (options.MaxAttributeLength is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options.MaxAttributeLength));
    }
}

public sealed class TelemetryDataClassifier(ITelemetrySecretDetector secrets, ITelemetryPiiDetector pii) : ITelemetryDataClassifier
{
    private static readonly HashSet<string> PersonalNames = new(StringComparer.OrdinalIgnoreCase)
        { "tenant.id", "tenant_id", "user.id", "user_id", "email", "prompt", "completion", "exception.message", "tool.arguments" };
    public TelemetryDataClassification Classify(string name, object? value)
    {
        if (string.IsNullOrWhiteSpace(name) || value is null) return TelemetryDataClassification.Unknown;
        if (secrets.IsSecret(name, value)) return TelemetryDataClassification.Credential;
        if (PersonalNames.Contains(name) || pii.IsPersonalData(name, value)) return TelemetryDataClassification.PersonalData;
        if (Instrumentation.IsApprovedDimension(name, value as string ?? "")) return TelemetryDataClassification.Operational;
        return TelemetryDataClassification.Unknown;
    }
}

public sealed class TelemetryPrivacyPolicy(TelemetryPrivacyOptions options) : ITelemetryPrivacyPolicy
{
    public TelemetrySanitizationAction Decide(string name, TelemetryDataClassification classification)
    {
        // Credentials are unconditionally denied; opt-in flags cannot weaken this rule.
        if (classification == TelemetryDataClassification.Credential) return TelemetrySanitizationAction.Drop;
        if (classification == TelemetryDataClassification.PersonalData)
        {
            if (name is "prompt" && options.AllowPrompt || name is "completion" && options.AllowCompletion ||
                name is "exception.message" && options.AllowExceptionMessages) return TelemetrySanitizationAction.Redact;
            return TelemetrySanitizationAction.Drop;
        }
        if (classification == TelemetryDataClassification.Operational || classification == TelemetryDataClassification.Public)
            return TelemetrySanitizationAction.Allow;
        return options.DropUnknown ? TelemetrySanitizationAction.Drop : TelemetrySanitizationAction.Redact;
    }
}

public sealed class DefaultTelemetrySecretDetector : ITelemetrySecretDetector
{
    private static readonly string[] SensitiveNameParts = ["authorization", "cookie", "password", "secret", "token", "api_key", "apikey", "private_key", "connection_string", "credential"];
    public bool IsSecret(string name, object? value)
    {
        if (SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) return true;
        if (value is not string text) return false;
        return text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("-----BEGIN ", StringComparison.Ordinal) || text.Contains("://", StringComparison.Ordinal) && text.Contains('@');
    }
}

/// <summary>No expensive inference is performed at export; integrate a bounded local detector here if explicitly configured.</summary>
public sealed class NoOpTelemetryPiiDetector : ITelemetryPiiDetector { public bool IsPersonalData(string name, object? value) => false; }

public sealed class TelemetryPrivacyFilter(ITelemetryDataClassifier classifier, ITelemetryPrivacyPolicy policy,
    ITelemetryPrivacyOptionsAccessor options) : ITelemetryPrivacyFilter
{
    public KeyValuePair<string, object?>[] Filter(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var result = new List<KeyValuePair<string, object?>>();
        foreach (var item in attributes)
        {
            try
            {
                if (item.Value is not string text || text.Length > options.Options.MaxAttributeLength) continue;
                var classification = classifier.Classify(item.Key, item.Value);
                if (policy.Decide(item.Key, classification) != TelemetrySanitizationAction.Allow) continue;
                if (classification is TelemetryDataClassification.Operational or TelemetryDataClassification.Public &&
                    Instrumentation.IsApprovedDimension(item.Key, text)) result.Add(item);
            }
            catch { /* Fail closed at every exporter boundary. */ }
        }
        return result.ToArray();
    }
}

public interface ITelemetryPrivacyOptionsAccessor { TelemetryPrivacyOptions Options { get; } }
public sealed class TelemetryPrivacyOptionsAccessor(TelemetryPrivacyOptions options) : ITelemetryPrivacyOptionsAccessor { public TelemetryPrivacyOptions Options { get; } = options; }

public interface ITelemetryPseudonymizationKeyProvider { string KeyId { get; } ReadOnlyMemory<byte> GetKey(string tenantId); }
public sealed class HmacTelemetryPseudonymizer(ITelemetryPseudonymizationKeyProvider keys) : ITelemetryPseudonymizer
{
    public string Pseudonymize(string domain, string tenantId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain); ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(value);
        var key = keys.GetKey(tenantId);
        if (key.IsEmpty) throw new InvalidOperationException("No pseudonymization key is configured.");
        var bytes = Encoding.UTF8.GetBytes($"openconnect\0{keys.KeyId}\0{tenantId}\0{domain}\0{value}");
        return $"{keys.KeyId}:{Convert.ToHexString(HMACSHA256.HashData(key.Span, bytes)).ToLowerInvariant()}";
    }
}

public sealed class TelemetryExportValidator(ITelemetryPrivacyFilter filter) : ITelemetryExportValidator
{
    /// <summary>Validates the final activity tag set immediately before an exporter consumes it.</summary>
    public bool ValidateActivity(System.Diagnostics.Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return IsSafe(activity.TagObjects.Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value)));
    }

    public bool IsSafe(IEnumerable<KeyValuePair<string, object?>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var input = attributes.ToArray();
        return input.Length == filter.Filter(input).Length;
    }
}

public static class TelemetryPrivacy
{
    public static (ITelemetryPrivacyFilter Filter, ITelemetryExportValidator Validator) CreateDefault()
    {
        var options = new TelemetryPrivacyOptions();
        var accessor = new TelemetryPrivacyOptionsAccessor(options);
        var secretDetector = new DefaultTelemetrySecretDetector();
        var classifier = new TelemetryDataClassifier(secretDetector, new NoOpTelemetryPiiDetector());
        var filter = new TelemetryPrivacyFilter(classifier, new TelemetryPrivacyPolicy(options), accessor);
        return (filter, new TelemetryExportValidator(filter));
    }
}
