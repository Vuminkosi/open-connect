import { expect, test } from '@playwright/test';

test('real agent highlights the actual row for the newest Todo', async ({ page, request }) => {
  test.setTimeout(360_000);
  const title = 'Prepare a launch checklist';
  await page.goto('/');
  await page.getByRole('tab', { name: 'Completed' }).click();
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  await page.evaluate(() => {
    window.__highlightedTaskRows = [];
    const record = () => {
      for (const element of document.querySelectorAll('[data-openconnect-liveui-highlight="true"]'))
        window.__highlightedTaskRows.push(element.innerText?.trim() ?? '');
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true, attributeFilter: ['data-openconnect-liveui-highlight'], subtree: true,
    });
  });
  await page.locator('.oc-anchored-fab').click();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Find my most recently created task and highlight its row.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.getByText(title, { exact: true })).toBeVisible({ timeout: 180_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 180_000 });
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(title);
  expect(await page.evaluate(() => window.__highlightedTaskRows.some((text) => text.includes('Prepare a launch checklist')))).toBe(true);

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
  if (process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND === 'LocalWorker') {
    expect(events.some((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan' &&
      JSON.parse(event.payload.payloadJson).provider === 'LocalWorker')).toBe(true);
  }
  const highlight = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'highlight' &&
    event.payload.targetId?.endsWith('-list-task-list') && event.payload.value === title);
  expect(highlight).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === highlight.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});
