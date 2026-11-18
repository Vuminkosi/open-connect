import { expect, test } from '@playwright/test';

test('real agent starts an interactive tour and advances when asked to continue', async ({ page, request }) => {
  test.setTimeout(480_000);
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  const status = page.locator('.oc-status__text');
  const textbox = page.getByRole('textbox', { name: /message ellai/i });
  const send = page.getByRole('button', { name: /send to ellai/i });
  await expect(status).toHaveText('Ready');
  await textbox.fill('Please show me around this page interactively.');
  await send.click();
  await expect(status).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(status).toHaveText('Ready', { timeout: 180_000 });
  const firstReply = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  expect(firstReply).toMatch(/\?/);
  expect(firstReply).toMatch(/continue|next|proceed|more/i);

  await textbox.fill('Yes, please continue to the next area.');
  await send.click();
  await expect(status).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(status).toHaveText('Ready', { timeout: 180_000 });

  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = await response.json();
  const accepted = events.filter((event) => event.type === 'task.accepted');
  expect(accepted).toHaveLength(2);
  const interactions = events.filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  const verified = (event) => {
    const receipt = interactions.find((item) => item.type === 'liveui.response' &&
      item.payload.correlationId === event.payload.correlationId);
    return receipt && JSON.parse(receipt.payload.payloadJson).outcome === 'Completed' &&
      JSON.parse(receipt.payload.payloadJson).postconditionVerified === true;
  };
  const actions = interactions.filter((event) => event.type === 'liveui.request' &&
    ['highlight', 'navigate', 'execute'].includes(event.payload.operation) && verified(event));
  const first = actions.filter((event) => event.sequence > accepted[0].sequence && event.sequence < accepted[1].sequence);
  const second = actions.filter((event) => event.sequence > accepted[1].sequence);
  expect(first.length).toBeGreaterThan(0);
  expect(second.length).toBeGreaterThan(0);
  expect(second.some((event) => !first.some((prior) => (prior.payload.targetId ?? prior.payload.routeId) ===
    (event.payload.targetId ?? event.payload.routeId)))).toBe(true);
});
