import { expect, test } from '@playwright/test';

test('connected Blazor Server uses the private inference worker and verifies its proposed action', async ({ page, request }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show my completed tasks using the server worker.');
  await page.getByRole('button', { name: /send to ellai/i }).click();

  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
    if (!response.ok()) throw new Error(`Acceptance fixture returned ${response.status()}: ${await response.text()}`);
    const payload = await response.json();
    if (payload.error) throw new Error(`Acceptance fixture rejected the tool result: ${JSON.stringify(payload.error)}`);
    return payload.runs.at(-1)?.completed ? 'completed' : 'running';
  }, { timeout: 90_000 }).toBe('completed');

  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The local inference worker selected and verified the Completed tab.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');

  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  expect(payload.error).toBeNull();
  const run = payload.runs.at(-1);
  expect(run).toMatchObject({ completed: true, scenarioName: 'worker_inference' });
  expect(run.results.map(result => result.name)).toEqual([
    'liveui_infer_plan', 'liveui_execute', 'liveui_await_state',
  ]);
  expect(run.results[0].evidence).toMatchObject({
    provider: 'LocalWorker',
    targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
    action: 'activate',
  });
  expect(run.results.at(-1).evidence).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
    verifiedState: 'state_verified',
  });
});
