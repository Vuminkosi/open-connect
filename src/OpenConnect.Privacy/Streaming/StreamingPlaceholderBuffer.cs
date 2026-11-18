using System.Text;
using OpenConnect.Privacy.Tokenization;

namespace OpenConnect.Privacy.Streaming;

/// <summary>
/// Buffers streaming text chunks across token split boundaries to guarantee no partial tokens
/// or unmasked secrets leak into stream emission before resolution.
/// </summary>
public sealed class StreamingPlaceholderBuffer
{
    private const int MaxTokenLength = 64;
    private const string TokenPrefix = "[OC_";
    private readonly StringBuilder _buffer = new();
    private readonly ITokenVault? _vault;
    private readonly PrivacyScope? _scope;
    private readonly Func<string, ValueTask<string?>>? _resolver;

    /// <summary>Initializes a buffer with a token vault and scope for resolution.</summary>
    public StreamingPlaceholderBuffer(ITokenVault vault, PrivacyScope scope)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    /// <summary>Initializes a buffer with a custom resolver delegate.</summary>
    public StreamingPlaceholderBuffer(Func<string, ValueTask<string?>> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Processes a streaming chunk and returns any safe-to-emit text.</summary>
    /// <param name="chunk">Streaming text chunk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved and safe output string.</returns>
    public async ValueTask<string> ProcessChunkAsync(string chunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(chunk) && _buffer.Length == 0) return string.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        var output = new StringBuilder();
        var index = 0;

        while (index < chunk.Length || (_buffer.Length > 0 && index == chunk.Length))
        {
            if (_buffer.Length > 0)
            {
                // We are currently buffering a potential token
                if (index < chunk.Length)
                {
                    var ch = chunk[index++];
                    _buffer.Append(ch);
                }

                var bufStr = _buffer.ToString();

                // Check if buffer can still be a token
                if (!IsPrefixOfToken(bufStr))
                {
                    // Not a token prefix - flush buffer
                    output.Append(bufStr);
                    _buffer.Clear();
                    continue;
                }

                // Check if token is completed
                if (bufStr.Length >= 6 && bufStr.EndsWith(']'))
                {
                    var resolved = await ResolveTokenAsync(bufStr, cancellationToken).ConfigureAwait(false);
                    output.Append(resolved ?? bufStr);
                    _buffer.Clear();
                    continue;
                }

                // If buffer exceeded maximum possible token length, it's not a valid token
                if (_buffer.Length > MaxTokenLength)
                {
                    output.Append(bufStr);
                    _buffer.Clear();
                    continue;
                }

                // Continue buffering next characters
                if (index >= chunk.Length)
                {
                    break;
                }
            }
            else
            {
                // Buffer is empty, look for start of token '['
                var nextBracket = chunk.IndexOf('[', index);
                if (nextBracket < 0)
                {
                    output.Append(chunk, index, chunk.Length - index);
                    break;
                }

                if (nextBracket > index)
                {
                    output.Append(chunk, index, nextBracket - index);
                }

                _buffer.Append('[');
                index = nextBracket + 1;
            }
        }

        return output.ToString();
    }

    /// <summary>Flushes any remaining buffered content when the stream ends.</summary>
    public async ValueTask<string> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_buffer.Length == 0) return string.Empty;

        var bufStr = _buffer.ToString();
        _buffer.Clear();

        if (bufStr.StartsWith(TokenPrefix, StringComparison.Ordinal) && bufStr.EndsWith(']'))
        {
            var resolved = await ResolveTokenAsync(bufStr, cancellationToken).ConfigureAwait(false);
            return resolved ?? bufStr;
        }

        return bufStr;
    }

    private static bool IsPrefixOfToken(string s)
    {
        if (s.Length <= TokenPrefix.Length)
        {
            return TokenPrefix.StartsWith(s, StringComparison.Ordinal);
        }
        return s.StartsWith(TokenPrefix, StringComparison.Ordinal);
    }

    private async ValueTask<string?> ResolveTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (_resolver is not null)
        {
            return await _resolver(token).ConfigureAwait(false);
        }
        if (_vault is not null && _scope is not null)
        {
            return await _vault.ResolveAsync(token, _scope, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
