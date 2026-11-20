import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { approvalDetails } from '../scripts/approval-details.mjs';

test('T27: rejects a stale edit after another circuit changes the task', async ({ page, context, request }, testInfo) => {
  const title = 'Review concurrency case';
  const concurrentDescription = 'Changed by another user';
  const requestedDescription = 'Requested assistant change';
  await page.goto('/');
  const titleInput = page.getByRole('textbox', { name: 'Task Title' });
  const add = page.getByRole('button', { name: 'Add Task' });
  await expect(async () => {
    await titleInput.fill(title);
    await titleInput.blur();
    await expect(add).toBeEnabled();
  }).toPass({ timeout: 15_000 });
  await add.click();
  await expect(page.getByText(title, { exact: true })).toHaveCount(1);

  const other = await context.newPage();
  await other.goto('/');
  await expect(other.getByText(title, { exact: true })).toHaveCount(1);
  const otherRow = other.getByText(title, { exact: true }).locator('xpath=ancestor::*[contains(concat(" ", normalize-space(@class), " "), " mud-list-item ")][1]');
  await expect(async () => {
    await otherRow.getByRole('button', { name: 'Edit task' }).click({ timeout: 2_000 });
    await expect(otherRow.getByRole('textbox', { name: 'Description', exact: true })).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 15_000, intervals: [500] });
  const otherDescription = otherRow.getByRole('textbox', { name: 'Description', exact: true });
  await otherDescription.fill(concurrentDescription, { timeout: 5_000 });
  await otherDescription.blur();
  let concurrentChangeMade = false;
  let unexpectedConfirmation = false;
  const rejectedConfirmations = [];
  await page.addLocatorHandler(page.getByRole('dialog', { name: 'Confirm action' }), async (dialog) => {
    const details = await approvalDetails(dialog);
    const opening = /^Allow Click on 'Edit task'\?/i.test(details);
    const entering = /^Allow SetValue on 'Description'\?/i.test(details) &&
      details.includes(`Value: ${requestedDescription}`);
    const saving = /^Allow Click on 'Save changes'\?/i.test(details);
    const filtering = /^Allow SetValue on '[^']*search[^']*'\?/i.test(details) && details.includes(`Value: ${title}`);
    const tab = /^Allow SelectTab on '(All|Active|Completed)'\?/.test(details);
    const read = /^Allow (Inspect|Focus|WaitForState|Highlight) on /.test(details);
    // Text entry may be auto-approved; apply the competing update only after
    // the stale editor is open and immediately before its save is allowed.
    if (saving && !concurrentChangeMade) {
      await other.getByRole('button', { name: 'Save changes' }).click({ timeout: 10_000 });
      await expect(other.getByText(concurrentDescription, { exact: true })).toBeVisible();
      concurrentChangeMade = true;
    }
    if (opening || entering || saving || filtering || tab || read) await dialog.getByRole('button', { name: 'Allow' }).click();
    else {
      unexpectedConfirmation = true;
      rejectedConfirmations.push({ action: details.match(/^Allow (\w+) on /)?.[1] ?? 'unrecognized',
        editTarget: details.includes("'Edit task'"), descriptionTarget: details.includes("'Description'"),
        saveTarget: /save/i.test(details), searchTarget: /search/i.test(details),
        deleteTarget: /delete/i.test(details), createTarget: /add task|new task/i.test(details),
        requestedTitleValue: details.includes(`Value: ${title}`) });
      await dialog.getByRole('button', { name: 'Cancel' }).click();
    }
  });

  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 }, timeout: 5_000 });
  await page.getByRole('textbox', { name: /message ellai/i })
    .fill(`Change the description of ${title} to ${requestedDescription}.`);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  let responseTimedOut = false;
  try { await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 }); }
  catch { responseTimedOut = true; }
  const answer = responseTimedOut ? '' : await page.locator('.oc-message-assistant .oc-message-text').last().innerText();
  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const match = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = match ? JSON.parse(match.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null,
      target: typeof payload.targetId === 'string' ? createHash('sha256').update(payload.targetId).digest('hex') : null,
      outcome: receipt?.outcome ?? null,
      code: receipt?.code ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T27', concurrentChangeMade, responseTimedOut, rejectedConfirmations, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });

  expect(unexpectedConfirmation).toBe(false);
  expect(concurrentChangeMade).toBe(true);
  await expect(page.getByRole('alert')).toContainText(/changed while you were editing/i);
  await other.reload();
  await expect(other.getByText(concurrentDescription, { exact: true })).toBeVisible();
  await expect(other.getByText(requestedDescription, { exact: true })).toHaveCount(0);
  expect(trace.some((item) => item.operation === 'execute')).toBe(true);
  expect(responseTimedOut).toBe(false);
  expect(answer).toMatch(/conflict|stale|changed (?:while|since|by another)|modified (?:since|by another)/i);
  expect(answer).not.toMatch(/\b(successfully updated|description has been updated)\b/i);
});
