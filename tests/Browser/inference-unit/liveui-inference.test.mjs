import test from 'node:test';
import assert from 'node:assert/strict';

globalThis.addEventListener ??= () => {};
globalThis.isSecureContext = true;
globalThis.document ??= {};
Object.defineProperty(globalThis, 'navigator', { configurable: true, value: {
  userActivation: { isActive: true }, userAgent: 'Chrome/1', userAgentData: { brands: [{ brand: 'Google Chrome' }] }
} });
const inference = await import('../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui-inference.js');

test('reports Prompt API absence without initializing a model', async () => {
  delete globalThis.LanguageModel;
  const result = await inference.getAvailability();
  assert.equal(result.status, 'Unavailable');
  assert.equal(result.reason, 'prompt_api_unavailable');
});

test('initializes on explicit activation and returns a structured plan', async () => {
  let destroyed = false;
  globalThis.LanguageModel = {
    availability: async () => 'available',
    create: async () => ({
      prompt: async (_prompt, options) => {
        assert.ok(options.responseConstraint);
        return JSON.stringify({ schemaVersion: 'openconnect.liveui/v1', actions: [{ type: 'activate', targetId: 'todo.tab.completed' }] });
      },
      destroy: () => { destroyed = true; }
    })
  };
  const availability = await inference.initialize();
  assert.equal(availability.status, 'Available');
  await assert.rejects(inference.generate({ intent: 'Show completed tasks' }), /inference_correlation_missing/);
  const response = await inference.generate({ requestId: 'request-1', executionId: 'execution-1', sessionId: 'session-1',
    manifestHash: 'manifest-1', intent: 'Show completed tasks', routeId: 'todo', schemaVersion: 'openconnect.liveui/v1',
    capabilities: [{ targetId: 'todo.tab.completed', actions: ['activate'] }] });
  assert.equal(response.requestId, 'request-1');
  assert.equal(response.executionId, 'execution-1');
  assert.equal(response.sessionId, 'session-1');
  assert.equal(response.schemaVersion, 'openconnect.liveui/v1');
  assert.equal(response.manifestHash, 'manifest-1');
  assert.equal(response.plan.actions[0].targetId, 'todo.tab.completed');
  inference.dispose();
  assert.equal(destroyed, true);
});

test('does not start a browser-managed download without a user gesture', async () => {
  let createCalls = 0;
  navigator.userActivation.isActive = false;
  globalThis.LanguageModel = {
    availability: async () => 'downloadable',
    create: async () => { createCalls++; return {}; }
  };
  const availability = await inference.initialize();
  assert.equal(availability.status, 'Downloadable');
  assert.equal(availability.reason, 'user_activation_required');
  assert.equal(createCalls, 0);
});

test('cancels an in-progress local prompt through AbortSignal', async () => {
  navigator.userActivation.isActive = true;
  globalThis.LanguageModel = {
    availability: async () => 'available',
    create: async () => ({
      prompt: async (_prompt, options) => new Promise((_resolve, reject) => {
        options.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
      }),
      destroy: () => {}
    })
  };
  await inference.initialize();
  const generation = inference.generate({ requestId: 'request-1', executionId: 'execution-1', sessionId: 'session-1',
    manifestHash: 'manifest-1', intent: 'Show completed tasks', routeId: 'todo', schemaVersion: 'openconnect.liveui/v1', capabilities: [] });
  await Promise.resolve();
  inference.cancel();
  await assert.rejects(generation, { name: 'AbortError' });
  inference.dispose();
});
