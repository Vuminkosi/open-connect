using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.QdrantVactor;

/// <summary>Test/development-only deterministic placeholder; this is not a semantic embedding model.</summary>
public sealed class DeterministicEmbeddingStub : IEmbeddingGenerator
{
    public ValueTask<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var values = new float[hash.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++) values[i] = (BitConverter.ToUInt32(hash, i * sizeof(float)) / (float)uint.MaxValue) * 2 - 1;
        return ValueTask.FromResult<ReadOnlyMemory<float>>(values);
    }
}
