import { expect, test } from '@playwright/test';

test('real agent does not repeat a completed action after its receipt is lost', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 180_000 });
  const stats = await (await request.get('http://127.0.0.1:5130/__receipt-loss/stats')).json();
  expect(stats.droppedReceipts).toBe(1);

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
  const executions = events.filter((event) => event.type === 'liveui.request' && event.payload.operation === 'execute' &&
    event.payload.action === 'SelectTab' && event.payload.targetId?.endsWith('-tab-completed'));
  expect(executions).toHaveLength(1);
  expect(events.some((event) => event.type === 'liveui.response' &&
    event.payload.correlationId === executions[0].payload.correlationId)).toBe(false);
});
