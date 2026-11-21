using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public static class CloudHostPrivacyEvidenceEndpoints
{
    public static void MapCloudHostPrivacyEvidence(this WebApplication app, CloudGatewayClientTrust trust, bool databaseConfigured)
    {
        app.MapPost("/cloud/internal/v1/privacy/evidence", (HttpContext context, CancellationToken cancellationToken) =>
            HandleAsync(context, trust, databaseConfigured, cancellationToken));
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, CloudGatewayClientTrust trust,
        bool databaseConfigured, CancellationToken cancellationToken)
    {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !trust.IsTrusted(context.Connection.ClientCertificate, DateTimeOffset.UtcNow)) return Results.Unauthorized();
            if (!databaseConfigured) return Results.StatusCode(503);
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter) ||
                header.Parameter.Length > 160 || header.Parameter.Any(char.IsWhiteSpace)) return Results.Unauthorized();
            CloudCredentialIdentity? identity;
            try { identity = await context.RequestServices.GetRequiredService<CloudCredentialService>().ValidateAsync(header.Parameter, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return Results.StatusCode(503); }
            if (identity is null) return Results.Unauthorized();
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) ||
                !string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                type.Parameters.Count > 1 || type.Parameters.Count == 1 &&
                !string.Equals(type.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Headers.ContainsKey("Content-Encoding")) return Results.StatusCode(415);
            const int maximum = 8192;
            if (context.Request.ContentLength > maximum) return Results.StatusCode(413);
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
                if (length > maximum) return Results.StatusCode(413);
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                var report = CloudHostPrivacyReportParser.Parse(document.RootElement, identity);
                var store = context.RequestServices.GetRequiredService<ICloudHostPrivacyEvidenceStore>();
                var accepted = await store.AppendAsync(identity, report, deadline.Token);
                return accepted ? Results.Ok(new { eventId = report.EventId }) : Results.Conflict();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return Results.StatusCode(408); }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
            { return Results.BadRequest(); }
            catch { return Results.StatusCode(503); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
