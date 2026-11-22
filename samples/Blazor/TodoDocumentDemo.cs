using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Documents;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Explicitly enabled, development-only OCR and extraction example.</summary>
internal sealed class TodoDocumentDemo(TodoDocumentDemoAccess access, IConfiguration configuration,
    IPrivacyFirewall firewall, IOptions<PrivacyOptions> privacy)
{
    private const string Schema = """{"type":"object","additionalProperties":false,"required":["reference"],"properties":{"reference":{"type":["string","null"]}}}""";

    internal async Task<DocumentExtractionResult> ProcessAsync(ClaimsPrincipal user, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var context = access.CreateContext(user);
        var socket = configuration["OpenConnect:DocumentDemo:DocumentSocket"];
        var origin = configuration["OpenConnect:DocumentDemo:ExtractionOrigin"];
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var inferenceToken = configuration["OPENCONNECT_INFERENCE_TOKEN"];
        if (string.IsNullOrWhiteSpace(inferenceToken)) inferenceToken = token;
        var keyText = configuration["OPENCONNECT_WORKER_SIGNING_KEY"];
        if (string.IsNullOrWhiteSpace(socket) || !Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(keyText))
            throw new InvalidOperationException("Document example is not configured.");
        byte[] key;
        try { key = Convert.FromHexString(keyText); }
        catch (FormatException) { throw new InvalidOperationException("Document example is not configured."); }
        try
        {
            using var documents = new DocumentWorkerClient(socket, token, key, firewall, privacy,
                (scope, kind, _, _) => ValueTask.FromResult(access.Authorizes(scope) && kind == "documents.ocr"));
            using var extraction = new ExtractionWorkerClient(endpoint, inferenceToken!, key, firewall,
                (scope, _, _, _) => ValueTask.FromResult(access.Authorizes(scope)),
                configuration.GetValue<bool>("OpenConnect:DocumentDemo:AllowInsecureHttp"));
            using var schema = JsonDocument.Parse(Schema);
            return await new DocumentExtractionWorkflow(documents, extraction)
                .ExecuteAsync(context, content, schema.RootElement, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
