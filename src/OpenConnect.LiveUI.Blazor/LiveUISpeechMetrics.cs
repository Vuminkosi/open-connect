using System.Diagnostics.Metrics;

namespace OpenConnect.LiveUI.Blazor;

internal static class LiveUISpeechMetrics
{
    private static readonly Meter Meter = new("OpenConnect.LiveUI", "1.0.0");

    public static readonly Histogram<double> SpeechHighlightOffset =
        Meter.CreateHistogram<double>("openconnect.liveui.speech.highlight.offset", "ms");
}
