using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Documents;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Services.DocumentDemo;

internal interface IDocumentationDocumentDemo
{
    Task<DocumentExtractionResult> ProcessAsync(Stream input, CancellationToken cancellationToken);
}

internal sealed class DocumentationDocumentConfiguration(string socket, Uri extractionOrigin, string workerToken,
    string inferenceToken, string signingKey, bool allowInsecureHttp)
{
    internal string Socket { get; } = socket;
    internal Uri ExtractionOrigin { get; } = extractionOrigin;
    internal string WorkerToken { get; } = workerToken;
    internal string InferenceToken { get; } = inferenceToken;
    internal string SigningKey { get; } = signingKey;
    internal bool AllowInsecureHttp { get; } = allowInsecureHttp;
}

internal static class DocumentationDocumentRegistration
{
    internal static IServiceCollection AddDocumentationDocumentDemo(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("OpenConnect:DocumentDemo:Enabled"))
            return services;
        if (!environment.IsDevelopment() || !services.Any(service => service.ServiceType == typeof(DocumentationGatewayVisitorTokens)))
            throw new InvalidOperationException("The document example requires development and documentation visitor identity.");
        var prefix = "OpenConnect:DocumentDemo:";
        var socket = configuration[prefix + "DocumentSocket"];
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var inferenceToken = configuration["OPENCONNECT_INFERENCE_TOKEN"];
        var key = configuration["OPENCONNECT_WORKER_SIGNING_KEY"];
        var allowHttp = configuration.GetValue<bool>(prefix + "AllowInsecureHttp");
        if (string.IsNullOrWhiteSpace(socket) || !Path.IsPathFullyQualified(socket) ||
            !Uri.TryCreate(configuration[prefix + "ExtractionOrigin"], UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttps && !(allowHttp && origin.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("The document example worker settings are invalid.");
        if (string.IsNullOrWhiteSpace(inferenceToken))
            inferenceToken = token;
        if (inferenceToken.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("The document example worker settings are invalid.");
        byte[] keyBytes;
        try
        { keyBytes = Convert.FromHexString(key); }
        catch (FormatException) { throw new InvalidOperationException("The document example worker settings are invalid."); }
        try
        { if (keyBytes.Length < 32) throw new InvalidOperationException("The document example worker settings are invalid."); }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
        services.AddOpenConnectRulesDetector();
        services.EnsureHostPrivacyServices();
        services.AddSingleton(new DocumentationDocumentConfiguration(socket, origin, token, inferenceToken, key, allowHttp));
        services.AddSingleton(new DocumentationDocumentCapacity());
        services.AddScoped<IDocumentationDocumentDemo, DocumentationDocumentDemo>();
        return services;
    }
}

internal sealed class DocumentationDocumentCapacity : IDisposable
{
    internal SemaphoreSlim Slots { get; } = new(2, 2);
    public void Dispose() => Slots.Dispose();
}

internal sealed class DocumentationDocumentDemo(DocumentationGatewayVisitorIdentity visitor,
    DocumentationGatewayVisitorTokens tokens, DocumentationDocumentConfiguration settings,
    DocumentationDocumentCapacity capacity, IPrivacyFirewall firewall, IOptions<PrivacyOptions> privacy)
    : IDocumentationDocumentDemo
{
    private const string Schema = """{"type":"object","additionalProperties":false,"required":["reference"],"properties":{"reference":{"type":["string","null"]}}}""";

    public async Task<DocumentExtractionResult> ProcessAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!tokens.TryValidate(visitor.Token, out var tenant, out var principal))
            throw new UnauthorizedAccessException();
        var context = new EgressContext(new TenantId(Ulid.Parse(tenant)), new PrincipalId(Ulid.Parse(principal)),
            new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
        bool Authorized(EgressContext scope) => tokens.TryValidate(visitor.Token, out var currentTenant, out var currentPrincipal) &&
            currentTenant == tenant && currentPrincipal == principal && scope == context;
        if (!await capacity.Slots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Document example is busy.");
        byte[]? key = null;
        byte[]? content = null;
        var chunk = new byte[8192];
        using var buffer = new MemoryStream();
        try
        {
            using var uploadDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            int count;
            while ((count = await input.ReadAsync(chunk, uploadDeadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 10 * 1024 * 1024)
                    throw new ArgumentException("Invalid document size.", nameof(input));
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length == 0)
                throw new ArgumentException("Invalid document size.", nameof(input));
            content = buffer.ToArray();
            key = Convert.FromHexString(settings.SigningKey);
            using var documents = new DocumentWorkerClient(settings.Socket, settings.WorkerToken, key, firewall, privacy,
                (scope, kind, _, _) => ValueTask.FromResult(Authorized(scope) && kind == "documents.ocr"));
            using var extraction = new ExtractionWorkerClient(settings.ExtractionOrigin, settings.InferenceToken, key, firewall,
                (scope, _, _, _) => ValueTask.FromResult(Authorized(scope)), settings.AllowInsecureHttp);
            using var schema = JsonDocument.Parse(Schema);
            return await new DocumentExtractionWorkflow(documents, extraction)
                .ExecuteAsync(context, content, schema.RootElement, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            if (content is not null)
                CryptographicOperations.ZeroMemory(content);
            if (buffer.TryGetBuffer(out var memory))
                CryptographicOperations.ZeroMemory(memory.AsSpan());
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            capacity.Slots.Release();
        }
    }
}
