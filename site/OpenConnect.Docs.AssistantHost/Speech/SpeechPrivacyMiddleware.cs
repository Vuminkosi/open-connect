using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.AssistantHost.Speech;

// The SDK speech contract has no session ID. Use a fresh per-request privacy scope.
internal sealed class SpeechPrivacyMiddleware(RequestDelegate next)
{
    private const int MaximumBodyBytes = 16 * 1024;
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
        { "text", "voiceId", "locale", "format", "speechEpoch", "idempotencyKey" };

    public async Task InvokeAsync(HttpContext context, IEgressGate gate)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/openconnect/v1/speech/synthesize", StringComparison.OrdinalIgnoreCase) ||
            context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        byte[] rewritten;
        try
        {
            if (context.Request.ContentLength > MaximumBodyBytes) { context.Response.StatusCode = 413; return; }
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int count;
            while ((count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) > 0)
            {
                if (buffer.Length + count > MaximumBodyBytes) { context.Response.StatusCode = 413; return; }
                await buffer.WriteAsync(bytes.AsMemory(0, count), context.RequestAborted);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { context.Response.StatusCode = 400; return; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? textName = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Fields.Contains(property.Name) || !seen.Add(property.Name)) { context.Response.StatusCode = 400; return; }
                if (property.Name.Equals("text", StringComparison.OrdinalIgnoreCase)) textName = property.Name;
            }
            if (textName is null || document.RootElement.GetProperty(textName).ValueKind != JsonValueKind.String ||
                document.RootElement.GetProperty(textName).GetString() is not { Length: > 0 and <= 2000 } text)
            { context.Response.StatusCode = 400; return; }
            var tenant = Ulid.Parse(context.User.FindFirstValue("tenant_id") ?? "");
            var principal = Ulid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
            var scope = new EgressContext(new TenantId(tenant), new PrincipalId(principal),
                new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
            var sanitized = await DemandAllowedAsync(gate, text, scope, context.RequestAborted);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == textName || property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString()!;
                if (!string.Equals(await DemandAllowedAsync(gate, value, scope, context.RequestAborted), value, StringComparison.Ordinal))
                { context.Response.StatusCode = 403; return; }
            }
            var body = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            body[textName] = sanitized;
            rewritten = JsonSerializer.SerializeToUtf8Bytes(body);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (PrivacyBlockedException) { context.Response.StatusCode = 403; return; }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        catch (Exception) { context.Response.StatusCode = 503; return; }

        var originalBody = context.Request.Body;
        var originalLength = context.Request.ContentLength;
        using var forwardedBody = new MemoryStream(rewritten, writable: false);
        context.Request.Body = forwardedBody;
        context.Request.ContentLength = rewritten.Length;
        try { await next(context); }
        finally { context.Request.Body = originalBody; context.Request.ContentLength = originalLength; }
    }

    private static async ValueTask<string> DemandAllowedAsync(IEgressGate gate, string value, EgressContext scope,
        CancellationToken cancellationToken)
    {
        var decision = await gate.EvaluateAsync(value, scope, cancellationToken);
        if (decision is null || !decision.Allowed || decision.SanitizedContent is null ||
            decision.Action is not (SecurityPolicyAction.Allow or SecurityPolicyAction.Mask or SecurityPolicyAction.Redact))
            throw new PrivacyBlockedException("Content was blocked by privacy policy.");
        return decision.SanitizedContent;
    }
}
