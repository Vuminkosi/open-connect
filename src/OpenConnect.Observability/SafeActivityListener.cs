using System.Diagnostics;

namespace OpenConnect.Observability;

/// <summary>Applies the default privacy filter when an activity stops.</summary>
public sealed class SafeActivityListener : IDisposable
{
    private readonly ActivityListener _listener;

    public SafeActivityListener(ITelemetryPrivacyFilter? filter = null)
    {
        filter ??= TelemetryPrivacy.CreateDefault().Filter;
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => SafeExporter.ValidateAndSanitizeActivity(activity, filter)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();
}
