import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

test('T30: deletion requires confirmation and affects only the requested task', async ({ page, request }, testInfo) => {
  const title = 'Confirm deletion review';
  await page.goto('/');
  const input = page.getByRole('textbox', { name: 'Task Title' });
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(async () => {
    await input.fill(title);
    await input.blur();
    await expect(add).toBeEnabled({ timeout: 1_000 });
  }).toPass({ timeout: 15_000, intervals: [250] });
  await add.click();
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
  const initialRows = await page.locator('.mud-list-item').count();
  const filter = page.getByRole('textbox', { name: 'Search tasks by title or description...' });
  await filter.fill(title);
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Delete task' })).toHaveCount(1);
  let approvedDeletion = false;
  let unexpectedConfirmation = false;
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const uniqueRequestedTask = await page.getByText(title, { exact: true }).count() === 1 &&
      await page.getByRole('button', { name: 'Delete task' }).count() === 1;
    if (/^Allow (Click|Open|Close) on 'Delete task'\?/.test(details) && uniqueRequestedTask) {
      approvedDeletion = true;
      await dialog.getByRole('button', { name: 'Allow' }).click();
    } else {
      unexpectedConfirmation = true;
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Delete the task called ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  const answer = await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T30', approvedDeletion, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  await page.reload();
  expect(unexpectedConfirmation).toBe(false);
  expect(approvedDeletion).toBe(true);
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  await expect(page.locator('.mud-list-item')).toHaveCount(initialRows - 1);
  expect(trace.some((item) => item.operation === 'execute' && ['Click', 'Activate'].includes(item.action))).toBe(true);
  expect(answer).toMatch(/\b(deleted|removed)\b/i);
  expect(answer).not.toMatch(/\b(unable|failed|could not|couldn't|not deleted)\b/i);
});
