import { expect, test } from '@playwright/test';

test('real Qwen invalid plan falls back to the real gateway and executes both filters', async ({ page, request }) => {
  await page.goto('/');
  expect((await request.post('/__acceptance/liveui-metrics/reset')).status()).toBe(204);
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('View finished tasks containing report');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report', { timeout: 120_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const workerCalls = (await (await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls')).json()).calls;
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gateway = 'http://127.0.0.1:5120/openconnect/v1';
  const response = await request.get(`${gateway}/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  if (!workerCalls.some((call) => call.status === 422)) {
    console.log('Synthetic inference requests:', JSON.stringify(events.filter((event) =>
      event.type === 'liveui.request' && event.payload.operation === 'infer_plan').map((event) => event.payload.value)));
  }
  expect(workerCalls.some((call) => call.status === 422)).toBe(true);
  const planEvent = events.find((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan' &&
    JSON.parse(event.payload.payloadJson).provider === 'Gateway');
  if (!planEvent) {
    const metrics = await request.get('/__acceptance/liveui-metrics');
    if (metrics.ok()) console.log('LiveUI inference metrics:', JSON.stringify((await metrics.json()).measurements));
  }
  expect(planEvent).toBeTruthy();
  expect(events.some((event) => event.type === 'liveui.request' && event.payload.operation === 'infer_plan' &&
    event.payload.correlationId === planEvent.payload.correlationId)).toBe(true);
  const plan = JSON.parse(planEvent.payload.payloadJson);
  expect(plan.outcome).toBe('completed');
  let after = planEvent.sequence;
  for (const expected of [
    { type: 'activate', suffix: '-tab-completed', operation: 'SelectTab' },
    { type: 'setValue', suffix: '-input-search-tasks-by-title-or-description', operation: 'SetValue', value: 'report' },
  ]) {
    const action = plan.plan.actions.find((item) => item.type === expected.type && item.targetId.endsWith(expected.suffix) &&
      (expected.value === undefined || item.value === expected.value));
    expect(action).toBeTruthy();
    const execution = events.find((event) => event.sequence > after && event.type === 'liveui.request' &&
      event.payload.operation === 'execute' && event.payload.action === expected.operation &&
      event.payload.targetId === action.targetId && (expected.value === undefined || event.payload.value === expected.value));
    expect(execution).toBeTruthy();
    const receipt = events.find((event) => event.type === 'liveui.response' &&
      event.payload.correlationId === execution.payload.correlationId);
    expect(receipt).toBeTruthy();
    expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
    after = receipt.sequence;
  }
});
