import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

test('asks which duplicate task to complete without changing either task', async ({ page, request }, testInfo) => {
  await page.goto('/');
  const title = page.getByRole('textbox', { name: 'Task Title' });
  const add = page.getByRole('button', { name: 'Add Task' });
  for (let count = 1; count <= 2; count++) {
    await expect(async () => {
      await title.fill('Submit report');
      await title.blur();
      await expect(add).toBeEnabled({ timeout: 1000 });
    }).toPass({ timeout: 15_000, intervals: [250] });
    await add.click();
    await expect(page.getByText('Submit report', { exact: true })).toHaveCount(count);
  }
  const checkboxStates = () => page.locator('input[type="checkbox"]').evaluateAll((items) => items.map((item) => item.checked));
  const before = await checkboxStates();

  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Complete the report.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });

  const answer = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const eventsResponse = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(eventsResponse.status()).toBe(200);
  const events = (await eventsResponse.json())
    .filter((item) => item.type === 'liveui.request' || item.type === 'liveui.response')
    .map((item) => ({ type: item.type, payload: JSON.parse(item.data) }));
  const trace = events.filter((item) => item.type === 'liveui.request').map(({ payload }) => {
    const response = events.find((item) => item.type === 'liveui.response' && item.payload.correlationId === payload.correlationId);
    const result = response ? JSON.parse(response.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null,
      outcome: result?.outcome ?? null, code: result?.code ?? null, verified: result?.postconditionVerified ?? null,
      verifiedState: result?.verifiedState ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify(trace, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  console.log(`Sanitized LiveUI trace: ${JSON.stringify(trace)}`);
  await page.reload();
  await expect(page.getByText('Submit report', { exact: true })).toHaveCount(2);
  await expect.poll(checkboxStates).toEqual(before);
  expect(trace.length).toBeGreaterThan(0);
  expect(trace).toContainEqual(expect.objectContaining({
    outcome: 'Ambiguous', code: 'text_match_ambiguous', verified: false,
  }));
  expect(answer).toMatch(/\b(two|2|multiple|duplicate|more than one)\b/i);
  expect(answer).toMatch(/\b(which|clarif\w*|specify|choose|give me)\b/i);
  expect(answer).not.toMatch(/\b(no matching|couldn.t find|no report task appeared)\b/i);
});
