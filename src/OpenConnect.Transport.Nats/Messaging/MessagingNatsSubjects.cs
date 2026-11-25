using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Transport.Nats.Messaging;

/// <summary>
/// Canonical NATS stream and subject helpers for OpenConnect IMessaging with fail-closed token validation
/// to prevent subject injection and wildcard escape.
/// </summary>
public static class MessagingNatsSubjects
{
    public const string StreamName = "OC_MESSAGING";
    public const string DurableSubjectPrefix = "oc.v1.messaging.durable";
    public const string DurableWildcardFilter = "oc.v1.messaging.durable.>";
    public const string LiveSubjectPrefix = "oc.v1.messaging.live";
    public const int MaxTokenLength = 128;

    public static string DurableConversation(TenantId tenantId, ConversationId conversationId)
        => DurableConversation(tenantId.Value.ToString(), conversationId.Value.ToString());

    public static string DurableConversation(string tenantToken, string conversationToken)
    {
        var tenant = ValidateOpaqueToken(tenantToken, nameof(tenantToken));
        var conversation = ValidateOpaqueToken(conversationToken, nameof(conversationToken));
        return $"{DurableSubjectPrefix}.{tenant}.{conversation}";
    }

    public static string LiveGateway(TenantId tenantId, string gatewayId)
        => LiveGateway(tenantId.Value.ToString(), gatewayId);

    public static string LiveGateway(string tenantToken, string gatewayToken)
    {
        var tenant = ValidateOpaqueToken(tenantToken, nameof(tenantToken));
        var gateway = ValidateOpaqueToken(gatewayToken, nameof(gatewayToken));
        return $"{LiveSubjectPrefix}.{tenant}.{gateway}";
    }

    /// <summary>
    /// Validates an opaque NATS subject token. Only ASCII letters, digits, hyphens, and underscores are allowed.
    /// Rejects dots, wildcards (*, &gt;), whitespace, slashes, and control characters.
    /// </summary>
    public static string ValidateOpaqueToken(string token, string paramName)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("NATS subject token must be a non-empty opaque identifier.", paramName);
        if (token.Length > MaxTokenLength)
            throw new ArgumentException($"NATS subject token exceeds maximum length of {MaxTokenLength}.", paramName);

        foreach (var ch in token)
        {
            var isAllowed = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '-'
                || ch == '_';
            if (!isAllowed)
                throw new ArgumentException($"NATS subject token contains disallowed character '{ch}'.", paramName);
        }

        return token;
    }

    /// <summary>
    /// Validates that a concrete publish subject conforms strictly to either the durable conversation
    /// or live gateway subject format without wildcards or injection.
    /// </summary>
    public static string ValidateSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("NATS subject must not be null or empty.", nameof(subject));

        var durablePrefix = DurableSubjectPrefix + ".";
        var livePrefix = LiveSubjectPrefix + ".";
        string remainder;
        if (subject.StartsWith(durablePrefix, StringComparison.Ordinal))
        {
            remainder = subject[durablePrefix.Length..];
        }
        else if (subject.StartsWith(livePrefix, StringComparison.Ordinal))
        {
            remainder = subject[livePrefix.Length..];
        }
        else
        {
            throw new ArgumentException("NATS messaging subject must start with an allowed messaging prefix.", nameof(subject));
        }

        var parts = remainder.Split('.');
        if (parts.Length != 2)
            throw new ArgumentException("NATS messaging subject must contain exactly two opaque scope tokens.", nameof(subject));

        ValidateOpaqueToken(parts[0], nameof(subject));
        ValidateOpaqueToken(parts[1], nameof(subject));
        return subject;
    }
}
