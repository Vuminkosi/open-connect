using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public static class CloudVoiceOutputPrivacyEndpoints
{
    public static void MapCloudVoiceOutputPrivacy(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured) =>
        app.MapPost("/cloud/internal/v1/voice-synthesis/output-privacy",
            (HttpContext context, CancellationToken cancellationToken) => InspectAsync(context, trust, databaseConfigured, cancellationToken));

    internal static async Task<IResult> InspectAsync(HttpContext context, CloudGatewayClientTrust trust, bool databaseConfigured,
        CancellationToken cancellationToken)
    {
        var (identity, error) = await CloudVoiceQuotaEndpoints.AuthorizeAsync(context, trust, databaseConfigured, cancellationToken);
        if (error is not null)
            return error;
        if (!ReadId(context, "X-Cloud-Request-Id", out var requestId) || !ReadId(context, "X-Cloud-Reservation-Id", out var reservationId) ||
            !ReadId(context, "X-Cloud-Claim-Id", out var claimId))
            return Results.BadRequest();
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContainsKey("Content-Encoding"))
            return Results.StatusCode(415);
        const int maxBody = 65536;
        if (context.Request.ContentLength > maxBody)
            return Results.StatusCode(413);
        var buffer = new byte[maxBody + 1];
        try
        {
            if (!await context.RequestServices.GetRequiredService<ICloudVoiceAccessStore>()
                .IsClaimAllowedAsync(identity!, reservationId, claimId, cancellationToken))
                return Results.StatusCode(503);
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0)
                    break;
                length += count;
            }
            if (length > maxBody)
                return Results.StatusCode(413);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest();
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 2 || properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
                properties.Any(x => x.Name is not ("transcript" or "modelFingerprint") || x.Value.ValueKind != JsonValueKind.String))
                return Results.BadRequest();
            var transcript = document.RootElement.GetProperty("transcript").GetString();
            var fingerprint = document.RootElement.GetProperty("modelFingerprint").GetString();
            if (fingerprint is not { Length: 64 } || fingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return Results.BadRequest();
            var result = await context.RequestServices.GetRequiredService<CloudVoiceOutputPrivacyService>()
                .EvaluateAsync(identity!, requestId, transcript!, cancellationToken);
            if (result.Code == "invalid_request")
                return Results.BadRequest();
            var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            if (!await context.RequestServices.GetRequiredService<ICloudVoiceOutputEvidenceStore>()
                .AppendForCompletedAsync(identity!, requestId, reservationId, claimId, fingerprint, result.Code, traceId, cancellationToken))
                return Results.StatusCode(503);
            return Results.Json(result, statusCode: result.Code switch { "allowed" => 200, "privacy_blocked" => 403, _ => 503 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JsonException) { return Results.BadRequest(); }
        catch { return Results.StatusCode(503); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static bool ReadId(HttpContext context, string name, out Guid id)
    {
        var values = context.Request.Headers[name];
        id = Guid.Empty;
        return values.Count == 1 && Guid.TryParseExact(values.ToString(), "D", out id) && id != Guid.Empty;
    }
}
