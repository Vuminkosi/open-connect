import { approvalDetails } from '../scripts/approval-details.mjs';
import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import { createHmac, randomBytes } from 'node:crypto';

test('T20: reports validation and recovers task creation', async ({ page, request }, testInfo) => {
  const title = 'Validation recovery proof';
  const traceKey = randomBytes(32);
  await page.addInitScript(() => {
    window.__sawTitleValidation = false;
    new MutationObserver(() => {
      if (document.body?.textContent?.includes('Enter a task title.')) window.__sawTitleValidation = true;
    }).observe(document, { childList: true, characterData: true, subtree: true });
  });
  await page.goto('/');
  let rejected = false;
  const confirmations = [];
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const input = page.getByRole('textbox', { name: 'Task Title' });
    const titleChange = /'Task Title'/i.test(details) && details.includes(`Value: ${title}`);
    const clearTitle = /^Allow SetValue on 'Task Title'\?\nValue:\s*$/i.test(details) && await input.inputValue() === '';
    const submit = /^Allow Click on 'Add Task'\?/i.test(details) && ['', title].includes(await input.inputValue());
    const read = /^Allow (Inspect|Focus|WaitForState|Highlight) on /i.test(details);
    const allowed = titleChange || clearTitle || submit || read;
    confirmations.push({ action: details.match(/^Allow (\w+) on /i)?.[1] ?? 'unknown',
      category: titleChange ? 'enter-title' : clearTitle ? 'empty-title' : submit ? 'submit' : read ? 'read' : 'other', allowed });
    if (allowed) await dialog.getByRole('button', { name: 'Allow' }).click();
    else { rejected = true; await dialog.getByRole('button', { name: 'Cancel' }).click(); }
  });
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 }, timeout: 5_000 });
  await expect(page.locator('.mud-overlay').last()).toBeHidden({ timeout: 5_000 });
  await page.getByRole('textbox', { name: /message ellai/i }).fill(
    `Try adding a task without a title. If the form reports a validation error, correct the title to ${title} and add it.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  let responseFailure;
  try { await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 }); }
  catch (error) { responseFailure = error; }
  const sawValidation = await page.evaluate(() => window.__sawTitleValidation);
  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, stateName: payload.stateName ?? null,
      targetHash: typeof payload.targetId === 'string' ? createHmac('sha256', traceKey).update(payload.targetId).digest('hex') : null,
      outcome: receipt?.outcome ?? null, code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null,
      verifiedState: receipt?.verifiedState ?? null };
  });
  let persistedCount = null;
  if (!responseFailure) {
    await page.reload();
    await expect.poll(() => page.getByText(title, { exact: true }).count(), { timeout: 10_000 }).toBe(1).catch(() => {});
    persistedCount = await page.getByText(title, { exact: true }).count();
  }
  await fs.writeFile(testInfo.outputPath('sanitized-liveui-trace.json'), JSON.stringify({ scenario: 'T20', sawValidation, rejected, persistedCount, confirmations, trace }, null, 2));
  if (responseFailure) throw responseFailure;
  expect(rejected).toBe(false);
  expect(sawValidation).toBe(true);
  const firstSubmit = trace.findIndex((item) => item.operation === 'execute' && ['Click', 'Activate'].includes(item.action));
  const invalidObservation = trace.findIndex((item, index) => index > firstSubmit && item.operation === 'inspect' &&
    item.outcome === 'Completed' && item.verified && item.verifiedState === 'control_invalid');
  const correction = trace.findIndex((item, index) => index > invalidObservation && item.operation === 'execute' &&
    item.action === 'SetValue' && item.outcome === 'Completed' && item.verified);
  const finalSubmit = trace.findIndex((item, index) => index > correction && item.operation === 'execute' &&
    ['Click', 'Activate'].includes(item.action) && item.outcome !== 'Denied');
  expect(firstSubmit).toBeGreaterThanOrEqual(0);
  expect(invalidObservation).toBeGreaterThan(firstSubmit);
  expect(correction).toBeGreaterThan(invalidObservation);
  expect(finalSubmit).toBeGreaterThan(correction);
  expect(persistedCount).toBe(1);
});
