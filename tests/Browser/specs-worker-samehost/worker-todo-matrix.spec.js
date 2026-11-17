import { expect, test } from '@playwright/test';

const route = 'route.root';
const all = 'route.root.components-todolistcomponent-razor-tab-all';
const active = 'route.root.components-todolistcomponent-razor-tab-active';
const completed = 'route.root.components-todolistcomponent-razor-tab-completed';
const search = 'route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description';
const list = 'route.root.components-todolistcomponent-razor-list-task-list';

const cases = [
  ['Open my tasks', [{ type: 'navigate', targetId: route, value: null }]],
  ['Show completed tasks', [{ type: 'activate', targetId: completed, value: null }]],
  ['Show active tasks', [{ type: 'activate', targetId: active, value: null }]],
  ['Show all tasks', [{ type: 'activate', targetId: all, value: null }]],
  ['Search for report', [{ type: 'setValue', targetId: search, value: 'report' }]],
  ['Highlight the tasks', [{ type: 'highlight', targetId: list, value: null }]],
  ['Show completed tasks containing report', [
    { type: 'activate', targetId: completed, value: null }, { type: 'setValue', targetId: search, value: 'report' },
  ]],
];

for (const [intent, expected] of cases) {
  test(`worker-backed interactive request: ${intent}`, async ({ page, request }) => {
    await page.goto('/');
    await page.locator('.oc-anchored-fab').click();
    await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
    await page.getByRole('textbox', { name: /message ellai/i }).fill(intent);
    await page.getByRole('button', { name: /send to ellai/i }).click();
    await expect(page.getByRole('log', { name: 'Conversation' })).toContainText('The requested task view is ready.', { timeout: 90_000 });
    const result = await (await request.get('http://127.0.0.1:5110/__acceptance/runs')).json();
    expect(result.error).toBeNull();
    const run = result.runs.at(-1);
    expect(run).toMatchObject({ userText: intent, scenarioName: 'worker_matrix', completed: true });
    expect(run.results[0].evidence).toEqual({ provider: 'LocalWorker', actions: expected });
    expect(run.results).toHaveLength(expected.length + 1);
    for (const receipt of run.results.slice(1)) {
      expect(receipt.evidence).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
    }
    if (expected.some(action => action.targetId === completed))
      await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
    if (expected.some(action => action.targetId === active))
      await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true');
    if (expected.some(action => action.targetId === all))
      await expect(page.getByRole('tab', { name: 'All' })).toHaveAttribute('aria-selected', 'true');
    if (expected.some(action => action.targetId === search))
      await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  });
}

test('worker-backed navigation opens Todo from another route', async ({ page, request }) => {
  await page.goto('/workspace-guide');
  await expect(page.getByRole('heading', { name: 'Workspace guide' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Open my tasks');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText('The requested task view is ready.', { timeout: 90_000 });
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  const result = await (await request.get('http://127.0.0.1:5110/__acceptance/runs')).json();
  expect(result.error).toBeNull();
  const run = result.runs.at(-1);
  expect(run.results[0].evidence).toEqual({ provider: 'LocalWorker', actions: [{ type: 'navigate', targetId: route, value: null }] });
  expect(run.results[1].evidence).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
