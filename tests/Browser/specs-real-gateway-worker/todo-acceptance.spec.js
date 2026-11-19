import { expect, test } from '@playwright/test';

const plannerKind = process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND ?? 'LocalWorker';

const cases = [
  { name: 'open Todo', prompt: 'Open my Todo list.', route: '/workspace-guide',
    actions: [{ type: 'navigate', targetSuffix: 'route.root' }] },
  { name: 'open tasks', prompt: 'Open my tasks.', route: '/workspace-guide',
    actions: [{ type: 'navigate', targetSuffix: 'route.root' }] },
  { name: 'all tasks', prompt: 'Show all tasks.', initiallyActive: true,
    actions: [{ type: 'activate', targetSuffix: '-tab-all' }] },
  { name: 'completed tasks', prompt: 'Show completed tasks.',
    actions: [{ type: 'activate', targetSuffix: '-tab-completed' }] },
  { name: 'active tasks', prompt: 'Show active tasks.',
    actions: [{ type: 'activate', targetSuffix: '-tab-active' }] },
  { name: 'search report', prompt: 'Search for report.',
    actions: [{ type: 'setValue', targetSuffix: '-input-search-tasks-by-title-or-description', value: 'report' }] },
  { name: 'quoted search', prompt: 'Search for "Report?!".', searchValue: 'Report?!',
    actions: [{ type: 'setValue', targetSuffix: '-input-search-tasks-by-title-or-description', value: 'Report?!' }] },
  { name: 'completed report tasks', prompt: 'Show completed tasks containing report.',
    actions: [{ type: 'activate', targetSuffix: '-tab-completed' },
      { type: 'setValue', targetSuffix: '-input-search-tasks-by-title-or-description', value: 'report' }] },
  { name: 'highlight tasks', prompt: 'Highlight the tasks.',
    actions: [{ type: 'highlight', targetSuffix: '-list-task-list' }] },
  { name: 'highlight results', prompt: 'Highlight the results.',
    actions: [{ type: 'highlight', targetSuffix: '-list-task-list' }] },
  { name: 'explain current view', prompt: 'Explain the current view.',
    actions: [{ type: 'inspect', targetSuffix: '-region-todo-list' }] },
];

async function verifyPlan(page, request, expectedActions) {
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const sessionEvents = await response.json();
  const taskId = JSON.parse(sessionEvents.find((event) => event.type === 'task.accepted').data).taskId;
  const messages = sessionEvents.filter((event) => event.type === 'message');
  expect(messages.length).toBeGreaterThan(0);
  expect(messages.every((event) => event.taskId === taskId)).toBe(true);
  const events = sessionEvents.filter((event) => event.type === 'liveui.request' || event.type === 'liveui.response')
    .map((event) => ({ ...event, payload: JSON.parse(event.data) }));
  const plans = events.filter((event) => event.type === 'liveui.response' && event.payload.resultKind === 'plan')
    .map((event) => ({ event, plan: JSON.parse(event.payload.payloadJson) }))
    .filter(({ plan }) => plan.provider === plannerKind && plan.outcome === 'completed');
  const matching = plans.find(({ plan }) => expectedActions.every((expected) => plan.plan.actions.some((action) =>
    action.type === expected.type && action.targetId.endsWith(expected.targetSuffix) &&
    (expected.value === undefined || action.value === expected.value))));
  expect(matching).toBeTruthy();
  const planRequest = events.find((event) => event.type === 'liveui.request' && event.payload.operation === 'infer_plan' &&
    event.payload.correlationId === matching.event.payload.correlationId);
  expect(planRequest).toBeTruthy();
  let after = matching.event.sequence;
  for (const expected of expectedActions) {
    const action = matching.plan.plan.actions.find((item) => item.type === expected.type &&
      item.targetId.endsWith(expected.targetSuffix) && (expected.value === undefined || item.value === expected.value));
    const operation = { navigate: 'navigate', activate: 'execute', setValue: 'execute', highlight: 'highlight', inspect: 'inspect' }[expected.type];
    const execution = events.find((event) => event.sequence > after && event.type === 'liveui.request' &&
      event.payload.operation === operation &&
      (expected.type === 'navigate' ? event.payload.routeId === action.targetId : event.payload.targetId === action.targetId) &&
      (expected.type !== 'activate' || event.payload.action === 'SelectTab') &&
      (expected.type !== 'setValue' || (event.payload.action === 'SetValue' && event.payload.value === expected.value)));
    expect(execution).toBeTruthy();
    const receipt = events.find((event) => event.type === 'liveui.response' &&
      event.payload.correlationId === execution.payload.correlationId);
    expect(receipt).toBeTruthy();
    expect(JSON.parse(receipt.payload.payloadJson)).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
    after = receipt.sequence;
  }
}

for (const scenario of cases) {
  test(`real conversation and ${plannerKind}: ${scenario.name}`, async ({ page, request }) => {
    const fallbackCallsBefore = plannerKind === 'Gateway'
      ? (await (await request.get('http://127.0.0.1:5311/__stats')).json()).planCalls : 0;
    await page.goto(scenario.route ?? '/');
    if (scenario.initiallyActive) {
      await page.getByRole('tab', { name: 'Active' }).click();
      await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true');
    }
    await page.locator('.oc-anchored-fab').click();
    await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
    const voice = page.locator('.oc-bubble__live-speak');
    if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
      await page.locator('.oc-bubble__header-actions button').first().click();
      await page.getByText('Voice output', { exact: true }).click();
    }
    await expect(page.locator('.oc-status__text')).toHaveText('Ready');
    await page.getByRole('textbox', { name: /message ellai/i }).fill(scenario.prompt);
    await page.getByRole('button', { name: /send to ellai/i }).click();
    await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
    if (scenario.route) await expect.poll(() => new URL(page.url()).pathname, { timeout: 120_000 }).toBe('/');
    if (scenario.name === 'completed tasks' || scenario.name === 'completed report tasks')
      await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
    if (scenario.name === 'all tasks')
      await expect(page.getByRole('tab', { name: 'All' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
    if (scenario.name === 'active tasks') {
      await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true', { timeout: 120_000 });
      await expect(page.getByText('Gather feedback for report', { exact: true })).toBeVisible();
      await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
      await expect(page.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
      await expect(page.getByText('Draft the annual report', { exact: true })).toHaveCount(0);
    }
    if (scenario.name === 'search report' || scenario.name === 'completed report tasks' || scenario.searchValue)
      await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue(scenario.searchValue ?? 'report', { timeout: 120_000 });
    await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
    if (scenario.name === 'explain current view')
      await expect(page.locator('.oc-message-assistant .oc-message-text').last()).toContainText(/\b(?:workflows|tasks?|todos?)\b/i);
    await verifyPlan(page, request, scenario.actions);
    if (plannerKind === 'Gateway') {
      const stats = await (await request.get('http://127.0.0.1:5311/__stats')).json();
      expect(stats.planCalls).toBeGreaterThan(fallbackCallsBefore);
    }
  });
}


test('real conversation does not substitute a Todo action for an unsupported account request', async ({ page, request }) => {
  await page.goto('/');
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('dialog', { name: 'Ellai composer' })).toBeVisible();
  const voice = page.locator('.oc-bubble__live-speak');
  if (await voice.evaluate((element) => element.classList.contains('oc-bubble__live-speak--active'))) {
    await page.locator('.oc-bubble__header-actions button').first().click();
    await page.getByText('Voice output', { exact: true }).click();
  }
  await expect(page.locator('.oc-status__text')).toHaveText('Ready');
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Change my account password on this page.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.locator('.oc-status__text')).toHaveText(/Replying/, { timeout: 30_000 });
  await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 170_000 });
  await expect(page.getByRole('tab', { name: 'All' })).toHaveAttribute('aria-selected', 'true');
  await page.locator('.oc-bubble__header-actions button').first().click();
  const sessionText = await page.getByText(/^Session:/).textContent();
  const sessionId = sessionText?.match(/[0-9A-HJKMNP-TV-Z]{26}/)?.[0];
  expect(sessionId).toBeTruthy();
  const response = await request.get(`http://127.0.0.1:5120/openconnect/v1/sessions/${sessionId}/events`, {
    headers: { authorization: `Bearer ${process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER}` },
  });
  expect(response.status()).toBe(200);
  const operations = (await response.json()).filter((event) => event.type === 'liveui.request')
    .map((event) => JSON.parse(event.data).operation);
  expect(operations.filter((operation) => ['execute', 'navigate', 'highlight'].includes(operation))).toEqual([]);
});
