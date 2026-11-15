import { expect, test } from '@playwright/test';

async function openComposer(page, route = '/') {
  await page.addInitScript(() => {
    const highlightAttribute = 'data-openconnect-liveui-highlight';
    const originalSetAttribute = Element.prototype.setAttribute;
    Object.defineProperty(window, '__openConnectLiveUIHighlights', { value: [] });
    Element.prototype.setAttribute = function (name, value) {
      if (name === highlightAttribute && value === 'true') {
        window.__openConnectLiveUIHighlights.push({
          target: this.getAttribute('data-oc-target'),
          text: (this.textContent || '').trim(),
        });
      }
      return originalSetAttribute.call(this, name, value);
    };
  });
  await page.goto(route);
  if (route === '/') {
    await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  } else {
    await expect(page.getByRole('heading', { name: 'Workspace guide' })).toBeVisible();
  }

  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();

  const voiceOutput = page.locator('.oc-bubble__live-speak');
  if (await voiceOutput.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(voiceOutput).not.toHaveClass(/--active/);
  await expect(page.locator('.mud-overlay')).toHaveCount(0);
}

async function sendPrompt(page, prompt) {
  const status = page.locator('.oc-status__text');
  await expect(status).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill(prompt);
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(status).toHaveText(/Replying/, { timeout: 30_000 });
  const response = page.locator('.oc-message-assistant .oc-message-text').last();
  await expect(response).not.toBeEmpty({ timeout: 170_000 });
  return {
    response,
    waitForCompletion: () => expect(status).toHaveText('Ready', { timeout: 170_000 }),
  };
}

async function expectHighlightActivation(page, expected) {
  await expect.poll(() => page.evaluate((expected) =>
    window.__openConnectLiveUIHighlights.some((highlight) =>
      (expected.target === undefined || highlight.target === expected.target) &&
      (expected.text === undefined || highlight.text.includes(expected.text))), expected),
  { timeout: 35_000 }).toBe(true);
}

test('real provider navigates from another compiled route and highlights the existing Todo region', async ({ page }) => {
  await openComposer(page, '/workspace-guide');
  const turn = await sendPrompt(page, 'Open my Todo list and highlight the task list.');

  await expect.poll(() => new URL(page.url()).pathname).toBe('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await expectHighlightActivation(page, { text: 'Prepare a launch checklist' });
  await turn.waitForCompletion();
});

test('real provider selects and verifies the existing Completed tab', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Show my completed tasks.');

  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await turn.waitForCompletion();
});

test('real provider uses the existing search control and verifies rendered results', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Find tasks containing report.');

  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Gather feedback for report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
  await turn.waitForCompletion();
});

test('real provider grounds its recommendation and highlights the unique highest-priority task', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Highlight the task I should work on and explain why.');

  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  await expectHighlightActivation(page, { text: 'Prepare a launch checklist' });
  await expect(turn.response).toContainText(/urgent|highest.priority|priority/i);
  await turn.waitForCompletion();
});

test('real provider explains the current screen from inspected state without an onboarding popup', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, "Explain what I'm looking at and highlight the task area.");

  await expect(page.getByRole('heading', { name: 'Workflows & Tasks' })).toBeVisible();
  await expectHighlightActivation(page, { text: 'Prepare a launch checklist' });
  await expect(turn.response).toContainText(/workflows|tasks/i);
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toHaveCount(1);
  await expect(page.locator('.mud-overlay')).toHaveCount(0);
  await turn.waitForCompletion();
});

test('real provider combines completed filtering and report search while retaining both controls', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Show completed tasks containing report and explain them.');

  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(turn.response).toContainText(/quarterly report/i);
  await expect(turn.response).toContainText(/annual report/i);
  await turn.waitForCompletion();
});
