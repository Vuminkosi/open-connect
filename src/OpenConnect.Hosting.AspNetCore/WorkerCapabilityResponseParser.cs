using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

// Parses only the reviewed expansion IDs. Network identity and exclusive registry ownership
// are the host monitor's responsibility, not properties of an untrusted response body.
internal static class WorkerCapabilityResponseParser
{
    private static readonly IReadOnlyDictionary<string, (string WorkerKind, CapabilityKind HostKind)> Known =
        new Dictionary<string, (string, CapabilityKind)>(StringComparer.Ordinal)
        {
            ["documents.analyze"] = ("tool", CapabilityKind.Tool),
            ["documents.extract"] = ("tool", CapabilityKind.Tool),
            ["knowledge.embed"] = ("embedding", CapabilityKind.Model),
            ["knowledge.rerank"] = ("tool", CapabilityKind.Tool),
            ["knowledge.index"] = ("tool", CapabilityKind.Tool),
            ["vision.analyze"] = ("image", CapabilityKind.Model),
            ["vision.embed"] = ("image", CapabilityKind.Model),
            ["analytics.analyze"] = ("tool", CapabilityKind.Tool),
            ["security.rules"] = ("security", CapabilityKind.Security),
            ["security.gliner-pii"] = ("security", CapabilityKind.Security),
            ["security.deberta-injection"] = ("security", CapabilityKind.Security),
            ["security.qwen3-guard"] = ("security", CapabilityKind.Security),
            ["security.paddle-ocr"] = ("security", CapabilityKind.Security),
            ["security.yunet"] = ("security", CapabilityKind.Security),
            ["security.malware"] = ("security", CapabilityKind.Security),
            ["security.clamav"] = ("security", CapabilityKind.Security),
            ["messaging.inapp.send"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.inapp.receive"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.inapp.call"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.e2ee"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.push.fcm"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.push.webpush"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.push.web"] = ("imessaging", CapabilityKind.Tool),
            ["messaging.device.enrollment"] = ("imessaging", CapabilityKind.Tool)
        };

    internal static IReadOnlyList<CapabilityDescriptor> Parse(JsonElement root, IReadOnlySet<string> allowed, bool inference)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (allowed.Count == 0 || allowed.Any(id => !Known.ContainsKey(id)))
            throw new ArgumentException("Invalid capability scope.");
        try
        { return inference ? ParseInference(root, allowed) : ParseStructured(root, allowed); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new InvalidOperationException("Invalid worker capability response."); }
    }

    private static IReadOnlyList<CapabilityDescriptor> ParseStructured(JsonElement root, IReadOnlySet<string> allowed)
    {
        Properties(root, "capabilities");
        var list = root.GetProperty("capabilities");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 64)
            Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CapabilityDescriptor>();
        foreach (var descriptor in list.EnumerateArray())
        {
            Properties(descriptor, "schemaVersion", "id", "kind", "state", "version", "localOnly");
            var id = Text(descriptor.GetProperty("id"), 256);
            var kind = Text(descriptor.GetProperty("kind"), 32);
            var version = Text(descriptor.GetProperty("version"), 128);
            if (!seen.Add(id) || descriptor.GetProperty("schemaVersion").GetString() != "openconnect.v1" ||
                descriptor.GetProperty("state").GetString() != "ready" || descriptor.GetProperty("localOnly").ValueKind != JsonValueKind.True ||
                string.IsNullOrWhiteSpace(version))
                Invalid();
            if (allowed.Contains(id))
            {
                if (!Known.TryGetValue(id, out var expected) ||
                    !(kind == expected.WorkerKind || (expected.WorkerKind == "imessaging" && kind == "tool")))
                    Invalid();
                result.Add(new CapabilityDescriptor(new CapabilityId(id), expected.HostKind, DetectorState.Ready));
            }
        }
        return result;
    }

    private static IReadOnlyList<CapabilityDescriptor> ParseInference(JsonElement root, IReadOnlySet<string> allowed)
    {
        Properties(root, "worker", "version", "backend", "engine", "modelId", "modelRevision", "modelLoaded", "capabilities", "privacyMode");
        if (root.GetProperty("worker").GetString() != "inference" || root.GetProperty("engine").GetString() != "real" ||
            root.GetProperty("privacyMode").GetString() != "strict-local" || root.GetProperty("modelLoaded").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            Invalid();
        _ = Text(root.GetProperty("version"), 128);
        _ = Text(root.GetProperty("backend"), 128);
        foreach (var field in new[] { "modelId", "modelRevision" })
            if (root.GetProperty(field).ValueKind != JsonValueKind.Null)
                _ = Text(root.GetProperty(field), 256);
        var list = root.GetProperty("capabilities");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 64)
            Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in list.EnumerateArray())
            if (!seen.Add(Text(value, 256)))
                Invalid();
        return allowed.Contains("documents.extract") && seen.Contains("documents.extract")
            ? [new CapabilityDescriptor(new CapabilityId("documents.extract"), CapabilityKind.Tool, DetectorState.Ready)] : [];
    }

    private static string Text(JsonElement element, int maximum)
    {
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(c => c < 32 || c == 127 || char.IsSurrogate(c)))
            Invalid();
        return value!;
    }

    private static void Properties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                Invalid();
        if (seen.Count != names.Length)
            Invalid();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new InvalidOperationException();
}
