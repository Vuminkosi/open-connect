using OpenConnect.Hosting.AspNetCore;

namespace OpenConnect.Samples.Workers.Host;

// Sample-specific compatibility behavior, not a general voice SDK heuristic.
internal sealed class TodoVoiceTranscriptAdmissionPolicy : IVoiceTranscriptAdmissionPolicy
{
    public bool ShouldAdmitEarly(string transcript)
    {
        var value = transcript.ToLowerInvariant();
        return value.Contains("tomorrow", StringComparison.Ordinal)
            && value.Contains("weighbridge", StringComparison.Ordinal)
            && (value.Contains("todo", StringComparison.Ordinal) || value.Contains("create", StringComparison.Ordinal) || value.Contains("check", StringComparison.Ordinal));
    }

    public string NormalizeTranscript(string transcript)
    {
        return transcript
            .Replace("toto", "Todo", StringComparison.OrdinalIgnoreCase)
            .Replace("waybridge", "weighbridge", StringComparison.OrdinalIgnoreCase)
            .Replace("weybridge", "weighbridge", StringComparison.OrdinalIgnoreCase)
            .Replace("weigh bridge", "weighbridge", StringComparison.OrdinalIgnoreCase);
    }

}
