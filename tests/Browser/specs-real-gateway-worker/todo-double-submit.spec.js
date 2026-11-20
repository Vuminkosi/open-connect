import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

test('T25: rapid duplicate submission creates at most one requested task', async ({ page, request }, testInfo) => {
  const title = 'Review duplicate submission';
  await page.goto('/');
  await expect(page.getByText(title, { exact: true })).toHaveCount(0);
  let unexpectedConfirmation = false;
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    if ((details.includes("'Task Title'") && details.includes(`Value: ${title}`)) || details.includes("'Add Task'"))
      await dialog.getByRole('button', { name: 'Allow' }).click();
    else {
      await dialog.getByRole('button', { name: 'Cancel' }).click();
      unexpectedConfirmation = true;
    }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill(`Create a task called ${title}.`);
  await page.getByRole('button', { name: /send to ellai/i }).dblclick({ delay: 20 });
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL ?? 'http://127.0.0.1:5120';
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = await response.json();
  const accepted = events.filter((event) => event.type === 'task.accepted').length;
  const interactions = events.filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = interactions.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = interactions.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T25', accepted, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  await page.reload();
  expect(unexpectedConfirmation).toBe(false);
  expect(accepted).toBe(1);
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);
  expect(trace.some((item) => item.operation === 'execute' && item.action === 'SetValue' && item.verified)).toBe(true);
});
