using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Streaming;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class StreamingPlaceholderBufferTests
{
    private static readonly PrivacyScope Scope = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), "stream");

    [Fact]
    public async Task RestoresTokenWhenSplitAcrossChunks()
    {
        using var vault = new TokenVault(new byte[32]);
        var token = await vault.StoreAsync("sensitive@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(10));

        var buffer = new StreamingPlaceholderBuffer(vault, Scope);

        // Split token across 3 chunks: "[OC_", first half, second half + "]"
        var chunk1 = $"Hello {token[..5]}";
        var chunk2 = token[5..20];
        var chunk3 = $"{token[20..]} and more text";

        var out1 = await buffer.ProcessChunkAsync(chunk1);
        var out2 = await buffer.ProcessChunkAsync(chunk2);
        var out3 = await buffer.ProcessChunkAsync(chunk3);
        var out4 = await buffer.FlushAsync();

        var combined = out1 + out2 + out3 + out4;
        Assert.Equal("Hello sensitive@example.com and more text", combined);

        // Assert that during chunk1 and chunk2, no partial token or partial secret leaked
        Assert.Equal("Hello ", out1);
        Assert.Equal(string.Empty, out2);
        Assert.Equal("sensitive@example.com and more text", out3);
    }

    [Theory]
    [InlineData(1)] // 1 character per chunk!
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public async Task PropertyTest_EveryChunkSizeReconstructsIdenticalStream(int chunkSize)
    {
        using var vault = new TokenVault(new byte[32]);
        var token1 = await vault.StoreAsync("user@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(10));
        var token2 = await vault.StoreAsync("secret_key_123", "SECRET", Scope, TimeSpan.FromMinutes(10));

        var fullInput = $"Prefix text {token1} middle non-token text [brackets] and then {token2} suffix.";
        var expectedOutput = "Prefix text user@example.com middle non-token text [brackets] and then secret_key_123 suffix.";

        var buffer = new StreamingPlaceholderBuffer(vault, Scope);
        var sb = new StringBuilder();

        for (var i = 0; i < fullInput.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, fullInput.Length - i);
            var chunk = fullInput.Substring(i, len);
            var part = await buffer.ProcessChunkAsync(chunk);
            sb.Append(part);
        }
        sb.Append(await buffer.FlushAsync());

        Assert.Equal(expectedOutput, sb.ToString());
    }

    [Fact]
    public async Task PropertyTest_EverySingleSplitPointIsEquivalentlyRestored()
    {
        using var vault = new TokenVault(new byte[32]);
        var token = await vault.StoreAsync("alice@test.corp", "EMAIL", Scope, TimeSpan.FromMinutes(10));
        var fullInput = $"The user email is {token}.";
        var expectedOutput = "The user email is alice@test.corp.";

        // Test EVERY single possible split index from 1 to fullInput.Length - 1
        for (var split = 1; split < fullInput.Length; split++)
        {
            var chunk1 = fullInput[..split];
            var chunk2 = fullInput[split..];

            var buffer = new StreamingPlaceholderBuffer(vault, Scope);
            var out1 = await buffer.ProcessChunkAsync(chunk1);
            var out2 = await buffer.ProcessChunkAsync(chunk2);
            var out3 = await buffer.FlushAsync();

            var result = out1 + out2 + out3;
            Assert.Equal(expectedOutput, result);
        }
    }
}
