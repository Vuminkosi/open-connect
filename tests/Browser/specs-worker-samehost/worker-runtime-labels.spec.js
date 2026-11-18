import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';

test('current local worker resolves an opaque runtime target from its label', async ({ request }) => {
  const response = await request.post('http://127.0.0.1:5110/__acceptance/liveui-worker/v1/liveui/inference/plan', {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN}` },
    data: {
      requestId: randomUUID(), schemaVersion: 'openconnect.liveui/v1', intent: 'Show active tasks',
      context: { route: 'home', availableActions: [
        { target: 'oc.runtime.active', action: 'activate', label: 'Active' },
        { target: 'oc.runtime.completed', action: 'activate', label: 'Completed' },
      ] },
      generation: { maxActions: 8, maxOutputTokens: 256 },
    },
  });
  expect(response.status()).toBe(200);
  const result = await response.json();
  expect(result.plan.actions).toEqual([{ type: 'activate', target: 'oc.runtime.active', value: null }]);
});

test('gateway screens runtime labels before forwarding them to the worker', async ({ request }) => {
  const created = await request.post('http://127.0.0.1:5100/openconnect/v1/sessions', { data: {} });
  expect(created.status()).toBe(201);
  const session = await created.json();
  const plan = (label) => ({
    requestId: randomUUID(), executionId: randomUUID().replaceAll('-', ''),
    request: {
      intent: 'Show active tasks', sessionId: session.id, routeId: 'home',
      schemaVersion: 'openconnect.liveui/v1', manifestHash: 'sha256:test',
      capabilities: [{ targetId: 'oc.runtime.active', actions: ['activate'], label }],
    },
  });
  const safe = await request.post('http://127.0.0.1:5100/openconnect/v1/liveui/inference/plan', { data: plan('Active') });
  expect(safe.status()).toBe(200);
  const result = await safe.json();
  expect(result.result.plan.actions).toEqual([{ type: 'activate', targetId: 'oc.runtime.active', value: null }]);

  const before = (await (await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls')).json()).calls.length;
  const blocked = await request.post('http://127.0.0.1:5100/openconnect/v1/liveui/inference/plan', { data: plan('person@example.org') });
  expect(blocked.status()).toBe(403);
  const after = (await (await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls')).json()).calls.length;
  expect(after).toBe(before);
});
