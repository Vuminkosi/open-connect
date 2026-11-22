import { parseUniqueJson } from './classification-request.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const exact = (value, keys) => object(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const near = (actual, expected) => typeof actual === 'number' && Number.isFinite(actual) &&
    Math.abs(actual - expected) <= 1e-12 * Math.max(1, Math.abs(expected));
const accurateSum = values => {
    let sum = 0; let correction = 0;
    for (const value of values) {
        const next = sum + value;
        correction += Math.abs(sum) >= Math.abs(value) ? (sum - next) + value : (value - next) + sum;
        sum = next;
    }
    return sum + correction;
};
const optional = (actual, expected) => expected === null ? actual === null : near(actual, expected);
const median = values => values.length % 2 ? values[(values.length - 1) / 2] : values[values.length / 2 - 1] / 2 + values[values.length / 2] / 2;
const sameSet = (values, expected) => Array.isArray(values) && values.length === expected.length &&
    new Set(values).size === values.length && expected.every(value => values.includes(value));

function validateForecast(result, input, values, observed) {
    const fields = ['algorithm_version', 'interval_seconds', 'seasonal_period', 'sample_size', 'observed_count', 'coverage', 'points', 'backtest', 'warnings'];
    if (!exact(result, fields) || result.algorithm_version !== 'seasonal-naive-v1' || result.interval_seconds !== input.intervalSeconds ||
        result.seasonal_period !== input.seasonalPeriod || result.sample_size !== values.length || result.observed_count !== observed ||
        !near(result.coverage, values.length ? observed / values.length : 0) || !Array.isArray(input.timestamps) ||
        input.timestamps.length !== values.length || !Number.isInteger(input.intervalSeconds) || input.intervalSeconds < 1 ||
        input.intervalSeconds > 31536000 || !Number.isInteger(input.seasonalPeriod) || input.seasonalPeriod < 1 || input.seasonalPeriod > 365 ||
        !Number.isInteger(input.horizon) || input.horizon < 1 || input.horizon > 100 || !Array.isArray(result.points)) return false;
    const times = input.timestamps.map(value => Date.parse(value));
    if (times.some(value => !Number.isFinite(value))) return false;
    const regular = times.every((value, index) => index === 0 || value - times[index - 1] === input.intervalSeconds * 1000);
    const holdout = Math.max(input.seasonalPeriod, 5);
    const warnings = [];
    if (!regular) warnings.push('irregular_timestamps');
    if (observed !== values.length) warnings.push('missing_values');
    if (values.length < 2 * input.seasonalPeriod + holdout) warnings.push('insufficient_history');
    if (warnings.length) return result.points.length === 0 && result.backtest === null && sameSet(result.warnings, warnings);
    if (result.points.length !== input.horizon || !exact(result.backtest, ['samples', 'mean_absolute_error', 'naive_mean_absolute_error', 'residual_lower', 'residual_upper']) ||
        result.backtest.samples !== holdout) return false;
    const residuals = []; const naive = [];
    for (let index = values.length - holdout; index < values.length; index++) {
        residuals.push(values[index] - values[index - input.seasonalPeriod]);
        naive.push(Math.abs(values[index] - values[index - 1]));
    }
    const mae = accurateSum(residuals.map(Math.abs)) / holdout;
    const naiveMae = accurateSum(naive) / holdout;
    const ordered = [...residuals].sort((a, b) => a - b);
    const lower = ordered[Math.max(0, Math.ceil(0.05 * holdout) - 1)];
    const upper = ordered[Math.ceil(0.95 * holdout) - 1];
    if (!near(result.backtest.mean_absolute_error, mae) || !near(result.backtest.naive_mean_absolute_error, naiveMae) ||
        !near(result.backtest.residual_lower, lower) || !near(result.backtest.residual_upper, upper)) return false;
    warnings.push('empirical_bands_not_calibrated');
    if (input.horizon > 1) warnings.push('multi_step_uncertainty_not_estimated');
    if (holdout < 20) warnings.push('limited_backtest_samples');
    if (input.seasonalPeriod > 1 && (near(mae, naiveMae) ? result.warnings?.includes('seasonality_not_improved_over_naive') : mae >= naiveMae))
        warnings.push('seasonality_not_improved_over_naive');
    if (!sameSet(result.warnings, warnings)) return false;
    return result.points.every((point, index) => {
        const predicted = values[values.length - input.seasonalPeriod + index % input.seasonalPeriod];
        return exact(point, ['timestamp', 'value', 'lower', 'upper']) && typeof point.timestamp === 'string' &&
            Date.parse(point.timestamp) === times.at(-1) + input.intervalSeconds * 1000 * (index + 1) &&
            point.value === predicted && near(point.lower, predicted + lower) && near(point.upper, predicted + upper) && point.lower <= point.upper;
    });
}

// A successful result still requires output privacy inspection and settlement before release.
export function validateAnalyticsResponse(bytes, request) {
    try {
        if (!(bytes instanceof Uint8Array) || bytes.byteLength < 1 || bytes.byteLength > 2 * 1024 * 1024 ||
            !object(request) || !object(request.input) || !Array.isArray(request.input.values)) return null;
        const envelope = parseUniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
        const hasUsage = object(envelope) && Object.hasOwn(envelope, 'usage');
        if (!exact(envelope, hasUsage ? ['schemaVersion', 'taskId', 'kind', 'result', 'usage'] : ['schemaVersion', 'taskId', 'kind', 'result']) || envelope.schemaVersion !== 'openconnect.v1' ||
            envelope.taskId !== request.requestId || envelope.kind !== 'analytics.analyze') return null;
        if (hasUsage && (!exact(envelope.usage, ['computeMilliseconds']) || !Number.isInteger(envelope.usage.computeMilliseconds) ||
            envelope.usage.computeMilliseconds < 1 || envelope.usage.computeMilliseconds > 120000)) return null;
        const input = request.input; const result = envelope.result;
        const fields = ['feature', 'unit', 'lineage', 'sample_size', 'observed_count', 'missing_count', 'coverage', 'minimum',
            'maximum', 'median', 'median_absolute_deviation', 'threshold', 'anomalies', 'warnings', 'algorithm_version'];
        if (input.trend === true) fields.push('trend');
        if (input.forecast != null) fields.push('forecast');
        if (!exact(result, fields) || ['feature', 'unit', 'lineage'].some(name => result[name] !== input[name]) ||
            result.algorithm_version !== 'robust-mad-v1' || result.threshold !== (input.threshold ?? 3.5)) return null;
        const values = input.values; const observed = values.filter(value => value !== null);
        if (values.length > 10000 || observed.some(value => typeof value !== 'number' || !Number.isFinite(value)) ||
            result.sample_size !== values.length || result.observed_count !== observed.length || result.missing_count !== values.length - observed.length ||
            !near(result.coverage, values.length ? observed.length / values.length : 0)) return null;
        const sorted = [...observed].sort((a, b) => a - b);
        const center = sorted.length ? median(sorted) : null;
        const mad = center === null ? null : median(sorted.map(value => Math.abs(value - center)).sort((a, b) => a - b));
        if (!optional(result.minimum, sorted.length ? sorted[0] : null) || !optional(result.maximum, sorted.length ? sorted.at(-1) : null) ||
            !optional(result.median, center) || !optional(result.median_absolute_deviation, mad)) return null;
        const warning = observed.length < 5 ? 'insufficient_baseline' : mad === 0 ? 'zero_median_absolute_deviation' : null;
        if (!sameSet(result.warnings, warning ? [warning] : []) || !Array.isArray(result.anomalies)) return null;
        const expected = new Map();
        if (!warning) values.forEach((value, index) => {
            if (value !== null) {
                const score = 0.6744897501960817 * ((value - center) / mad);
                if (!Number.isFinite(score)) throw new Error('numeric range');
                if (Math.abs(score) > result.threshold) expected.set(index, score);
            }
        });
        if (result.anomalies.length !== expected.size) return null;
        for (const item of result.anomalies) {
            if (!exact(item, ['index', 'value', 'robust_score']) || !expected.has(item.index) || item.value !== values[item.index] ||
                !near(item.robust_score, expected.get(item.index))) return null;
            expected.delete(item.index);
        }
        if (input.forecast != null && (!object(input.forecast) || !validateForecast(result.forecast, input.forecast, values, observed.length))) return null;
        if (input.trend === true) {
            const trend = result.trend; const warnings = ['descriptive_only_no_significance_test'];
            if (observed.length !== values.length) warnings.push('missing_values');
            if (observed.length < 3) warnings.push('insufficient_history');
            if (!exact(trend, ['algorithm_version', 'sample_size', 'observed_count', 'slope_per_sample', 'warnings']) ||
                trend.algorithm_version !== 'linear-position-v1' || trend.sample_size !== values.length || trend.observed_count !== observed.length ||
                !sameSet(trend.warnings, warnings)) return null;
            let slope = null;
            if (observed.length >= 3) {
                const points = values.flatMap((value, index) => value === null ? [] : [[index, value]]);
                const meanX = accurateSum(points.map(point => point[0])) / points.length;
                const meanY = accurateSum(points.map(point => point[1])) / points.length;
                slope = accurateSum(points.map(point => (point[0] - meanX) * (point[1] - meanY))) /
                    accurateSum(points.map(point => (point[0] - meanX) ** 2));
                if (!Number.isFinite(slope)) return null;
            }
            if (!optional(trend.slope_per_sample, slope)) return null;
        }
        return { result: envelope.result, computeMilliseconds: hasUsage ? envelope.usage.computeMilliseconds : null };
    } catch { return null; }
}
