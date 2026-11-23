using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Restoration;
using OpenConnect.Privacy.Streaming;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class StreamingPlaceholderBufferTests
{
    private static readonly PrivacyScope Scope = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), "stream");

    private const string FixtureToken = "[OC_StreamFixture]";
    private const string FixtureValue = "blue-square";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BracketAdjacentTokenMatchesFinalOutputAtEverySplit(int precedingBracketCount)
    {
        var literalBrackets = new string('[', precedingBracketCount);
        var input = $"prefix {literalBrackets}{FixtureToken} suffix";
        var vault = new FixtureTokenVault();
        var restored = await new TokenRestorationService(vault)
            .RestoreAsync(input, Scope, new(RestorationTarget.FinalUserOutput));
        Assert.Equal($"prefix {literalBrackets}{FixtureValue} suffix", restored);

        var tokenEnd = input.IndexOf(FixtureToken, StringComparison.Ordinal) + FixtureToken.Length;
        for (var split = 0; split <= input.Length; split++)
        {
            var buffer = new StreamingPlaceholderBuffer(vault, Scope);
            var first = await buffer.ProcessChunkAsync(input[..split]);
            var second = await buffer.ProcessChunkAsync(input[split..]);
            var remainder = await buffer.FlushAsync();

            if (split < tokenEnd)
            {
                Assert.DoesNotContain(FixtureToken[..4], first);
                Assert.DoesNotContain(FixtureValue, first);
            }

            Assert.Equal(restored, first + second + remainder);
        }
    }

    [Theory]
    [InlineData("[x")]
    [InlineData("[OC_literal")]
    public async Task StrayBracketTextIsPreserved(string input)
    {
        var buffer = new StreamingPlaceholderBuffer(
            _ => throw new InvalidOperationException("Literal text must not be resolved."));

        var first = await buffer.ProcessChunkAsync(input[..1]);
        var second = await buffer.ProcessChunkAsync(input[1..]);
        var remainder = await buffer.FlushAsync();

        Assert.Equal(input, first + second + remainder);
        Assert.Equal(string.Empty, await buffer.FlushAsync());
    }

    [Fact]
    public async Task ReachingLengthBoundAndFlushingStillPreserveLiteralText()
    {
        var buffer = new StreamingPlaceholderBuffer(
            _ => throw new InvalidOperationException("Incomplete text must not be resolved."));
        var atBound = "[OC_" + new string('a', 60);

        Assert.Equal(string.Empty, await buffer.ProcessChunkAsync(atBound));
        Assert.Equal(atBound + "x", await buffer.ProcessChunkAsync("x"));
        Assert.Equal(string.Empty, await buffer.FlushAsync());

        Assert.Equal(string.Empty, await buffer.ProcessChunkAsync("[OC_literal"));
        Assert.Equal("[OC_literal", await buffer.FlushAsync());
        Assert.Equal(string.Empty, await buffer.FlushAsync());
    }

    private sealed class FixtureTokenVault : ITokenVault
    {
        public ValueTask<string> StoreAsync(string value, string entityType, PrivacyScope scope, TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> ResolveAsync(string token, PrivacyScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token == FixtureToken && scope == Scope ? FixtureValue : null);
    }

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
