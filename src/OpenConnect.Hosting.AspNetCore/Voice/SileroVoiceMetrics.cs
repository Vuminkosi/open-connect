using System.Diagnostics.Metrics;

namespace OpenConnect.Hosting.AspNetCore.Voice;

/// <summary>Privacy-safe measurements for the OpenConnect host Silero VAD implementation.</summary>
internal static class SileroVoiceMetrics
{
    private static readonly Meter Meter = new("OpenConnect.Hosting.AspNetCore.Voice", "1.0.0");
    private static readonly Histogram<double> InferenceDuration = Meter.CreateHistogram<double>(
        "openconnect.voice.vad.inference.duration", "ms",
        "Duration of a successful Silero ONNX inference for one audio frame.");
    private static readonly Counter<long> SequenceGaps = Meter.CreateCounter<long>(
        "openconnect.voice.audio.frame.sequence.gap", "{frame}",
        "Audio frame sequence gaps that reset per-stream Silero recurrent state.");

    public static void InferenceCompleted(double elapsedMilliseconds)
    {
        try
        {
            InferenceDuration.Record(elapsedMilliseconds);
        }
        catch
        {
            // Telemetry must not affect live voice processing.
        }
    }

    public static void FrameSequenceGapDetected()
    {
        try
        {
            SequenceGaps.Add(1);
        }
        catch
        {
            // Telemetry must not affect live voice processing.
        }
    }
}
