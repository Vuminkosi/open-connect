using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Analytics;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Services.AnalyticsDemo;

internal interface IDocumentationAnalyticsDemo
{
    Task<JsonDocument> AnalyzeAsync(CancellationToken cancellationToken);
}

internal sealed class DocumentationAnalyticsConfiguration(Uri origin, string token, string signingKey, bool allowHttp)
{
    internal Uri Origin { get; } = origin;
    internal string Token { get; } = token;
    internal string SigningKey { get; } = signingKey;
    internal bool AllowHttp { get; } = allowHttp;
}

internal static class DocumentationAnalyticsRegistration
{
    internal static IServiceCollection AddDocumentationAnalyticsDemo(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        const string prefix = "OpenConnect:AnalyticsDemo:";
        if (!configuration.GetValue<bool>(prefix + "Enabled")) return services;
        if (!environment.IsDevelopment() || !services.Any(service => service.ServiceType == typeof(DocumentationGatewayVisitorTokens)))
            throw new InvalidOperationException("The analytics example requires development and documentation visitor identity.");
        var allowHttp = configuration.GetValue<bool>(prefix + "AllowInsecureHttp");
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        var signingKey = configuration["OPENCONNECT_WORKER_SIGNING_KEY"];
        if (!Uri.TryCreate(configuration[prefix + "Origin"], UriKind.Absolute, out var origin) ||
            (origin.Scheme != Uri.UriSchemeHttps && !(allowHttp && origin.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) ||
            string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException("The analytics example worker settings are invalid.");
        byte[] key;
        try { key = Convert.FromHexString(signingKey); }
        catch (FormatException) { throw new InvalidOperationException("The analytics example worker settings are invalid."); }
        try { if (key.Length < 32) throw new InvalidOperationException("The analytics example worker settings are invalid."); }
        finally { CryptographicOperations.ZeroMemory(key); }
        services.AddOpenConnectRulesDetector();
        services.EnsureHostPrivacyServices();
        services.AddSingleton(new DocumentationAnalyticsConfiguration(origin, token, signingKey, allowHttp));
        services.AddSingleton<DocumentationAnalyticsCapacity>();
        services.AddScoped<IDocumentationAnalyticsDemo, DocumentationAnalyticsDemo>();
        return services;
    }
}

internal sealed class DocumentationAnalyticsCapacity : IDisposable
{
    internal SemaphoreSlim Slots { get; } = new(2, 2);
    public void Dispose() => Slots.Dispose();
}

internal sealed class DocumentationAnalyticsDemo(DocumentationGatewayVisitorIdentity visitor,
    DocumentationGatewayVisitorTokens tokens, DocumentationAnalyticsConfiguration settings,
    DocumentationAnalyticsCapacity capacity, IPrivacyFirewall firewall) : IDocumentationAnalyticsDemo
{
    public async Task<JsonDocument> AnalyzeAsync(CancellationToken cancellationToken)
    {
        if (!tokens.TryValidate(visitor.Token, out var tenant, out var principal)) throw new UnauthorizedAccessException();
        if (!await capacity.Slots.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Analytics example is busy.");
        byte[]? key = null;
        try
        {
            var scope = new EgressContext(new TenantId(Ulid.Parse(tenant)), new PrincipalId(Ulid.Parse(principal)),
                new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
            key = Convert.FromHexString(settings.SigningKey);
            using var client = new AnalyticsWorkerClient(settings.Origin, settings.Token, key, firewall,
                (candidate, _, _) => ValueTask.FromResult(candidate == scope && tokens.TryValidate(visitor.Token, out var t, out var p) && t == tenant && p == principal), settings.AllowHttp);
            using var input = JsonDocument.Parse("""{"feature":"weight","unit":"kg","lineage":"synthetic-example","values":[1,2,3,4,5,100],"trend":true}""");
            return await client.AnalyzeAsync(scope, input.RootElement, cancellationToken).ConfigureAwait(false);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); capacity.Slots.Release(); }
    }
}
