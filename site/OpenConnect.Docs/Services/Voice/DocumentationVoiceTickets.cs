using System.Security.Cryptography;
using OpenConnect.Abstractions;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>Short-lived, single-use references to server-held visitor credentials.</summary>
internal sealed class DocumentationVoiceTickets(TimeProvider? clock = null)
{
    private const int Capacity = 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public string Issue(string sessionId, string visitorToken)
    {
        if (!IsCanonicalSession(sessionId) || string.IsNullOrWhiteSpace(visitorToken))
            throw new ArgumentException("A valid session and visitor identity are required.");
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _entries.Remove(key);
            if (_entries.Count >= Capacity)
                throw new InvalidOperationException("Voice connection capacity is unavailable.");
            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _entries.Add(ticket, new Entry(sessionId, visitorToken, now.AddSeconds(30)));
            return ticket;
        }
    }

    public bool TryConsume(string ticket, string sessionId, out string? visitorToken)
    {
        visitorToken = null;
        if (ticket.Length != 43 || !IsCanonicalSession(sessionId))
            return false;
        lock (_sync)
        {
            if (!_entries.Remove(ticket, out var entry) || entry.Expires <= _clock.GetUtcNow() ||
                !string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                return false;
            visitorToken = entry.VisitorToken;
            return true;
        }
    }

    private static bool IsCanonicalSession(string value)
    {
        if (value.Length != 26)
            return false;
        try
        { var id = Ulid.Parse(value); return id != default && id.ToString() == value; }
        catch (FormatException) { return false; }
    }

    private sealed record Entry(string SessionId, string VisitorToken, DateTimeOffset Expires);
}
