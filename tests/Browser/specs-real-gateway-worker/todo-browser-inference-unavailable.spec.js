import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';

test('T33: uses server-local inference when browser inference is unavailable', async ({ page, request }, testInfo) => {
  const availabilityResponse = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/liveui/inference/worker/availability`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  const availabilityPayload = availabilityResponse.ok() ? await availabilityResponse.json() : null;
  const availability = availabilityPayload && { provider: availabilityPayload.provider, status: availabilityPayload.status };
  await page.addInitScript(() => {
    Object.defineProperty(globalThis, 'LanguageModel', { configurable: true, value: undefined });
  });
  await page.goto('/');
  expect(await page.evaluate(() => typeof globalThis.LanguageModel)).toBe('undefined');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  await page.locator('.mud-overlay').last().click({ position: { x: 5, y: 5 } });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  let tabSelected = true;
  try { await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 }); }
  catch { tabSelected = false; }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });

  const response = await request.get(`${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const match = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = match ? JSON.parse(match.payload.payloadJson) : null;
    return { operation: payload.operation, action: payload.action ?? null, provider: receipt?.provider ?? null,
      outcome: receipt?.outcome ?? null, verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.writeFile(tracePath, JSON.stringify({ scenario: 'T33', browserPromptApi: 'unavailable',
    workerAvailabilityStatus: availabilityResponse.status(), workerAvailability: availability, tabSelected, trace }, null, 2));
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  expect(trace).toContainEqual(expect.objectContaining({ operation: 'infer_plan', provider: 'LocalWorker', outcome: 'completed' }));
  expect(trace).toContainEqual(expect.objectContaining({ operation: 'execute', action: 'SelectTab', outcome: 'Completed', verified: true }));
  expect(tabSelected).toBe(true);
});
