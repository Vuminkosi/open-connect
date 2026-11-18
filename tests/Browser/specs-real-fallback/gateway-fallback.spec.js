import { expect, test } from '@playwright/test';

test('unavailable worker falls back to guarded gateway and executes its plan', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const stats = await (await request.get('http://127.0.0.1:5311/__stats')).json();
  expect(stats.readinessCalls).toBeGreaterThan(0);
  expect(stats.planCalls).toBeGreaterThan(0);
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`/openconnect/v1/sessions/${sessionId}/events`);
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  const planEvent = events.find((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan' &&
    JSON.parse(event.payload.payloadJson).provider === 'Gateway');
  expect(planEvent).toBeTruthy();
  const planRequest = events.find((event) => event.type === 'liveui.request' &&
    event.payload.operation === 'infer_plan' && event.payload.correlationId === planEvent.payload.correlationId);
  expect(planRequest).toBeTruthy();
  const plan = JSON.parse(planEvent.payload.payloadJson);
  expect(plan.outcome).toBe('completed');
  const action = plan.plan.actions.find((item) => item.type === 'activate' && item.targetId.endsWith('-tab-completed'));
  expect(action).toBeTruthy();
  const execution = events.find((event) => event.sequence > planEvent.sequence && event.type === 'liveui.request' &&
    event.payload.operation === 'execute' && event.payload.action === 'SelectTab' && event.payload.targetId === action.targetId);
  expect(execution).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' &&
    event.payload.correlationId === execution.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});


test('healthy worker is selected before the optional gateway planner', async ({ page, request }) => {
  expect((await request.post('http://127.0.0.1:5311/__healthy')).status()).toBe(204);
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const stats = await (await request.get('http://127.0.0.1:5311/__stats')).json();
  expect(stats.planCalls).toBeGreaterThan(0);
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`/openconnect/v1/sessions/${sessionId}/events`);
  expect(response.status()).toBe(200);
  const plans = (await response.json()).filter((event) => event.type === 'liveui.response')
    .map((event) => JSON.parse(event.data)).filter((payload) => payload.resultKind === 'plan')
    .map((payload) => JSON.parse(payload.payloadJson));
  expect(plans.some((plan) => plan.provider === 'LocalWorker' && plan.outcome === 'completed')).toBe(true);
  expect(plans.some((plan) => plan.provider === 'Gateway')).toBe(false);
});
