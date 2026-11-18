import { expect, test } from '@playwright/test';

test('a model-backed worker plan drives a verified Todo tab action', async ({ page, request }) => {
  const initialCalls = await (await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls')).json();
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Use the server worker to select the Completed tab.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The local inference worker selected and verified the Completed tab.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  const calls = await (await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls')).json();
  expect(calls.calls).toHaveLength(initialCalls.calls.length + 1);
  expect(calls.calls.at(-1)).toMatchObject({ status: 200, provider: 'qwen3-0.6b', actions: [
    { type: 'activate', target: 'route.root.components-todolistcomponent-razor-tab-completed' },
  ] });
  const runs = await (await request.get('http://127.0.0.1:5110/__acceptance/runs')).json();
  expect(runs.error).toBeNull();
  expect(runs.runs.at(-1).completed).toBe(true);
  expect(runs.runs.at(-1).results.some(result => result.evidence?.postconditionVerified === true)).toBe(true);
});
