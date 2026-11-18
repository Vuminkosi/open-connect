import { expect, test } from '@playwright/test';

test('real agent uses an interactive follow-up to filter the previously selected tasks', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  const textbox = page.getByRole('textbox', { name: /message ellai/i });
  const send = page.getByRole('button', { name: /send to ellai/i });
  await textbox.fill('Show completed tasks.');
  await send.click();
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 180_000 });

  await textbox.fill('Now show only those containing report.');
  await send.click();
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' }))
    .toHaveValue('report', { timeout: 120_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 180_000 });

  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  const plans = events.filter((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan')
    .map((event) => JSON.parse(event.payload.payloadJson))
    .filter((plan) => plan.provider === process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND && plan.outcome === 'completed');
  expect(plans.length).toBeGreaterThanOrEqual(2);
  expect(plans.some((plan) => plan.plan.actions.some((action) => action.type === 'setValue' && action.value === 'report'))).toBe(true);
  const search = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'execute' &&
    event.payload.action === 'SetValue' && event.payload.value === 'report');
  expect(search).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === search.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
