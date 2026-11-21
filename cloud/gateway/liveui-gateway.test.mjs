import { test } from 'node:test';
import assert from 'node:assert/strict';
import { handle } from './gateway.mjs';
import { createFingerprintKeyring } from './request-fingerprint.mjs';
import { createLiveUIAccessMonitor } from './liveui-access-monitor.mjs';
import { liveUIExecutionFromEnvironment } from './liveui-execution-configuration.mjs';

const id = '11111111-1111-1111-1111-111111111111';
test('LiveUI route authorizes, inspects, claims and inspects output before returning a proposal', async () => {
    const calls = []; let denied = false; let raw;
    const value = { schemaVersion: 'openconnect.cloud.liveui/v1', requestId: id, capability: 'inference.liveui',
        expiresAt: new Date(Date.now() + 60000).toISOString(), input: { schemaVersion: 'v1', intent: 'open item',
            context: { route: '/items', availableActions: [{ target: 'item', action: 'activate' }] }, generation: { maxActions: 2, maxOutputTokens: 32 } } };
    const request = { url: 'http://localhost/cloud/v1/liveui/plan', method: 'POST',
        headers: new Headers({ authorization: 'Bearer key', 'content-type': 'application/json' }),
        readBody: async () => { calls.push('body'); raw = Buffer.from(JSON.stringify(value)); return { bytes: raw }; } };
    const authorization = { authorize: async () => { calls.push('authorize'); return { authorized: true, identity: { tenantId: id, applicationId: id } }; },
        inspectLiveUI: async () => { calls.push('input'); return { allowed: true }; } };
    const controlPlane = { findFingerprintKey: async () => { calls.push('lookup'); return { status: 'not_found' }; },
        reserve: async () => { calls.push('reserve'); return { status: 'reserved', reservationId: id }; },
        claim: async () => { calls.push('claim'); return { status: 'claimed', claimId: id }; },
        verifyClaim: async () => { calls.push('access'); return true; },
        inspectOutput: async () => { calls.push('output'); return denied ? { allowed: false, code: 'privacy_blocked' } : { allowed: true, code: 'allowed' }; } };
    const keyring = createFingerprintKeyring({ activeKeyId: 'key', keys: [{ id: 'key', key: Buffer.alloc(32, 1) }] });
    const liveUIExecution = { keyring, controlPlane, deployment: {}, modelId: 'model', createAccessMonitor: createLiveUIAccessMonitor,
        probe: { probe: async () => { calls.push('probe'); return true; } },
        dispatcher: { executeClaimed: async () => { calls.push('dispatch'); return { status: 'completed',
            result: { plan: { schemaVersion: 'v1', actions: [{ type: 'activate', target: 'item' }] }, provider: 'deterministic' } }; } } };
    try {
        const response = await handle(request, { authorization, liveUIExecution });
        assert.equal(response.status, 200); assert.equal((await response.json()).plan.actions[0].target, 'item');
        assert.deepEqual(calls, ['authorize', 'probe', 'body', 'lookup', 'input', 'reserve', 'claim', 'access', 'dispatch', 'access', 'output', 'access']);
        assert.ok(raw.every(byte => byte === 0));
        calls.length = 0; denied = true;
        assert.equal((await handle(request, { authorization, liveUIExecution })).status, 403);
        calls.length = 0; authorization.authorize = async () => ({ authorized: false, code: 'unauthorized' });
        assert.equal((await handle(request, { authorization, liveUIExecution })).status, 401);
        assert.deepEqual(calls, []);
    } finally { keyring.dispose(); }
});
const config = { CLOUD_LIVEUI_EXECUTION_ENABLED: 'true', CLOUD_LIVEUI_ORIGIN: 'https://worker',
    CLOUD_LIVEUI_CA_FILE: '/secrets/ca', CLOUD_LIVEUI_CERT_FILE: '/secrets/cert', CLOUD_LIVEUI_KEY_FILE: '/secrets/key',
    CLOUD_LIVEUI_TOKEN_FILE: '/secrets/token', CLOUD_LIVEUI_DEPLOYMENT_ID: id, CLOUD_LIVEUI_MODEL_ID: 'model',
    CLOUD_LIVEUI_ENDPOINT_REFERENCE: 'worker', CLOUD_LIVEUI_REGION: 'region', CLOUD_LIVEUI_CONTRACT_VERSION: 'v1',
    CLOUD_LIVEUI_MODEL_REVISION: 'revision', CLOUD_LIVEUI_MODEL_DIGEST: 'sha256:' + 'a'.repeat(64),
    CLOUD_AUTHORITY_ORIGIN: 'https://authority', CLOUD_AUTHORITY_CA_FILE: '/secrets/ca', CLOUD_AUTHORITY_CERT_FILE: '/secrets/cert',
    CLOUD_AUTHORITY_KEY_FILE: '/secrets/key', CLOUD_FINGERPRINT_ACTIVE_KEY_ID: 'key', CLOUD_FINGERPRINT_KEYS_FILE: '/secrets/fingerprint' };
const read = path => path.endsWith('fingerprint') ? Buffer.from('key:' + Buffer.alloc(32, 1).toString('base64') + '\n') : Buffer.from('fixture');
test('LiveUI execution is opt-in and requires pinned deployment and private TLS configuration', () => {
    assert.equal(liveUIExecutionFromEnvironment({}, undefined), undefined);
    assert.throws(() => liveUIExecutionFromEnvironment(config, undefined, read));
    for (const change of [{ CLOUD_LIVEUI_ORIGIN: 'http://worker' }, { CLOUD_LIVEUI_DEPLOYMENT_ID: 'invalid' },
        { CLOUD_LIVEUI_MODEL_DIGEST: 'invalid' }, { CLOUD_LIVEUI_KEY_FILE: 'relative' }, { CLOUD_AUTHORITY_ORIGIN: 'http://authority' }])
        assert.throws(() => liveUIExecutionFromEnvironment({ ...config, ...change }, {}, read));
    const execution = liveUIExecutionFromEnvironment(config, {}, read);
    assert.equal(execution.modelId, 'model'); execution.keyring.dispose();
    assert.throws(() => liveUIExecutionFromEnvironment({ ...config, CLOUD_LIVEUI_METERED_ENABLED: 'true' }, {}, read));
    assert.throws(() => liveUIExecutionFromEnvironment({ ...config, CLOUD_LIVEUI_METERED_ENABLED: 'true', CLOUD_LIVEUI_MODEL_CONTEXT_TOKENS: '0256' }, {}, read));
    const metered = liveUIExecutionFromEnvironment({ ...config, CLOUD_LIVEUI_METERED_ENABLED: 'true', CLOUD_LIVEUI_MODEL_CONTEXT_TOKENS: '2048' }, {}, read);
    assert.equal(metered.tokenAmount, 2048); metered.keyring.dispose();
});
