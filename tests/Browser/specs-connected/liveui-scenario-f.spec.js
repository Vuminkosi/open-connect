import { expect, test } from '@playwright/test';

async function openComposer(page, speechEnabled) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();

  if (!speechEnabled) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    const voiceOutput = page.getByText('Voice output', { exact: true });
    await expect(voiceOutput).toBeVisible();
    await voiceOutput.click();
    await expect(page.locator('.oc-bubble__live-speak')).not.toHaveClass(/--active/);
    await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
    await expect(page.locator('.mud-overlay')).toHaveCount(0);
  } else {
    await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);
  }
}

async function submitScenario(page) {
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show completed tasks containing report and explain them.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText('The matching completed tasks are Review the quarterly report and Draft the annual report.', { timeout: 90_000 });
}

async function expectVerifiedFixtureRun(request) {
  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  expect(payload.error).toBeNull();
  const run = payload.runs.at(-1);
  expect(run).toMatchObject({ completed: true });
  expect(run.results.map((result) => result.name)).toEqual([
    'liveui_get_current_page',
    'liveui_execute',
    'liveui_await_state',
    'liveui_execute',
    'liveui_await_state',
    'liveui_highlight',
  ]);
  expect(run.results.at(-1).receipt).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
    verifiedState: 'highlighted',
  });
}

async function expectScenarioState(page) {
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  await expect(page.locator('.mud-overlay')).toHaveCount(0);
}

test('connected Scenario F completes with voice output enabled', async ({ page, request }) => {
  await openComposer(page, true);
  await submitScenario(page);
  await expectScenarioState(page);
  await expectVerifiedFixtureRun(request);
});

test('connected Scenario F completes with voice output disabled', async ({ page, request }) => {
  await openComposer(page, false);
  await submitScenario(page);
  await expectScenarioState(page);
  await expectVerifiedFixtureRun(request);
});
