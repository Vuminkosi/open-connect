using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.LiveUI;

public static class LiveUISchema
{
    public const string CurrentVersion = "openconnect.liveui/v1";
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveUITargetKind>))]
public enum LiveUITargetKind { Button, Link, Input, Select, Checkbox, Tab, Heading, Region, List, Table }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUICapability>))]
public enum LiveUICapability { Activate, Close, Filter, Focus, Highlight, Inspect, Scroll, SetValue, Select, WaitForState }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIActionType>))]
public enum LiveUIActionType { Navigate, Open, Close, Click, Focus, Scroll, Highlight, ClearHighlight, SetValue, Select, SelectTab, Inspect, WaitForState }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIActionOutcome>))]
public enum LiveUIActionOutcome { Completed, Denied, Ambiguous, MissingTarget, Uncertain, Failed, Conflict }
[JsonConverter(typeof(JsonStringEnumConverter<LiveUIAuthorizationOutcome>))]
public enum LiveUIAuthorizationOutcome { Allowed, Denied, TimedOut }

public sealed record LiveUIManifest(string SchemaVersion, string BuildId, string ContentHash, IReadOnlyList<LiveUIRoute> Routes);
public sealed record LiveUIRoute(string Id, string Path, string? Title, IReadOnlyList<LiveUITarget> Targets);
public sealed record LiveUITarget(
    string Id,
    LiveUITargetKind Kind,
    string Label,
    IReadOnlySet<LiveUICapability> Capabilities,
    string? Role = null,
    string? Selector = null,
    string? ParentId = null,
    bool RequiresConfirmation = false);

/// <summary>Typed request. Authorization identity comes from the host policy, never from model output.</summary>
public sealed record LiveUIAction(
    string ActionId,
    string SchemaVersion,
    string SessionId,
    string ManifestHash,
    string RouteId,
    string? TargetId,
    LiveUIActionType Type,
    LiveUIActionParameters Parameters,
    TimeSpan Timeout)
{
    /// <summary>The concrete path observed by the trusted host when creating this action.</summary>
    [JsonIgnore]
    public string? ExpectedPath { get; init; }
}

public sealed record LiveUIActionParameters(
    string? Value = null,
    string? DestinationRouteId = null,
    string? StateName = null,
    string? StateValue = null,
    string? ScrollAlignment = null,
    int HighlightDurationMilliseconds = 0);

public sealed record LiveUIActionReceipt(
    string ActionId,
    string SessionId,
    LiveUIActionOutcome Outcome,
    string Code,
    bool PostconditionVerified,
    DateTimeOffset CompletedAt,
    string? VerifiedState = null)
{
    /// <summary>Host policy decision when evaluated; this does not indicate user confirmation or action success.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveUIAuthorizationOutcome? AuthorizationOutcome { get; init; }
}

public sealed record LiveUIExecutionContext(LiveUIManifest Manifest, LiveUIRoute Route, LiveUITarget? Target, LiveUIAction Action);
public sealed record LiveUIAuthorizationDecision(bool Allowed, string Code, bool UserConfirmed = false);
public sealed record LiveUIRuntimeMeasurements(
    double? TargetResolutionMilliseconds = null,
    double? BrowserActionMilliseconds = null);
public sealed record LiveUIRuntimeResult(
    LiveUIActionOutcome Outcome,
    string Code,
    bool PostconditionVerified,
    string? VerifiedState = null,
    LiveUIRuntimeMeasurements? Measurements = null);

public static class LiveUIManifestHasher
{
    public static string ComputeContentHash(LiveUIManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new
        {
            schemaVersion = manifest.SchemaVersion,
            buildId = manifest.BuildId,
            routes = manifest.Routes
                .OrderBy(route => route.Id, StringComparer.Ordinal)
                .Select(route => new
                {
                    route.Id,
                    route.Path,
                    route.Title,
                    targets = route.Targets
                        .OrderBy(target => target.Id, StringComparer.Ordinal)
                        .Select(target => new
                        {
                            target.Id,
                            kind = target.Kind.ToString(),
                            target.Label,
                            capabilities = target.Capabilities.Select(value => value.ToString()).Order(StringComparer.Ordinal),
                            target.Role,
                            target.Selector,
                            target.ParentId,
                            target.RequiresConfirmation
                        })
                })
        };
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }
}

public interface ILiveUIActionAuthorizer
{
    ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Asks the trusted host user to confirm a single already-authorized action.</summary>
public interface ILiveUIActionConfirmation
{
    /// <summary>Returns true only when the user confirms the exact supplied action.</summary>
    ValueTask<bool> ConfirmAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ILiveUIRuntime
{
    ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ILiveUIManifestProvider
{
    ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed class LiveUIManifestValidator
{
    public IReadOnlyList<string> Validate(LiveUIManifest? manifest)
    {
        if (manifest is null)
            return ["manifest_missing"];

        var errors = new List<string>();
        if (manifest.SchemaVersion != LiveUISchema.CurrentVersion)
            errors.Add("manifest_schema_unsupported");
        if (string.IsNullOrWhiteSpace(manifest.BuildId))
            errors.Add("manifest_build_id_missing");
        if (string.IsNullOrWhiteSpace(manifest.ContentHash))
            errors.Add("manifest_hash_missing");
        if (manifest.Routes is null || manifest.Routes.Count == 0)
        {
            errors.Add("manifest_routes_missing");
            return errors;
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var routePaths = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in manifest.Routes)
        {
            if (route is null || string.IsNullOrWhiteSpace(route.Id) || string.IsNullOrWhiteSpace(route.Path))
            {
                errors.Add("manifest_route_invalid");
                continue;
            }
            if (!route.Path.StartsWith("/", StringComparison.Ordinal) ||
                route.Path.StartsWith("//", StringComparison.Ordinal) || route.Path.Contains('\\') ||
                route.Path.Contains('?') || route.Path.Contains('#') ||
                route.Path.Split('/').Any(segment => segment is "." or ".."))
                errors.Add("manifest_route_path_invalid");
            if (!routeIds.Add(route.Id))
                errors.Add("manifest_route_id_duplicate");
            if (!routePaths.Add(route.Path))
                errors.Add("manifest_route_path_duplicate");
            if (route.Targets is null)
            {
                errors.Add("manifest_targets_missing");
                continue;
            }

            var routeTargets = new Dictionary<string, LiveUITarget>(StringComparer.Ordinal);
            foreach (var target in route.Targets)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.Label) ||
                    !Enum.IsDefined(target.Kind) || target.Capabilities is null || target.Capabilities.Any(capability => !Enum.IsDefined(capability)))
                {
                    errors.Add("manifest_target_invalid");
                    continue;
                }
                if (target.Selector is { Length: > 512 })
                    errors.Add("manifest_selector_invalid");
                if (!targetIds.Add(target.Id))
                    errors.Add("manifest_target_id_duplicate");
                routeTargets.TryAdd(target.Id, target);
            }

            var parentVisitStates = new Dictionary<string, byte>(StringComparer.Ordinal);
            var hasParentCycle = false;
            foreach (var target in routeTargets.Values)
            {
                if (target.ParentId is null)
                    continue;
                if (!routeTargets.ContainsKey(target.ParentId))
                {
                    errors.Add("manifest_target_parent_missing");
                }

                if (parentVisitStates.ContainsKey(target.Id))
                    continue;

                var path = new List<string>();
                var current = target;
                byte state;
                while (!parentVisitStates.TryGetValue(current.Id, out state))
                {
                    parentVisitStates[current.Id] = 1;
                    path.Add(current.Id);
                    if (current.ParentId is not { } parentId || !routeTargets.TryGetValue(parentId, out var next))
                        break;
                    current = next;
                }
                if (state == 1)
                    hasParentCycle = true;
                foreach (var targetId in path)
                    parentVisitStates[targetId] = 2;
            }
            if (hasParentCycle)
                errors.Add("manifest_target_parent_cycle");
        }
        if (errors.Count == 0)
        {
            var expectedHash = Encoding.ASCII.GetBytes(LiveUIManifestHasher.ComputeContentHash(manifest));
            var providedHash = Encoding.ASCII.GetBytes(manifest.ContentHash);
            if (providedHash.Length != expectedHash.Length ||
                !CryptographicOperations.FixedTimeEquals(providedHash, expectedHash))
                errors.Add("manifest_hash_mismatch");
        }
        return errors;
    }
}
