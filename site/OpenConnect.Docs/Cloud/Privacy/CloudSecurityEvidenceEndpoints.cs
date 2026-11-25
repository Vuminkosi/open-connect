using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenConnect.Abstractions;

namespace OpenConnect.Docs.Cloud;

/// <summary>
/// Additive <c>openconnect.security-evidence.v1</c> ingestion and tenant-scoped query endpoints (FW-100).
/// Preserves existing <c>openconnect.privacy-evidence.v1</c> endpoints without modification.
/// </summary>
public static class CloudSecurityEvidenceEndpoints
{
    /// <summary>
    /// Maps the authenticated host/cloud ingestion endpoint <c>POST /cloud/internal/v1/security/evidence</c>.
    /// Binds tenant and application identity strictly from the authenticated credential, never trusting payload tenant IDs.
    /// </summary>
    public static void MapCloudSecurityEvidenceIngestion(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/security/evidence", (HttpContext context, CancellationToken cancellationToken) =>
            HandleIngestionAsync(context, trust, databaseConfigured, cancellationToken));
    }

    /// <summary>
    /// Maps tenant-scoped query endpoints under <c>/cloud/api/v1</c> for <c>openconnect.security-evidence.v1</c>.
    /// </summary>
    public static void MapCloudSecurityEvidenceQueryEndpoints(this RouteGroupBuilder group, string? identityIssuer)
    {
        group.MapGet("/tenants/{tenantId:guid}/security/evidence", async (
            Guid tenantId,
            string? capability,
            string? decision,
            string? outcome,
            string? from,
            string? to,
            string? traceId,
            int? skip,
            int? take,
            ClaimsPrincipal principal,
            HttpContext context,
            [FromServices] CloudSecurityEvidenceQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty)
                return Results.BadRequest();

            if (!TryParseOptionalDecision(decision, out var parsedDecision) ||
                !TryParseOptionalOutcome(outcome, out var parsedOutcome) ||
                !TryParseOptionalTimestamp(from, out var parsedFrom) ||
                !TryParseOptionalTimestamp(to, out var parsedTo))
            {
                return Results.BadRequest();
            }

            var filter = new CloudSecurityEvidenceFilter(
                Capability: string.IsNullOrWhiteSpace(capability) ? null : capability.Trim(),
                Decision: parsedDecision,
                Outcome: parsedOutcome,
                FromUtc: parsedFrom,
                ToUtc: parsedTo,
                TraceId: string.IsNullOrWhiteSpace(traceId) ? null : traceId.Trim(),
                Skip: skip ?? 0,
                Take: take ?? 50);

            var page = await queryService.QueryTenantAsync(subject, tenantId, filter, cancellationToken);
            if (page is null)
                return Results.NotFound();

            return Results.Json(page, CloudSecurityEvidenceJson.Options);
        });

        group.MapGet("/tenants/{tenantId:guid}/security/evidence/{eventId:guid}", async (
            Guid tenantId,
            Guid eventId,
            ClaimsPrincipal principal,
            HttpContext context,
            [FromServices] CloudSecurityEvidenceQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var subject = CloudSubjectResolver.FromPrincipal(principal, identityIssuer);
            if (subject is null)
                return Results.Unauthorized();
            if (tenantId == Guid.Empty || eventId == Guid.Empty)
                return Results.BadRequest();

            var item = await queryService.GetEventAsync(subject, tenantId, eventId, cancellationToken);
            return item is null ? Results.NotFound() : Results.Json(item, CloudSecurityEvidenceJson.Options);
        });
    }

    internal static async Task<IResult> HandleIngestionAsync(
        HttpContext context,
        CloudGatewayClientTrust trust,
        bool databaseConfigured,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow))
            return Results.Unauthorized();
        if (!databaseConfigured)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length > 160 ||
            header.Parameter.Any(char.IsWhiteSpace))
        {
            return Results.Unauthorized();
        }

        CloudCredentialIdentity? identity;
        try
        {
            identity = await context.RequestServices.GetRequiredService<CloudCredentialService>()
                .ValidateAsync(header.Parameter, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (identity is null)
            return Results.Unauthorized();

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ||
            !string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            type.Parameters.Count > 1 ||
            (type.Parameters.Count == 1 && !string.Equals(type.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase)) ||
            context.Request.Headers.ContainsKey("Content-Encoding"))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        const int maximum = 8192;
        if (context.Request.ContentLength > maximum)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var buffer = new byte[maximum + 1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), deadline.Token);
                if (count == 0) break;
                length += count;
            }
            if (length > maximum)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var evidence = CloudSecurityEvidenceParser.Parse(document.RootElement, identity);
            var store = context.RequestServices.GetRequiredService<ICloudSecurityEvidenceStore>();
            var result = await store.RecordAsync(evidence, deadline.Token);
            if (!result.Recorded)
                return Results.Conflict();

            return Results.Ok(new
            {
                eventId = evidence.EventId,
                deduplicated = result.Deduplicated,
                integrityDigest = evidence.IntegrityDigest,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            return Results.BadRequest();
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool TryParseOptionalDecision(string? value, out SecurityEvidenceDecision? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        result = value.Trim().ToLowerInvariant() switch
        {
            "allow" => SecurityEvidenceDecision.Allow,
            "mask" => SecurityEvidenceDecision.Mask,
            "redact" => SecurityEvidenceDecision.Redact,
            "block" => SecurityEvidenceDecision.Block,
            "quarantine" => SecurityEvidenceDecision.Quarantine,
            "review" => SecurityEvidenceDecision.Review,
            "unavailable" => SecurityEvidenceDecision.Unavailable,
            _ => null,
        };
        return result is not null;
    }

    private static bool TryParseOptionalOutcome(string? value, out SecurityEnforcementOutcome? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        result = value.Trim().ToLowerInvariant() switch
        {
            "not_executed" => SecurityEnforcementOutcome.NotExecuted,
            "executed" => SecurityEnforcementOutcome.Executed,
            "prevented" => SecurityEnforcementOutcome.Prevented,
            "unknown" => SecurityEnforcementOutcome.Unknown,
            _ => null,
        };
        return result is not null;
    }

    private static bool TryParseOptionalTimestamp(string? value, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;
        result = parsed.ToUniversalTime();
        return true;
    }
}
