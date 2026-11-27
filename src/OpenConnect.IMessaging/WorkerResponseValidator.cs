using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Strictly validates responses from the private IMessaging Python worker.
/// Enforces correlation, tenant/task/kind matching, bounded allowlisted capabilities,
/// and never trusts worker responses to authorize recipients or advertise external channels.
/// </summary>
public static class WorkerResponseValidator
{
    private static readonly HashSet<string> AllowedExecuteStatuses = new(StringComparer.Ordinal)
    {
        "accepted",
        "deduplicated",
        "degraded",
        "rejected"
    };

    private static readonly HashSet<string> AllowedCapabilityIds = new(StringComparer.Ordinal)
    {
        MessagingCapabilityIds.InAppSend,
        MessagingCapabilityIds.InAppReceive,
        MessagingCapabilityIds.InAppCall,
        MessagingCapabilityIds.E2EE,
        MessagingCapabilityIds.PushFcm,
        MessagingCapabilityIds.PushWeb,
        "messaging.push.webpush",
        MessagingCapabilityIds.DeviceEnrollment
    };

    /// <summary>Validates a <c>POST /internal/v1/execute</c> JSON response against the expected request metadata.</summary>
    public static MessagingWorkerResult ValidateExecuteResponse(
        JsonElement root,
        string expectedTaskId,
        string expectedKind,
        string expectedTenantId)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response root must be a JSON object.", 502);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is not ("schemaVersion" or "taskId" or "kind" or "tenantId" or "status" or "result") ||
                !seen.Add(prop.Name))
            {
                throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response contains unexpected or duplicate fields.", 502);
            }
        }

        if (seen.Count != 6)
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response is missing required fields.", 502);

        var schemaVersion = GetRequiredString(root, "schemaVersion");
        var taskId = GetRequiredString(root, "taskId");
        var kind = GetRequiredString(root, "kind");
        var tenantId = GetRequiredString(root, "tenantId");
        var status = GetRequiredString(root, "status");
        var resultProp = root.GetProperty("result");

        if (!string.Equals(schemaVersion, "openconnect.v1", StringComparison.Ordinal))
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response schemaVersion mismatch.", 502);

        if (!string.Equals(taskId, expectedTaskId, StringComparison.Ordinal) ||
            !string.Equals(kind, expectedKind, StringComparison.Ordinal) ||
            !string.Equals(tenantId, expectedTenantId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response correlation mismatch.", 502);
        }

        if (!AllowedExecuteStatuses.Contains(status))
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response status is not recognized.", 502);

        if (resultProp.ValueKind != JsonValueKind.Object)
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response result must be a JSON object.", 502);

        string? errorCode = null;
        if (resultProp.TryGetProperty("reasonCode", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            errorCode = reasonEl.GetString();

        var accepted = status is "accepted" or "deduplicated";
        return new MessagingWorkerResult(
            schemaVersion,
            taskId,
            kind,
            accepted,
            status,
            errorCode,
            resultProp.GetRawText());
    }

    /// <summary>
    /// Validates a <c>GET /internal/v1/capabilities</c> JSON response and returns only
    /// allowlisted, verified-ready in-app messaging capabilities (strictly excluding WhatsApp/Telegram/Discord).
    /// </summary>
    public static MessagingWorkerReadiness ValidateCapabilitiesResponse(JsonElement root, DateTimeOffset checkedAt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("capabilities", out var capsEl) ||
            capsEl.ValueKind != JsonValueKind.Array)
        {
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker capabilities response is malformed.", 502);
        }

        var verified = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? contractVersion = null;

        foreach (var item in capsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            var state = item.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.String ? stateEl.GetString() : null;
            var schemaVer = item.TryGetProperty("schemaVersion", out var svEl) && svEl.ValueKind == JsonValueKind.String ? svEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(id) || !string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
                continue;

            // Never advertise external channel placeholders (WhatsApp, Telegram, Discord) or unverified IDs.
            if (!IsAllowedCapabilityId(id))
                continue;

            contractVersion ??= schemaVer;
            if (seen.Add(id))
                verified.Add(id);
        }

        var isReady = verified.Contains(MessagingCapabilityIds.InAppSend, StringComparer.Ordinal) &&
                      verified.Contains(MessagingCapabilityIds.InAppReceive, StringComparer.Ordinal);

        return new MessagingWorkerReadiness(isReady, verified, contractVersion ?? "openconnect.v1", checkedAt);
    }

    public static bool IsAllowedCapabilityId(string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            return false;

        // Explicitly block any external channel identifiers even if accidentally added.
        if (capabilityId.Contains("whatsapp", StringComparison.OrdinalIgnoreCase) ||
            capabilityId.Contains("telegram", StringComparison.OrdinalIgnoreCase) ||
            capabilityId.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllowedCapabilityIds.Contains(capabilityId);
    }

    private static string GetRequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, $"Worker response field '{name}' must be a string.", 502);
        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, $"Worker response field '{name}' cannot be empty.", 502);
        return value;
    }
}
