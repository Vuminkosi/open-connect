using System.Diagnostics.Metrics;

namespace OpenConnect.Hosting.AspNetCore.Voice;

/// <summary>Sanitized OpenTelemetry metrics for <c>openconnect.voice.v2</c> duplex voice sessions.</summary>
/// <remarks>No high-cardinality identifiers (session/tenant/capture/response IDs), raw transcripts, or raw audio are ever attached to metrics.</remarks>
internal static class VoiceDuplexMetrics
{
    private static readonly Meter Meter = new("OpenConnect.Hosting.AspNetCore.Voice.Duplex", "2.0.0");

    private static readonly Histogram<double> FirstPartialLatencyMs = Meter.CreateHistogram<double>(
        "openconnect.voice.first_partial.duration", "ms",
        "Elapsed milliseconds from speech start to first partial transcript hypothesis.");

    private static readonly Histogram<double> EndpointDecisionLatencyMs = Meter.CreateHistogram<double>(
        "openconnect.voice.endpoint_decision.duration", "ms",
        "Elapsed milliseconds from speech end/silence onset to authoritative endpoint finalization.");

    private static readonly Histogram<double> FirstSpeakablePhraseLatencyMs = Meter.CreateHistogram<double>(
        "openconnect.voice.first_phrase.duration", "ms",
        "Elapsed milliseconds from turn admission to first approved speakable phrase.");

    private static readonly Histogram<double> FirstTtsChunkLatencyMs = Meter.CreateHistogram<double>(
        "openconnect.voice.first_tts_chunk.duration", "ms",
        "Elapsed milliseconds from turn admission to first synthesized PCM16 chunk.");

    private static readonly Histogram<double> BargeInStopLatencyMs = Meter.CreateHistogram<double>(
        "openconnect.voice.barge_in_stop.duration", "ms",
        "Elapsed milliseconds to fence and cancel active response synthesis on barge-in interrupt.");

    private static readonly Counter<long> UnderrunCounter = Meter.CreateCounter<long>(
        "openconnect.voice.output.underrun", "{event}",
        "Count of detected voice output playback underruns.");

    private static readonly Counter<long> OverloadCounter = Meter.CreateCounter<long>(
        "openconnect.voice.overload", "{event}",
        "Count of bounded queue or worker overload rejections/cancellations.");

    private static readonly Counter<long> StaleEpochRejectedCounter = Meter.CreateCounter<long>(
        "openconnect.voice.stale_epoch.rejected", "{event}",
        "Count of stale speech epoch frames or control messages rejected.");

    public static void RecordFirstPartial(double elapsedMs, string providerKind)
    {
        try
        {
            if (elapsedMs >= 0)
            {
                FirstPartialLatencyMs.Record(elapsedMs, new KeyValuePair<string, object?>("provider", providerKind));
            }
        }
        catch { }
    }

    public static void RecordEndpointDecision(double elapsedMs, string decision)
    {
        try
        {
            if (elapsedMs >= 0)
            {
                EndpointDecisionLatencyMs.Record(elapsedMs, new KeyValuePair<string, object?>("decision", decision));
            }
        }
        catch { }
    }

    public static void RecordFirstSpeakablePhrase(double elapsedMs)
    {
        try
        {
            if (elapsedMs >= 0)
            {
                FirstSpeakablePhraseLatencyMs.Record(elapsedMs);
            }
        }
        catch { }
    }

    public static void RecordFirstTtsChunk(double elapsedMs)
    {
        try
        {
            if (elapsedMs >= 0)
            {
                FirstTtsChunkLatencyMs.Record(elapsedMs);
            }
        }
        catch { }
    }

    public static void RecordBargeInStop(double elapsedMs)
    {
        try
        {
            if (elapsedMs >= 0)
            {
                BargeInStopLatencyMs.Record(elapsedMs);
            }
        }
        catch { }
    }

    public static void RecordUnderrun()
    {
        try
        {
            UnderrunCounter.Add(1);
        }
        catch { }
    }

    public static void RecordOverload(string stage)
    {
        try
        {
            OverloadCounter.Add(1, new KeyValuePair<string, object?>("stage", stage));
        }
        catch { }
    }

    public static void RecordStaleEpochRejected(string kind)
    {
        try
        {
            StaleEpochRejectedCounter.Add(1, new KeyValuePair<string, object?>("kind", kind));
        }
        catch { }
    }
}
