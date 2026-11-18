using System.Diagnostics.Metrics;

namespace OpenConnect.LiveUI.Inference;

internal static class LiveUIInferenceMetrics
{
    private static readonly Meter Meter = new("OpenConnect.LiveUI.Inference", "1.0.0");

    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>("openconnect.liveui.inference.requests");

    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("openconnect.liveui.inference.duration", "ms");

    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("openconnect.liveui.inference.failures");

    public static readonly Counter<long> Fallbacks =
        Meter.CreateCounter<long>("openconnect.liveui.inference.fallbacks");

    public static readonly Counter<long> InvalidPlans =
        Meter.CreateCounter<long>("openconnect.liveui.inference.invalid_plans");

    public static readonly Counter<long> ModelAvailability =
        Meter.CreateCounter<long>("openconnect.liveui.inference.model_availability");

    public static string Provider(LiveUIInferenceProviderKind provider) => provider switch
    {
        LiveUIInferenceProviderKind.Browser => "browser",
        LiveUIInferenceProviderKind.LocalWorker => "local_worker",
        LiveUIInferenceProviderKind.Gateway => "gateway",
        _ => "unknown"
    };
}
