import { expect, test } from '@playwright/test';

async function openComposer(page, speechEnabled, route = '/') {
  await page.goto(route);
  if (route === '/') await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  if (route === '/workspace-guide') await expect(page.getByRole('heading', { name: 'Workspace guide' })).toBeVisible();
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

async function expectVerifiedFixtureRun(request, expectedTools = [
  'liveui_get_current_page',
  'liveui_execute',
  'liveui_await_state',
  'liveui_execute',
  'liveui_await_state',
  'liveui_highlight',
], expectedFinalEvidence = { verifiedState: 'highlighted' }) {
  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  expect(payload.error).toBeNull();
  const run = payload.runs.at(-1);
  expect(run).toMatchObject({ completed: true });
  expect(run.results.map((result) => result.name)).toEqual(expectedTools);
  expect(run.results.at(-1).evidence).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
    ...expectedFinalEvidence,
  });

  return run;
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

test('connected Scenario A navigates to the compiled Todo route and highlights its ready task region', async ({ page, request }) => {
  await openComposer(page, true, '/workspace-guide');
  await expect(page.getByRole('heading', { name: 'Workspace guide' })).toBeVisible();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Open my Todo list.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'I opened the Todo page, verified it was ready, and highlighted the task region.',
    { timeout: 90_000 },
  );
  await expect(page).toHaveURL(/127\.0\.0\.1:5100\/$/);
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  const run = await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_navigate', 'liveui_highlight']);
  expect(run.results[0].evidence).toMatchObject({
    routeId: 'route.workspace-guide',
    targetCount: 0,
    navigableRouteCount: 1,
  });
  expect(run.results[1].evidence).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
    code: 'route_ready',
  });
});

test('connected Scenario B selects and verifies the existing Completed tab', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Show my completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText('The Completed tab is selected and shows the completed tasks.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_execute', 'liveui_await_state'], { verifiedState: 'state_verified' });
});

test('connected active-task request selects the existing Active tab instead of answering with a task summary', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Pull up my active tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The Active tab is selected and shows your active tasks.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Gather feedback for report', { exact: true })).toBeVisible();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  await expect(page.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
  await expectVerifiedFixtureRun(
    request,
    ['liveui_get_current_page', 'liveui_execute', 'liveui_await_state'],
    { verifiedState: 'state_verified' },
  );
});

test('connected Blazor Server falls back to its server model when browser and worker inference are unavailable', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Pull up my active tasks when local inference is unavailable.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The Active tab is selected and shows your active tasks.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Active' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Gather feedback for report', { exact: true })).toBeVisible();

  const run = await expectVerifiedFixtureRun(request,
    ['liveui_infer_plan', 'liveui_get_current_page', 'liveui_execute', 'liveui_await_state'],
    { verifiedState: 'state_verified' });
  expect(run.results[0].evidence).toMatchObject({
    outcome: 'inference_unavailable',
    fallback: 'server-model',
  });
});

test('connected Scenario C searches through the existing task field and verifies the rendered results', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Find tasks containing report.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText('I searched the existing task list for report.', { timeout: 90_000 });
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue('report');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toBeVisible();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
  await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_execute', 'liveui_await_state'], { verifiedState: 'state_verified' });
});

test('connected Scenario F completes with voice output disabled', async ({ page, request }) => {
  await openComposer(page, false);
  await submitScenario(page);
  await expectScenarioState(page);
  await expectVerifiedFixtureRun(request);
});

test('connected Scenario D grounds a unique priority recommendation before highlighting its row', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Highlight the task I should work on and explain the criterion.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'I recommend Prepare a launch checklist because it is the only incomplete task with Urgent priority. I highlighted that task.',
    { timeout: 90_000 },
  );
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.locator('.mud-overlay')).toHaveCount(0);

  await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'todo.list', 'liveui_highlight']);
  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  const run = (await response.json()).runs.at(-1);
  expect(run.results[1].evidence).toMatchObject({ title: 'Prepare a launch checklist', priority: 3, isCompleted: false });
  expect(run.results[2].arguments).toMatchObject({ matchText: 'Prepare a launch checklist' });
});

test('connected Scenario E inspects and explains the current task region without an onboarding popup', async ({ page, request }) => {
  await openComposer(page, true);
  await page.getByRole('textbox', { name: /message ellai/i }).fill("Explain what I'm looking at.");
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'You are viewing Workflows & Tasks. The existing controls filter, search, and display the tasks, and I highlighted this task region.',
    { timeout: 90_000 },
  );
  await expect(page.getByRole('heading', { name: 'Workflows & Tasks' })).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.locator('.mud-overlay')).toHaveCount(0);

  await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_inspect', 'liveui_highlight']);
});

test('connected Todo policy hides and denies Add Task activation before browser execution', async ({ page, request }) => {
  await openComposer(page, true);
  const taskRegion = page.locator('[data-oc-target="todo-list"]');
  const originalTaskList = await taskRegion.innerText();

  await page.getByRole('textbox', { name: /message ellai/i })
    .fill('Highlight the Add Task button so I can create a task.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'The page policy does not permit activating Add Task, so I left the list unchanged.',
    { timeout: 90_000 },
  );

  const run = await expectVerifiedFixtureRun(
    request,
    ['liveui_get_current_page', 'liveui_execute'],
    { outcome: 'Denied', postconditionVerified: false, code: 'todo_action_not_allowed' },
  );
  expect(run.results[0].evidence.authorizedTargetIds)
    .not.toContain('route.root.components-todoeditorcomponent-razor-button-add-task');
  expect(run.results[1].arguments).toMatchObject({
    targetId: 'route.root.components-todoeditorcomponent-razor-button-add-task',
    action: 'Click',
  });
  expect(await taskRegion.innerText()).toBe(originalTaskList);
  await expect(page.getByRole('button', { name: 'Add Task' })).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.locator('.mud-overlay')).toHaveCount(0);
});
