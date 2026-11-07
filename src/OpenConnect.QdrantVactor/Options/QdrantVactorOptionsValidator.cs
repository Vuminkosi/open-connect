using Microsoft.Extensions.Options;

namespace OpenConnect.QdrantVactor;

public sealed class QdrantVactorOptionsValidator : IValidateOptions<QdrantVactorOptions>
{
    public ValidateOptionsResult Validate(string? name, QdrantVactorOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        if (options.Endpoint is not { IsAbsoluteUri: true } endpoint || endpoint.Scheme is not ("http" or "https"))
            return ValidateOptionsResult.Fail("A valid HTTP(S) Qdrant endpoint is required when QdrantVactor is enabled.");
        if (string.IsNullOrWhiteSpace(options.CollectionName)) return ValidateOptionsResult.Fail("A collection name is required.");
        if (options.VectorSize <= 0) return ValidateOptionsResult.Fail("VectorSize must be positive.");
        return ValidateOptionsResult.Success;
    }
}
