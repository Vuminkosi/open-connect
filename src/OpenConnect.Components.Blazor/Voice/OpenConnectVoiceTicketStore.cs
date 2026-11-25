using System.Security.Cryptography;
using OpenConnect.Abstractions;

namespace OpenConnect.Components.Blazor.Voice;

/// <summary>
/// Issues and consumes short-lived, single-use, session-bound tickets that reference
/// server-held bearer credentials so browser voice WebSockets never receive raw tokens.
/// </summary>
public sealed class OpenConnectVoiceTicketStore(TimeProvider? clock = null)
{
    private const int Capacity = 1024;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public string Issue(string sessionId, string bearerToken)
    {
        if (!IsCanonicalSession(sessionId) || string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("A valid canonical session ID and bearer token are required.");

        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _entries.Remove(key);

            if (_entries.Count >= Capacity)
                throw new InvalidOperationException("Voice connection ticket capacity is unavailable.");

            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            _entries.Add(ticket, new Entry(sessionId, bearerToken, now.Add(DefaultLifetime)));
            return ticket;
        }
    }

    public bool TryConsume(string ticket, string sessionId, out string? bearerToken)
    {
        bearerToken = null;
        if (string.IsNullOrEmpty(ticket) || ticket.Length != 43 || !IsCanonicalSession(sessionId))
            return false;

        lock (_sync)
        {
            if (!_entries.Remove(ticket, out var entry) ||
                entry.Expires <= _clock.GetUtcNow() ||
                !string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            bearerToken = entry.BearerToken;
            return true;
        }
    }

    private static bool IsCanonicalSession(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 26)
            return false;

        try
        {
            var id = Ulid.Parse(value);
            return id != default && string.Equals(id.ToString(), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record Entry(string SessionId, string BearerToken, DateTimeOffset Expires);
}
