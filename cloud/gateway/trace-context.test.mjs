import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createTraceContext, isValidTraceparent } from './trace-context.mjs';

test('preserves valid trace IDs and creates a new nonzero parent', () => {
    const incoming = '00-1234567890abcdef1234567890abcdef-0123456789abcdef-01';
    const trace = createTraceContext(incoming);
    assert.equal(trace.traceId, '1234567890abcdef1234567890abcdef');
    assert.ok(isValidTraceparent(trace.traceparent));
    assert.notEqual(trace.traceparent, incoming);
    assert.ok(trace.traceparent.endsWith('-01'));
});

test('invalid or duplicate trace context is replaced with a fresh valid context', () => {
    for (const incoming of ['', 'private value', '00-00000000000000000000000000000000-0123456789abcdef-01',
        '00-1234567890abcdef1234567890abcdef-0000000000000000-01',
        '00-1234567890abcdef1234567890abcdef-0123456789abcdef-01,00-1234567890abcdef1234567890abcdef-0123456789abcdef-01']) {
        const trace = createTraceContext(incoming);
        assert.ok(isValidTraceparent(trace.traceparent));
        assert.notEqual(trace.traceId, '00000000000000000000000000000000');
        assert.ok(trace.traceparent.endsWith('-00'));
    }
});
