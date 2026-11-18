using System.Diagnostics.Metrics;

namespace OpenConnect.LiveUI;

internal static class LiveUIMetrics
{
    private static readonly Meter Meter = new("OpenConnect.LiveUI", "1.0.0");

    public static readonly Histogram<double> ManifestLoadDuration =
        Meter.CreateHistogram<double>("openconnect.liveui.manifest.load.duration", "ms");

    public static readonly Histogram<double> TargetResolutionDuration =
        Meter.CreateHistogram<double>("openconnect.liveui.target.resolution.duration", "ms");

    public static readonly Counter<long> TargetResolutionFailures =
        Meter.CreateCounter<long>("openconnect.liveui.target.resolution.failures");

    public static readonly Histogram<double> BrowserActionDuration =
        Meter.CreateHistogram<double>("openconnect.liveui.browser.action.duration", "ms");

    public static readonly Histogram<double> ActionExecutionDuration =
        Meter.CreateHistogram<double>("openconnect.liveui.action.execution.duration", "ms");

    public static readonly Histogram<double> ConfirmedStateDuration =
        Meter.CreateHistogram<double>("openconnect.liveui.confirmed.state.duration", "ms");
}
