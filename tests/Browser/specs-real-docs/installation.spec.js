import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';

const cases = [
  { id: 'D01', prompt: 'Show me how to install OpenConnect.', route: /\/docs\/v1\/installation\/?$/, heading: /installation/i },
  { id: 'D02', prompt: 'Where is the gateway configuration?', route: /\/docs\/v1\/configuration\/?$/, heading: /gateway configuration/i },
  { id: 'D03', prompt: 'Take me to the LiveUI documentation.', route: /\/docs\/v1\/liveui\/?$/, heading: /LiveUI/i },
];

for (const scenario of cases) test(`${scenario.id}: navigates to documentation through the assistant`, async ({ page, request }, testInfo) => {
  await page.goto('/');
  await page.getByRole('button', { name: /open (ellai|assistant)/i }).click({ timeout: 15_000 });
  await expect(page.getByRole('dialog', { name: /composer/i })).toBeVisible();
  await page.getByRole('textbox', { name: /message/i }).fill(scenario.prompt);
  await page.getByRole('button', { name: /send/i }).click();
  await expect(page).toHaveURL(scenario.route, { timeout: 120_000 });
  await expect(page.getByRole('heading', { name: scenario.heading }).first()).toBeVisible();
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionId = (await page.getByText(/^Session:/).textContent())?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const gatewayUrl = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL;
  const response = await request.get(`${gatewayUrl}/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const events = (await response.json()).filter((event) => ['liveui.request', 'liveui.response'].includes(event.type))
    .map((event) => ({ type: event.type, payload: JSON.parse(event.data) }));
  const trace = events.filter((event) => event.type === 'liveui.request').map(({ payload }) => {
    const responseEvent = events.find((event) => event.type === 'liveui.response' && event.payload.correlationId === payload.correlationId);
    const receipt = responseEvent ? JSON.parse(responseEvent.payload.payloadJson) : null;
    return { operation: payload.operation, outcome: receipt?.outcome ?? null, code: receipt?.code ?? null,
      verified: receipt?.postconditionVerified ?? null };
  });
  const tracePath = testInfo.outputPath('sanitized-liveui-trace.json');
  await fs.mkdir(path.dirname(tracePath), { recursive: true });
  await fs.writeFile(tracePath, JSON.stringify({ scenario: scenario.id, trace }, null, 2), 'utf8');
  await testInfo.attach('sanitized-liveui-trace', { path: tracePath, contentType: 'application/json' });
  expect(trace).toContainEqual(expect.objectContaining({ operation: 'navigate', outcome: 'Completed', verified: true }));
});
