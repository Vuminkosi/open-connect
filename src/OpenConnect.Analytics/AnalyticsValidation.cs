using System.Text;
using System.Text.Json;

namespace OpenConnect.Analytics;

internal static class AnalyticsValidation
{
    internal static void ValidateInput(JsonElement payload)
    {
        try
        {
            var fields = Object(payload, ["feature", "unit", "lineage", "values"], ["threshold", "forecast", "trend"]);
            Text(fields["feature"], 128); Text(fields["unit"], 64); Text(fields["lineage"], 256);
            var values = fields["values"];
            Array(values, 10000);
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Null && Math.Abs(Number(value)) > 1e150) Invalid();
            if (fields.TryGetValue("threshold", out var threshold) && (Number(threshold) <= 0 || Number(threshold) > 100)) Invalid();
            if (fields.TryGetValue("trend", out var trend) && trend.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Invalid();
            if (fields.TryGetValue("forecast", out var forecast) && forecast.ValueKind != JsonValueKind.Null)
            {
                var settings = Object(forecast, ["timestamps", "intervalSeconds", "seasonalPeriod", "horizon"]);
                var timestamps = settings["timestamps"];
                Array(timestamps, 10000);
                if (timestamps.GetArrayLength() != values.GetArrayLength()) Invalid();
                foreach (var timestamp in timestamps.EnumerateArray())
                    if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out _)) Invalid();
                Integer(settings["intervalSeconds"], 1, 31536000);
                Integer(settings["seasonalPeriod"], 1, 365);
                Integer(settings["horizon"], 1, 100);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new ArgumentException("Invalid analytics request."); }
    }

    internal static void ValidateOutput(JsonElement root, JsonElement payload, string taskId)
    {
        try { ValidateOutputCore(root, payload, taskId); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new InvalidOperationException("Invalid analytics worker response."); }
    }

    private static void ValidateOutputCore(JsonElement root, JsonElement payload, string taskId)
    {
        var envelope = Object(root, ["schemaVersion", "taskId", "kind", "result"]);
        if (envelope["schemaVersion"].GetString() != "openconnect.v1" || envelope["taskId"].GetString() != taskId ||
            envelope["kind"].GetString() != "analytics.analyze") Invalid();
        var result = Object(envelope["result"], ["feature", "unit", "lineage", "sample_size", "observed_count", "missing_count",
            "coverage", "minimum", "maximum", "median", "median_absolute_deviation", "threshold", "anomalies", "warnings", "algorithm_version"],
            ["forecast", "trend"]);
        foreach (var name in new[] { "feature", "unit", "lineage" })
            if (result[name].GetString() != payload.GetProperty(name).GetString()) Invalid();
        var values = payload.GetProperty("values").EnumerateArray().ToArray();
        var observed = values.Count(value => value.ValueKind != JsonValueKind.Null);
        if (Integer(result["sample_size"], 0, 10000) != values.Length || Integer(result["observed_count"], 0, 10000) != observed ||
            Integer(result["missing_count"], 0, 10000) != values.Length - observed ||
            Math.Abs(Number(result["coverage"]) - (values.Length == 0 ? 0 : (double)observed / values.Length)) > 1e-12 ||
            Number(result["threshold"]) != (payload.TryGetProperty("threshold", out var threshold) ? Number(threshold) : 3.5) ||
            result["algorithm_version"].GetString() != "robust-mad-v1") Invalid();
        var numbers = values.Where(value => value.ValueKind != JsonValueKind.Null).Select(Number).OrderBy(value => value).ToArray();
        double? median = numbers.Length == 0 ? null : Median(numbers);
        double? mad = median is null ? null : Median(numbers.Select(number => Math.Abs(number - median.Value)).OrderBy(number => number).ToArray());
        MatchOptionalNumber(result["minimum"], numbers.Length == 0 ? null : numbers[0]);
        MatchOptionalNumber(result["maximum"], numbers.Length == 0 ? null : numbers[^1]);
        MatchOptionalNumber(result["median"], median);
        MatchOptionalNumber(result["median_absolute_deviation"], mad);
        Warnings(result["warnings"], ["insufficient_baseline", "zero_median_absolute_deviation"]);
        var expectedWarning = observed < 5 ? "insufficient_baseline" : mad == 0 ? "zero_median_absolute_deviation" : null;
        var actualWarnings = result["warnings"].EnumerateArray().Select(item => item.GetString()).ToArray();
        if (actualWarnings.Length != (expectedWarning is null ? 0 : 1) ||
            (expectedWarning is not null && actualWarnings[0] != expectedWarning)) Invalid();
        Array(result["anomalies"], 10000);
        var expectedAnomalies = new Dictionary<int, double>();
        if (expectedWarning is null)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index].ValueKind == JsonValueKind.Null) continue;
                var score = 0.6744897501960817 * ((Number(values[index]) - median!.Value) / mad!.Value);
                if (!double.IsFinite(score)) Invalid();
                if (Math.Abs(score) > Number(result["threshold"])) expectedAnomalies.Add(index, score);
            }
        }
        var indexes = new HashSet<int>();
        foreach (var item in result["anomalies"].EnumerateArray())
        {
            var anomaly = Object(item, ["index", "value", "robust_score"]);
            var index = Integer(anomaly["index"], 0, Math.Max(0, values.Length - 1));
            if (index >= values.Length || !indexes.Add(index) || values[index].ValueKind == JsonValueKind.Null ||
                Number(anomaly["value"]) != Number(values[index]) || !expectedAnomalies.TryGetValue(index, out var score) ||
                !NearlyEqual(Number(anomaly["robust_score"]), score)) Invalid();
        }
        if (indexes.Count != expectedAnomalies.Count) Invalid();
        var expectsForecast = payload.TryGetProperty("forecast", out var inputForecast) && inputForecast.ValueKind != JsonValueKind.Null;
        if (result.ContainsKey("forecast") != expectsForecast) Invalid();
        if (expectsForecast) ValidateForecast(result["forecast"], inputForecast, values, observed);
        var expectsTrend = payload.TryGetProperty("trend", out var inputTrend) && inputTrend.ValueKind == JsonValueKind.True;
        if (result.ContainsKey("trend") != expectsTrend) Invalid();
        if (expectsTrend)
        {
            var trend = Object(result["trend"], ["algorithm_version", "sample_size", "observed_count", "slope_per_sample", "warnings"]);
            if (trend["algorithm_version"].GetString() != "linear-position-v1" ||
                Integer(trend["sample_size"], 0, 10000) != values.Length || Integer(trend["observed_count"], 0, 10000) != observed) Invalid();
            Warnings(trend["warnings"], ["descriptive_only_no_significance_test", "missing_values", "insufficient_history"]);
            var expectedTrendWarnings = new HashSet<string>(StringComparer.Ordinal) { "descriptive_only_no_significance_test" };
            if (observed != values.Length) expectedTrendWarnings.Add("missing_values");
            if (observed < 3) expectedTrendWarnings.Add("insufficient_history");
            if (!expectedTrendWarnings.SetEquals(trend["warnings"].EnumerateArray().Select(item => item.GetString()!))) Invalid();
            double? slope = null;
            if (observed >= 3)
            {
                var points = values.Select((value, index) => (value, index))
                    .Where(point => point.value.ValueKind != JsonValueKind.Null).ToArray();
                var meanX = AccurateSum(points.Select(point => (double)point.index)) / observed;
                var meanY = AccurateSum(points.Select(point => Number(point.value))) / observed;
                var denominator = AccurateSum(points.Select(point => (point.index - meanX) * (point.index - meanX)));
                slope = AccurateSum(points.Select(point => (point.index - meanX) * (Number(point.value) - meanY))) / denominator;
                if (!double.IsFinite(slope.Value)) Invalid();
            }
            MatchOptionalNumber(trend["slope_per_sample"], slope);
        }
    }

    private static void ValidateForecast(JsonElement value, JsonElement input, JsonElement[] values, int observed)
    {
        var samples = values.Length;
        var result = Object(value, ["algorithm_version", "interval_seconds", "seasonal_period", "sample_size", "observed_count", "coverage", "points", "backtest", "warnings"]);
        if (result["algorithm_version"].GetString() != "seasonal-naive-v1" ||
            Integer(result["sample_size"], 0, 10000) != samples || Integer(result["observed_count"], 0, 10000) != observed ||
            Integer(result["interval_seconds"], 1, 31536000) != input.GetProperty("intervalSeconds").GetInt32() ||
            Integer(result["seasonal_period"], 1, 365) != input.GetProperty("seasonalPeriod").GetInt32() ||
            Math.Abs(Number(result["coverage"]) - (samples == 0 ? 0 : (double)observed / samples)) > 1e-12) Invalid();
        Warnings(result["warnings"], ["irregular_timestamps", "missing_values", "insufficient_history", "empirical_bands_not_calibrated",
            "multi_step_uncertainty_not_estimated", "limited_backtest_samples", "seasonality_not_improved_over_naive"]);
        var points = result["points"];
        var horizon = input.GetProperty("horizon").GetInt32();
        var interval = input.GetProperty("intervalSeconds").GetInt32();
        var season = input.GetProperty("seasonalPeriod").GetInt32();
        var times = input.GetProperty("timestamps").EnumerateArray().Select(item => item.GetDateTimeOffset()).ToArray();
        var regular = times.Zip(times.Skip(1), (earlier, later) => later - earlier == TimeSpan.FromSeconds(interval)).All(item => item);
        var canForecast = observed == samples && samples >= 2 * season + Math.Max(season, 5) && regular;
        var expectedWarnings = new HashSet<string>(StringComparer.Ordinal);
        if (!regular) expectedWarnings.Add("irregular_timestamps");
        if (observed != samples) expectedWarnings.Add("missing_values");
        if (samples < 2 * season + Math.Max(season, 5)) expectedWarnings.Add("insufficient_history");
        if (canForecast)
        {
            expectedWarnings.Add("empirical_bands_not_calibrated");
            if (horizon > 1) expectedWarnings.Add("multi_step_uncertainty_not_estimated");
            if (Math.Max(season, 5) < 20) expectedWarnings.Add("limited_backtest_samples");
        }
        Array(points, horizon);
        if (points.GetArrayLength() != (canForecast ? horizon : 0) ||
            (result["backtest"].ValueKind != JsonValueKind.Null) != canForecast) Invalid();
        var pointIndex = 0;
        foreach (var point in points.EnumerateArray())
        {
            var fields = Object(point, ["timestamp", "value", "lower", "upper"]);
            if (!fields["timestamp"].TryGetDateTimeOffset(out var timestamp) ||
                timestamp != times[^1].AddSeconds((long)interval * (pointIndex + 1)) ||
                Number(fields["value"]) != Number(values[samples - season + pointIndex % season]) ||
                Number(fields["lower"]) > Number(fields["upper"])) Invalid();
            pointIndex++;
        }
        if (result["backtest"].ValueKind != JsonValueKind.Null)
        {
            var backtest = Object(result["backtest"], ["samples", "mean_absolute_error", "naive_mean_absolute_error", "residual_lower", "residual_upper"]);
            var holdout = Math.Max(season, 5);
            if (Integer(backtest["samples"], 1, 365) != holdout) Invalid();
            var series = values.Select(Number).ToArray();
            var residuals = Enumerable.Range(samples - holdout, holdout)
                .Select(index => series[index] - series[index - season]).ToArray();
            var meanError = residuals.Sum(Math.Abs) / holdout;
            var naiveError = Enumerable.Range(samples - holdout, holdout)
                .Sum(index => Math.Abs(series[index] - series[index - 1])) / holdout;
            var ordered = residuals.OrderBy(item => item).ToArray();
            var lower = ordered[Math.Max(0, (int)Math.Ceiling(0.05 * holdout) - 1)];
            var upper = ordered[(int)Math.Ceiling(0.95 * holdout) - 1];
            if (!NearlyEqual(Number(backtest["mean_absolute_error"]), meanError) ||
                !NearlyEqual(Number(backtest["naive_mean_absolute_error"]), naiveError) ||
                !NearlyEqual(Number(backtest["residual_lower"]), lower) ||
                !NearlyEqual(Number(backtest["residual_upper"]), upper)) Invalid();
            if (season > 1 && meanError >= naiveError) expectedWarnings.Add("seasonality_not_improved_over_naive");
            if (season > 1 && NearlyEqual(meanError, naiveError))
            {
                // Python uses compensated summation; either comparison can occur at rounding boundaries.
                var reported = result["warnings"].EnumerateArray().Any(item => item.GetString() == "seasonality_not_improved_over_naive");
                if (reported) expectedWarnings.Add("seasonality_not_improved_over_naive");
                else expectedWarnings.Remove("seasonality_not_improved_over_naive");
            }
            foreach (var point in points.EnumerateArray())
                if (!NearlyEqual(Number(point.GetProperty("lower")), Number(point.GetProperty("value")) + lower) ||
                    !NearlyEqual(Number(point.GetProperty("upper")), Number(point.GetProperty("value")) + upper)) Invalid();
        }
        if (!expectedWarnings.SetEquals(result["warnings"].EnumerateArray().Select(item => item.GetString()!))) Invalid();
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) Invalid();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!(required.Contains(property.Name, StringComparer.Ordinal) || optional?.Contains(property.Name, StringComparer.Ordinal) == true) ||
                !result.TryAdd(property.Name, property.Value)) Invalid();
        if (required.Any(name => !result.ContainsKey(name))) Invalid();
        return result;
    }

    private static void Warnings(JsonElement value, string[] allowed)
    {
        Array(value, allowed.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var warning = item.GetString();
            if (warning is null || !allowed.Contains(warning, StringComparer.Ordinal) || !seen.Add(warning)) Invalid();
        }
    }

    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.EnumerateRunes().Count() > maximum) Invalid();
        return value.GetString()!;
    }
    private static double Median(double[] sorted) => sorted.Length % 2 == 0
        ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
        : sorted[sorted.Length / 2];
    private static bool NearlyEqual(double actual, double expected) => Math.Abs(actual - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected));
    private static double AccurateSum(IEnumerable<double> numbers)
    {
        double sum = 0, correction = 0;
        foreach (var value in numbers)
        {
            var next = sum + value;
            correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
            sum = next;
        }
        return sum + correction;
    }
    private static void MatchOptionalNumber(JsonElement value, double? expected)
    {
        if (expected is null) { if (value.ValueKind != JsonValueKind.Null) Invalid(); }
        else if (!NearlyEqual(Number(value), expected.Value)) Invalid();
    }
    private static void Array(JsonElement value, int maximum) { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) Invalid(); }
    private static int Integer(JsonElement value, int minimum, int maximum) { if (!value.TryGetInt32(out var number) || number < minimum || number > maximum) Invalid(); return value.GetInt32(); }
    private static double Number(JsonElement value) { if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) Invalid(); return value.GetDouble(); }
    private static void Invalid() => throw new InvalidOperationException();
}
