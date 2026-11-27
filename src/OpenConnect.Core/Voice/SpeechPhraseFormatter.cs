using System.Text;
using System.Text.RegularExpressions;

namespace OpenConnect.Core;

/// <summary>A single coherent speakable phrase emitted by <see cref="SpeechPhraseFormatter"/> with source mapping.</summary>
public sealed record SpeakablePhraseSegment(int SegmentIndex, string SpokenText, string SourceText);

/// <summary>Configuration for <see cref="SpeechPhraseFormatter"/> phrase chunking bounds.</summary>
public sealed record SpeechPhraseFormatterOptions(int MinPhraseWords = 8, int MaxPhraseWords = 25);

/// <summary>Stateful streaming text formatter that strips Markdown/code/URLs and emits coherent speakable phrases.</summary>
public sealed class SpeechPhraseFormatter
{
    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\((?:https?:\/\/|www\.)[^\)]+\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RawUrlRegex = new(@"\b(?:https?:\/\/|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingPrefixRegex = new(@"(?m)^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex BlockquotePrefixRegex = new(@"(?m)^\s*>\s?", RegexOptions.Compiled);
    private static readonly Regex ListPrefixRegex = new(@"(?m)^\s*(?:[-*+]|\d+\.)\s+", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"(?m)^\s*(?:[-*_]\s*){3,}$", RegexOptions.Compiled);
    private static readonly Regex InlineFormattingRegex = new(@"(\*\*|__|~~|\*|`)", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr.", "mr.", "mrs.", "ms.", "prof.", "sr.", "jr.", "st.", "vs.", "etc.",
        "e.g.", "i.e.", "u.s.", "u.k.", "u.s.a.", "ph.d.", "inc.", "ltd.", "no.", "fig."
    };

    private readonly int _minPhraseWords;
    private readonly int _maxPhraseWords;
    private readonly StringBuilder _buffer = new();
    private string _pendingHighSurrogate = string.Empty;
    private bool _inCodeFence;
    private int _nextSegmentIndex;

    public SpeechPhraseFormatter(SpeechPhraseFormatterOptions? options = null)
    {
        var effective = options ?? new SpeechPhraseFormatterOptions();
        if (effective.MinPhraseWords < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinPhraseWords must be at least 1.");
        }

        if (effective.MaxPhraseWords < effective.MinPhraseWords)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPhraseWords must be at least MinPhraseWords.");
        }

        _minPhraseWords = effective.MinPhraseWords;
        _maxPhraseWords = effective.MaxPhraseWords;
    }

    /// <summary>Appends an ordered assistant text delta and returns zero or more ready speakable phrase segments.</summary>
    public IReadOnlyList<SpeakablePhraseSegment> AppendDelta(string? delta, bool forceFlush = false)
    {
        var emitted = new List<SpeakablePhraseSegment>();
        if (!string.IsNullOrEmpty(delta))
        {
            var combined = string.IsNullOrEmpty(_pendingHighSurrogate)
                ? delta
                : _pendingHighSurrogate + delta;
            _pendingHighSurrogate = string.Empty;

            if (combined.Length > 0 && char.IsHighSurrogate(combined[^1]))
            {
                _pendingHighSurrogate = combined[^1].ToString();
                combined = combined[..^1];
            }

            AppendFilteredCodeFences(combined);
        }

        ExtractReadySegments(emitted);

        if (forceFlush)
        {
            var flushed = FlushInternal();
            if (flushed is not null)
            {
                emitted.Add(flushed);
            }
        }

        return emitted;
    }

    /// <summary>Flushes any remaining buffered speakable text at the end of a response or phrase timeout.</summary>
    public SpeakablePhraseSegment? Flush()
    {
        if (!string.IsNullOrEmpty(_pendingHighSurrogate))
        {
            _pendingHighSurrogate = string.Empty;
        }

        return FlushInternal();
    }

    /// <summary>Resets all buffered state and segment numbering.</summary>
    public void Reset()
    {
        _buffer.Clear();
        _pendingHighSurrogate = string.Empty;
        _inCodeFence = false;
        _nextSegmentIndex = 0;
    }

    private void AppendFilteredCodeFences(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var fenceIdx = text.IndexOf("```", index, StringComparison.Ordinal);
            if (fenceIdx < 0)
            {
                if (!_inCodeFence)
                {
                    _buffer.Append(text, index, text.Length - index);
                }

                break;
            }

            if (!_inCodeFence && fenceIdx > index)
            {
                _buffer.Append(text, index, fenceIdx - index);
            }

            _inCodeFence = !_inCodeFence;
            index = fenceIdx + 3;

            // If we just entered a code fence, skip optional language tag until newline when present in this chunk.
            if (_inCodeFence)
            {
                var newlineIdx = text.IndexOf('\n', index);
                if (newlineIdx >= 0)
                {
                    index = newlineIdx + 1;
                }
            }
        }
    }

    private void ExtractReadySegments(List<SpeakablePhraseSegment> emitted)
    {
        while (true)
        {
            var raw = _buffer.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var splitIndex = FindSafeSplitIndex(raw);
            if (splitIndex <= 0)
            {
                return;
            }

            var rawSlice = raw[..splitIndex];
            _buffer.Remove(0, splitIndex);

            var spoken = SanitizeForSpeech(rawSlice);
            if (!string.IsNullOrWhiteSpace(spoken))
            {
                emitted.Add(new SpeakablePhraseSegment(_nextSegmentIndex++, spoken, rawSlice.Trim()));
            }
        }
    }

    private SpeakablePhraseSegment? FlushInternal()
    {
        if (_buffer.Length == 0)
        {
            return null;
        }

        var raw = _buffer.ToString();
        _buffer.Clear();

        var spoken = SanitizeForSpeech(raw);
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return null;
        }

        return new SpeakablePhraseSegment(_nextSegmentIndex++, spoken, raw.Trim());
    }

    private int FindSafeSplitIndex(string raw)
    {
        // Do not split if we have an unclosed markdown link or trailing backtick fence prefix.
        if (HasUnclosedMarkdownLink(raw) || raw.EndsWith("`", StringComparison.Ordinal))
        {
            return -1;
        }

        var wordCount = 0;
        var inWord = false;
        var lastSafeClauseEnd = -1;
        var lastSafeMaxWordEnd = -1;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (char.IsHighSurrogate(ch))
            {
                i++;
                if (!inWord)
                {
                    inWord = true;
                    wordCount++;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (inWord)
                {
                    inWord = false;
                    if (wordCount >= _maxPhraseWords && lastSafeMaxWordEnd < 0)
                    {
                        lastSafeMaxWordEnd = i + 1;
                    }
                }

                continue;
            }

            if (!inWord)
            {
                inWord = true;
                wordCount++;
            }

            // Sentence boundary: '.', '!', '?' followed by whitespace (so we know the next token isn't part of a decimal or abbreviation).
            if (ch is '.' or '!' or '?')
            {
                if (i + 1 < raw.Length && char.IsWhiteSpace(raw[i + 1]))
                {
                    if (ch != '.' || !IsProtectedPeriod(raw, i))
                    {
                        return i + 2 <= raw.Length ? i + 2 : i + 1;
                    }
                }
            }
            else if (ch is ',' or ';' or ':' or '—')
            {
                if (wordCount >= _minPhraseWords && i + 1 < raw.Length && char.IsWhiteSpace(raw[i + 1]))
                {
                    lastSafeClauseEnd = i + 2 <= raw.Length ? i + 2 : i + 1;
                }
            }
        }

        if (lastSafeClauseEnd > 0)
        {
            return lastSafeClauseEnd;
        }

        return lastSafeMaxWordEnd;
    }

    private static bool IsProtectedPeriod(string text, int periodIndex)
    {
        // Decimal or currency like 3.14 or $12.50: digit immediately before and after '.'
        if (periodIndex > 0 && periodIndex + 1 < text.Length
            && char.IsDigit(text[periodIndex - 1])
            && char.IsDigit(text[periodIndex + 1]))
        {
            return true;
        }

        // Ellipsis: '...'
        if ((periodIndex > 0 && text[periodIndex - 1] == '.')
            || (periodIndex + 1 < text.Length && text[periodIndex + 1] == '.'))
        {
            return true;
        }

        // Extract current token ending at periodIndex.
        var tokenStart = periodIndex;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        var token = text[tokenStart..(periodIndex + 1)].TrimStart('(', '[', '"', '\'');
        if (Abbreviations.Contains(token))
        {
            return true;
        }

        // Single-letter initial or dotted acronym like "A." or "U.S."
        if (token.Length == 2 && char.IsLetter(token[0]) && token[1] == '.')
        {
            return true;
        }

        if (token.Count(c => c == '.') >= 2 && token.Length <= 8)
        {
            return true;
        }

        // Numbered list item like "1."
        if (token.Length >= 2 && token[..^1].All(char.IsDigit))
        {
            return true;
        }

        return false;
    }

    private static bool HasUnclosedMarkdownLink(string text)
    {
        var openBracket = text.LastIndexOf('[');
        if (openBracket < 0)
        {
            return false;
        }

        var closeBracket = text.IndexOf(']', openBracket);
        if (closeBracket < 0)
        {
            return true;
        }

        if (closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
        {
            return text.IndexOf(')', closeBracket + 2) < 0;
        }

        return false;
    }

    private static string SanitizeForSpeech(string raw)
    {
        var text = HorizontalRuleRegex.Replace(raw, " ");
        text = MarkdownLinkRegex.Replace(text, "$1");
        text = RawUrlRegex.Replace(text, " ");
        text = HeadingPrefixRegex.Replace(text, string.Empty);
        text = BlockquotePrefixRegex.Replace(text, string.Empty);
        text = ListPrefixRegex.Replace(text, string.Empty);
        text = InlineFormattingRegex.Replace(text, string.Empty);
        text = MultiWhitespaceRegex.Replace(text, " ").Trim();
        return text;
    }
}
