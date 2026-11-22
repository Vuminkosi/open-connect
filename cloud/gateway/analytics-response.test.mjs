import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateAnalyticsResponse } from './analytics-response.mjs';
import { readFileSync } from 'node:fs';

const id = '11111111-1111-1111-1111-111111111111';
const request = () => ({ requestId: id, input: { feature: 'weight', unit: 'kg', lineage: 'fixture', values: [1, 2, 3], trend: true } });
const response = () => ({ schemaVersion: 'openconnect.v1', taskId: id, kind: 'analytics.analyze', result: {
    feature: 'weight', unit: 'kg', lineage: 'fixture', sample_size: 3, observed_count: 3, missing_count: 0,
    coverage: 1, minimum: 1, maximum: 3, median: 2, median_absolute_deviation: 1, threshold: 3.5,
    anomalies: [], warnings: ['insufficient_baseline'], algorithm_version: 'robust-mad-v1',
    trend: { algorithm_version: 'linear-position-v1', sample_size: 3, observed_count: 3,
        slope_per_sample: 1, warnings: ['descriptive_only_no_significance_test'] },
} });
const encode = value => Buffer.from(JSON.stringify(value));

test('accepts consistent statistical and trend results', () => {
    const value = response();
    assert.deepEqual(validateAnalyticsResponse(encode(value), request()), { result: value.result, computeMilliseconds: null });
});

test('validates the shared worker usage fixture', () => {
    const fixture = JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-worker-analytics-response-v1.json', import.meta.url)));
    const input = { requestId: id, input: { feature: 'weight', unit: 'kg', lineage: 'fixture', values: [1, 2, 3] } };
    assert.deepEqual(validateAnalyticsResponse(encode(fixture), input), { result: fixture.result, computeMilliseconds: 14 });
    fixture.usage.computeMilliseconds = 0;
    assert.equal(validateAnalyticsResponse(encode(fixture), input), null);
});

test('rejects altered lineage, statistics, warnings, trend and injected fields', () => {
    for (const mutate of [
        value => { value.result.lineage = 'other'; },
        value => { value.result.median = 7; },
        value => { value.result.warnings = []; },
        value => { value.result.trend.slope_per_sample = 2; },
        value => { value.result.extra = 'private data'; },
        value => { value.taskId = 'other'; },
    ]) {
        const value = response(); mutate(value);
        assert.equal(validateAnalyticsResponse(encode(value), request()), null);
    }
    assert.equal(validateAnalyticsResponse(Buffer.from('{"taskId":"a","taskId":"b"}'), request()), null);
});

test('validates forecast backtest, intervals, bounds and warning flags', () => {
    const input = request(); input.input.trend = false; input.input.values = [1, 2, 1, 2, 1, 2, 1, 2, 1, 2];
    input.input.forecast = { timestamps: Array.from({ length: 10 }, (_, i) => new Date(Date.parse('2030-01-01T00:00:00Z') + i * 1000).toISOString()),
        intervalSeconds: 1, seasonalPeriod: 2, horizon: 2 };
    const value = response(); delete value.result.trend;
    Object.assign(value.result, { sample_size: 10, observed_count: 10, missing_count: 0, maximum: 2, median: 1.5, median_absolute_deviation: 0.5, warnings: [] });
    value.result.forecast = { algorithm_version: 'seasonal-naive-v1', interval_seconds: 1, seasonal_period: 2,
        sample_size: 10, observed_count: 10, coverage: 1,
        points: [{ timestamp: '2030-01-01T00:00:10+00:00', value: 1, lower: 1, upper: 1 },
            { timestamp: '2030-01-01T00:00:11+00:00', value: 2, lower: 2, upper: 2 }],
        backtest: { samples: 5, mean_absolute_error: 0, naive_mean_absolute_error: 1, residual_lower: 0, residual_upper: 0 },
        warnings: ['empirical_bands_not_calibrated', 'multi_step_uncertainty_not_estimated', 'limited_backtest_samples'] };
    assert.deepEqual(validateAnalyticsResponse(encode(value), input), { result: value.result, computeMilliseconds: null });
    value.result.forecast.points[1].upper = 3;
    assert.equal(validateAnalyticsResponse(encode(value), input), null);
});
