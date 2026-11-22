import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseAnalyticsRequest, withAnalyticsRequest } from './analytics-request.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const fixture = JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/cloud-analytics-request-v1.json', import.meta.url), 'utf8'));
const encode = value => Buffer.from(JSON.stringify(value));
function request(body, headers = { 'content-type': 'application/json' }) {
    const bytes = Buffer.from(body);
    return { headers: new Headers(headers), readBody: async () => ({ bytes }), bytes };
}

test('accepts shared contract and rejects tenant identity and changed deadlines', () => {
    assert.equal(parseAnalyticsRequest(encode(fixture), now).capability, 'analytics.analyze');
    const invalid = JSON.parse(readFileSync(new URL('../../contracts/fixtures/invalid/cloud-analytics-request-v1.json', import.meta.url), 'utf8'));
    assert.throws(() => parseAnalyticsRequest(encode(invalid), now));
    for (const expiry of ['2030-01-01T00:00:00.000Z', '2030-01-01T00:05:00.001Z', '2030-02-30T00:01:00.000Z'])
        assert.throws(() => parseAnalyticsRequest(encode({ ...fixture, expiresAt: expiry }), now));
});

test('rejects malformed numerical, forecast and duplicate data', () => {
    for (const change of [
        input => { input.values = [true]; }, input => { input.values = [1e151]; }, input => { input.values = Array(10001).fill(0); },
        input => { input.threshold = 0; }, input => { input.trend = 'true'; }, input => { input.extra = 'value'; },
        input => { input.feature = '\ud800'; },
        input => { input.forecast = { timestamps: [], intervalSeconds: 1, seasonalPeriod: 1, horizon: 1 }; },
        input => { input.forecast = { timestamps: Array(4).fill('2030-02-30T00:00:00.000Z'), intervalSeconds: 1, seasonalPeriod: 1, horizon: 1 }; },
    ]) {
        const value = structuredClone(fixture); change(value.input);
        assert.throws(() => parseAnalyticsRequest(encode(value), now));
    }
    const duplicate = JSON.stringify(fixture).replace('"feature":', '"feature":"other","feature":');
    assert.throws(() => parseAnalyticsRequest(Buffer.from(duplicate), now));
});

test('bounds input and clears exact bytes after success and failure', async () => {
    const raw = JSON.stringify(fixture);
    for (const fails of [false, true]) {
        const req = request(raw);
        const action = withAnalyticsRequest(req, async (_, body) => {
            assert.equal(Buffer.from(body).toString('utf8'), raw);
            if (fails) throw new Error('failure');
            return 'done';
        }, () => now);
        if (fails) await assert.rejects(action); else assert.equal(await action, 'done');
        assert.ok(req.bytes.every(value => value === 0));
    }
    assert.deepEqual(await withAnalyticsRequest(request(raw, { 'content-type': 'text/plain' }), () => {}, () => now), { code: 'unsupported_media_type' });
    assert.deepEqual(await withAnalyticsRequest(request(raw, { 'content-type': 'application/json', 'content-length': '262145' }), () => {}, () => now), { code: 'payload_too_large' });
});

test('validated analytics values cannot change between admission and dispatch', () => {
    const value = parseAnalyticsRequest(encode(fixture), now);
    assert.throws(() => { value.input.values[0] = 99; }, TypeError);
    assert.throws(() => { value.input.feature = 'other'; }, TypeError);
    assert.throws(() => { value.capability = 'other'; }, TypeError);
});
