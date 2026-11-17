import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';

test('the authenticated gateway validates Todo plans from its private worker', async ({ request }) => {
  const authorization = `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}`;
  const gateway = 'http://127.0.0.1:5120/openconnect/v1';
  expect((await request.post(`${gateway}/sessions`, { data: { name: 'Acceptance' } })).status()).toBe(401);
  expect((await request.get(`${gateway}/liveui/inference/worker/availability`)).status()).toBe(401);
  const created = await request.post(`${gateway}/sessions`, { headers: { authorization }, data: { name: 'Acceptance' } });
  expect(created.status()).toBe(201);
  const sessionId = (await created.json()).id;
  const route = 'route.root';
  const all = 'route.root.components-todolistcomponent-razor-tab-all';
  const active = 'route.root.components-todolistcomponent-razor-tab-active';
  const completed = 'route.root.components-todolistcomponent-razor-tab-completed';
  const search = 'route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description';
  const list = 'route.root.components-todolistcomponent-razor-list-task-list';
  const capabilities = [
    { targetId: route, actions: ['navigate'] }, { targetId: all, actions: ['activate'] },
    { targetId: active, actions: ['activate'] }, { targetId: completed, actions: ['activate'] },
    { targetId: search, actions: ['setValue', 'focus'] }, { targetId: list, actions: ['highlight', 'scroll'] },
  ];
  const cases = [
    ['Open my tasks', [{ type: 'navigate', targetId: route }]],
    ['Open my Todo list.', [{ type: 'navigate', targetId: route }]],
    ['Show completed tasks', [{ type: 'activate', targetId: completed }]],
    ['Show active tasks', [{ type: 'activate', targetId: active }]],
    ['Show all tasks', [{ type: 'activate', targetId: all }]],
    ['Search for report', [{ type: 'setValue', targetId: search, value: 'report' }]],
    ['Highlight the tasks', [{ type: 'highlight', targetId: list }]],
    ['Highlight the results.', [{ type: 'highlight', targetId: list }]],
    ['Show completed tasks containing report', [
      { type: 'activate', targetId: completed }, { type: 'setValue', targetId: search, value: 'report' },
    ]],
  ];
  for (const [intent, expected] of cases) {
    const requestId = randomUUID();
    const executionId = randomUUID().replaceAll('-', '');
    const response = await request.post(`${gateway}/liveui/inference/plan`, {
      headers: { authorization }, data: { requestId, executionId, request: {
        intent, sessionId, routeId: route, schemaVersion: 'openconnect.liveui/v1', capabilities,
        manifestHash: 'sha256:acceptance',
      } },
    });
    expect(response.status(), intent).toBe(200);
    const result = await response.json();
    expect(result).toMatchObject({ requestId, executionId, result: { provider: 'LocalWorker', outcome: 'completed', plan: { actions: expected } } });
    expect(result.result.plan.actions).toHaveLength(expected.length);
  }
});
