import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { liveUIInspectionFields, parseLiveUIRequest, validateLiveUIWorkerResponse } from './liveui-contract.mjs';

const now = Date.parse('2030-01-01T00:00:00.000Z');
const request = () => ({ schemaVersion: 'openconnect.cloud.liveui/v1', requestId: '11111111-1111-1111-1111-111111111111',
    capability: 'inference.liveui', expiresAt: '2030-01-01T00:01:00.000Z', input: { schemaVersion: 'v1', intent: 'Show tasks',
        context: { route: 'route.root', availableActions: [{ target: 'tasks.tab', action: 'activate', label: 'Tasks' },
            { target: 'tasks.search', action: 'setValue', label: 'Search' }] }, generation: { maxActions: 2, maxOutputTokens: 64 } } });
const parse = value => parseLiveUIRequest(Buffer.from(JSON.stringify(value)), now);
const response = () => ({ requestId: request().requestId, schemaVersion: 'v1', provider: 'qwen3-0.6b', status: 'completed',
    plan: { schemaVersion: 'v1', actions: [{ type: 'activate', target: 'tasks.tab', value: null }] } });

test('LiveUI request preserves and exposes every model-visible text field for privacy inspection', () => {
    const value = parse(request());
    assert.ok(Object.isFrozen(value.input.context.availableActions[0]));
    assert.throws(() => { value.input.context.availableActions[0].target = 'changed'; });
    assert.deepEqual(liveUIInspectionFields(value), ['v1', 'Show tasks', 'route.root', 'tasks.tab', 'activate', 'Tasks', 'tasks.search', 'setValue', 'Search']);
    assert.throws(() => parseLiveUIRequest(Buffer.from('{"requestId":"a","requestId":"b"}'), now));
    for (const mutate of [
        x => { x.expiresAt = '2030-01-01T00:00:00.000Z'; }, x => { x.expiresAt = '2030-01-01T00:05:00.001Z'; },
        x => { x.capability = 'inference.chat'; }, x => { x.input.context.extra = 'private'; },
        x => { x.input.context.availableActions[0].label = ''; }, x => { x.input.generation.maxOutputTokens = 513; },
        x => { x.input.intent = ''; }, x => { x.input.generation.maxActions = 0; },
    ]) { const value = request(); mutate(value); assert.throws(() => parse(value)); }
});

test('shared LiveUI contract fixtures agree with gateway validation', () => {
    const valid = readFileSync(new URL('../../contracts/fixtures/valid/cloud-liveui-request-v1.json', import.meta.url));
    const invalid = readFileSync(new URL('../../contracts/fixtures/invalid/cloud-liveui-request-v1.json', import.meta.url));
    assert.equal(parseLiveUIRequest(valid, now).input.intent, 'Show tasks');
    assert.throws(() => parseLiveUIRequest(invalid, now));
});

test('worker plans must match the request schema and exact action-target pairs', () => {
    const value = parse(request());
    assert.deepEqual(validateLiveUIWorkerResponse(response(), value), { schemaVersion: 'v1', actions: [{ type: 'activate', target: 'tasks.tab' }] });
    for (const mutate of [
        x => { x.plan.actions[0].target = 'unavailable'; }, x => { x.plan.actions[0].type = 'navigate'; },
        x => { x.plan.actions[0].value = 'unexpected'; }, x => { x.plan.schemaVersion = 'other'; },
        x => { x.requestId = 'other'; }, x => { x.provider = 'unapproved'; }, x => { x.plan.actions = []; },
        x => { x.plan.actions[0].type = 'executeScript'; }, x => { x.plan.actions[0].extra = true; },
        x => { x.plan.actions = [{ type: 'setValue', target: 'tasks.search' }]; },
    ]) { const result = response(); mutate(result); assert.equal(validateLiveUIWorkerResponse(result, value), null); }
    const search = response(); search.plan.actions = [{ type: 'setValue', target: 'tasks.search', value: 'fixture' }];
    assert.deepEqual(validateLiveUIWorkerResponse(search, value).actions, [{ type: 'setValue', target: 'tasks.search', value: 'fixture' }]);
});
