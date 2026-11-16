using System.Diagnostics.Metrics;

namespace OpenConnect.Samples.Blazor;

internal sealed record LiveUIMetricAcceptanceSample(double Value, string? Outcome);

internal sealed class LiveUIMetricsAcceptanceRecorder : IDisposable
{
    private const string CoreMeterName = "OpenConnect.LiveUI";
    private static readonly HashSet<string> MetricNames = new(StringComparer.Ordinal)
    {
        "openconnect.liveui.manifest.load.duration",
        "openconnect.liveui.target.resolution.duration",
        "openconnect.liveui.target.resolution.failures",
        "openconnect.liveui.browser.action.duration",
        "openconnect.liveui.action.execution.duration",
        "openconnect.liveui.confirmed.state.duration",
        "openconnect.liveui.speech.highlight.offset"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, List<LiveUIMetricAcceptanceSample>> _samples = new(StringComparer.Ordinal);
    private readonly MeterListener _listener;

    public LiveUIMetricsAcceptanceRecorder()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CoreMeterName && MetricNames.Contains(instrument.Name))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags));
        _listener.Start();
    }

    public IReadOnlyDictionary<string, LiveUIMetricAcceptanceSample[]> Snapshot()
    {
        lock (_gate)
            return _samples.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    public void Reset()
    {
        lock (_gate)
            _samples.Clear();
    }

    public void Dispose() => _listener.Dispose();

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        string? outcome = null;
        foreach (var tag in tags)
        {
            if (tag.Key == "outcome" && tag.Value is string value)
            {
                outcome = value;
                break;
            }
        }

        lock (_gate)
        {
            if (!_samples.TryGetValue(instrument.Name, out var values))
                _samples[instrument.Name] = values = [];
            values.Add(new LiveUIMetricAcceptanceSample(measurement.ToDouble(null), outcome));
        }
    }
}
