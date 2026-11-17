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

test('gateway planner executes a real-provider plan in the connected Todo UI', async ({ page, request }) => {
  test.skip(process.env.OPENCONNECT_LIVEUI_REAL_GATEWAY_PLANNER_RUN !== '1', 'Requires the opt-in same-host gateway planner.');
  test.setTimeout(300_000);
  await openComposer(page);
  expect((await request.post('/__acceptance/liveui-metrics/reset')).status()).toBe(204);
  const turn = await sendPrompt(page, 'Show completed tasks.');
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
  await turn.waitForCompletion();
  const metrics = await (await request.get('/__acceptance/liveui-metrics')).json();
  expect(metrics.measurements['openconnect.liveui.inference.requests']).toContainEqual(
    expect.objectContaining({ provider: 'gateway', outcome: 'completed' }));
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const eventsResponse = await request.get(`/openconnect/v1/sessions/${sessionId}/events`);
  expect(eventsResponse.status()).toBe(200);
  const events = (await eventsResponse.json()).filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  const planRequest = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'infer_plan');
  expect(planRequest).toBeTruthy();
  const planResponse = events.find((event) => event.type === 'liveui.response' &&
    event.payload.correlationId === planRequest.payload.correlationId);
  expect(planResponse).toBeTruthy();
  const plan = JSON.parse(planResponse.payload.payloadJson);
  expect(plan.provider).toBe('Gateway');
  expect(plan.outcome).toBe('completed');
  const selected = plan.plan.actions.find((action) => action.type === 'activate' && action.targetId.endsWith('-tab-completed'));
  expect(selected).toBeTruthy();
  const execute = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'execute' &&
    event.payload.targetId === selected.targetId && event.payload.action === 'SelectTab');
  expect(execute).toBeTruthy();
  const receipt = events.find((event) => event.type === 'liveui.response' &&
    event.payload.correlationId === execute.payload.correlationId);
  expect(receipt).toBeTruthy();
  expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});

test('real provider shows active Todo tasks', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Show active tasks.');

  await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true');
  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
  await turn.waitForCompletion();
});

test('real provider highlights the displayed Todo results', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Highlight the results.');

  await expectHighlightActivation(page, { text: 'Prepare a launch checklist' });
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

test('real provider explains the current screen and highlights the task area', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, "Explain what I'm looking at and highlight the task area.");

  await expect(page.getByRole('heading', { name: 'Workflows & Tasks' })).toBeVisible();
  await expectHighlightActivation(page, { text: 'Prepare a launch checklist' });
  await expect(turn.response).toContainText(/\b(?:workflows|tasks?|todos?)\b/i);
  await expect(turn.response).toContainText(/\b(?:search|filter|progress|tabs?)\b/i);
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

test('acceptance wording: open my Todo list', async ({ page }) => {
  await openComposer(page, '/workspace-guide');
  const turn = await sendPrompt(page, 'Open my Todo list.');
  await expect.poll(() => new URL(page.url()).pathname).toBe('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await turn.waitForCompletion();
});

test('acceptance wording: show completed tasks', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Show completed tasks.');
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-oc-target="todo-list"]').getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await turn.waitForCompletion();
});

test('acceptance wording: search for report', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Search for report.');
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  await expect(page.locator('[data-oc-target="todo-list"]').getByText('Gather feedback for report', { exact: true })).toBeVisible();
  await turn.waitForCompletion();
});

test('acceptance wording: completed tasks containing report', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Show completed tasks containing report.');
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  const taskList = page.locator('[data-oc-target="todo-list"]');
  await expect(taskList.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(taskList.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await turn.waitForCompletion();
});

test('acceptance wording: explain the current view', async ({ page }) => {
  await openComposer(page);
  const turn = await sendPrompt(page, 'Explain the current view.');
  await turn.waitForCompletion();
  await expect(turn.response).toContainText(/\b(?:workflows|tasks?|todos?)\b/i);
  await expect(turn.response).toContainText(/\b(?:search|filter|progress|tabs?)\b/i);
});
