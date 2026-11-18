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

test('a guided tour highlights the first area and asks before continuing', async ({ page, request }) => {
  await openComposer(page, false);
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Please show me around.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'This task region shows your work and its filters. Would you like to continue the tour?', { timeout: 90_000 });
  const run = await expectVerifiedFixtureRun(request,
    ['liveui_infer_plan', 'liveui_get_current_page', 'liveui_highlight']);
  expect(run.scenarioName).toBe('tour');
  expect(run.results.at(-1).evidence).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
});

test('task lookup plays a narrated visual timeline from the list to the exact task row', async ({ page, request }) => {
  await openComposer(page, true);
  await page.evaluate(() => {
    window.__liveuiHighlightTimeline = [];
    const record = () => {
      const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')]
        .map((element) => element.innerText?.trim() || element.tagName)
        .sort();
      const previous = window.__liveuiHighlightTimeline.at(-1);
      if (JSON.stringify(previous) !== JSON.stringify(highlighted)) window.__liveuiHighlightTimeline.push(highlighted);
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true,
      attributeFilter: ['data-openconnect-liveui-highlight'],
      subtree: true,
    });
  });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Pull up my tasks to find the most recent one.');
  await page.getByRole('button', { name: /send to ellai/i }).click();

  const title = 'Prepare a launch checklist';
  await expect(page.getByRole('log', { name: 'Conversation' }))
    .toContainText(`I opened your task list so you can see the available work. The most recently created task is ${title}. I brought its row into focus while I explained it.`, { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'All' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('textbox', { name: 'Search tasks by title or description...' })).toHaveValue(title);
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.length), { timeout: 15_000 }).toBeGreaterThanOrEqual(3);
  const timeline = await page.evaluate(() => window.__liveuiHighlightTimeline);
  expect(timeline[0]).toEqual(expect.arrayContaining([expect.stringContaining('Workflows & Tasks')]));
  expect(timeline.at(-1)).toEqual(expect.arrayContaining([expect.stringContaining(title)]));

  const run = await expectVerifiedFixtureRun(request, [
    'todo.list', 'liveui_infer_plan', 'liveui_get_current_page', 'liveui_execute',
    'liveui_await_state', 'liveui_execute', 'liveui_await_state',
    'liveui_highlight', 'liveui_highlight', 'liveui_highlight',
  ]);
  expect(run.scenarioName).toBe('most_recent_task');
  expect(run.results[0].evidence).toMatchObject({ title });
  expect(run.results[1].evidence).toMatchObject({ fallback: 'server-model' });
  const titleStart = run.answer.indexOf(title);
  expect(run.results.at(-3).arguments).toMatchObject({
    targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
    speechStartCharacter: 0,
    speechEndCharacter: run.answer.indexOf('.') + 1,
  });
  expect(run.results.at(-2).arguments).toMatchObject({
    targetId: 'route.root.components-todolistcomponent-razor-list-task-list',
    matchText: title,
    speechStartCharacter: titleStart,
    speechEndCharacter: titleStart + title.length + 1,
  });
  const rowHighlight = run.results.at(-1);
  expect(rowHighlight.arguments).toMatchObject({
    matchText: title,
    speechStartCharacter: run.answer.indexOf('I brought its row'),
    speechEndCharacter: run.answer.length,
  });
  expect(rowHighlight.evidence).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/speech');
    const speech = await response.json();
    return speech.calls.filter((call) => call.voiceId === 'af_heart').map((call) => call.text);
  }, { timeout: 15_000 }).toEqual(expect.arrayContaining([
    expect.stringContaining('I opened your task list'), expect.stringContaining(title), expect.stringContaining('I brought its row'),
  ]));
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

test('asking which tasks are completed opens the Completed tab and narrates each visible row', async ({ page, request }) => {
  await openComposer(page, true);
  await page.evaluate(() => {
    window.__liveuiHighlightTimeline = [];
    const record = () => {
      const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')]
        .map((element) => element.innerText?.trim() || element.tagName)
        .sort();
      const previous = window.__liveuiHighlightTimeline.at(-1);
      if (JSON.stringify(previous) !== JSON.stringify(highlighted)) window.__liveuiHighlightTimeline.push(highlighted);
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true,
      attributeFilter: ['data-openconnect-liveui-highlight'],
      subtree: true,
    });
  });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Which task is completed?');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'I opened the Completed tab so you can see the finished work. One completed task is Review the quarterly report. Another is Draft the annual report.',
    { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.length), { timeout: 15_000 }).toBeGreaterThanOrEqual(5);
  const timeline = await page.evaluate(() => window.__liveuiHighlightTimeline);
  expect(timeline[0]).toEqual(expect.arrayContaining([expect.stringContaining('Workflows & Tasks')]));
  expect(timeline.at(-1)).toEqual(expect.arrayContaining([expect.stringContaining('Draft the annual report')]));

  const run = await expectVerifiedFixtureRun(request, [
    'todo.list', 'liveui_infer_plan', 'liveui_get_current_page', 'liveui_execute',
    'liveui_await_state', 'liveui_highlight', 'liveui_highlight', 'liveui_highlight',
  ]);
  expect(run.scenarioName).toBe('completed_task_lookup');
  expect(run.results[0].evidence.titles).toEqual(['Review the quarterly report', 'Draft the annual report']);
  expect(run.results.at(-2).arguments.matchText).toBe('Review the quarterly report');
  expect(run.results.at(-1).arguments.matchText).toBe('Draft the annual report');
  await expect.poll(async () => {
    const speech = await (await request.get('http://127.0.0.1:5110/__acceptance/speech')).json();
    return speech.calls.filter((call) => call.voiceId === 'af_heart').map((call) => call.text);
  }, { timeout: 15_000 }).toEqual(expect.arrayContaining([
    expect.stringContaining('Review the quarterly report'), expect.stringContaining('Draft the annual report'),
  ]));
  if (process.env.OPENCONNECT_LIVEUI_REAL_KOKORO_WORKER) {
    const speech = await (await request.get('http://127.0.0.1:5110/__acceptance/speech')).json();
    const kokoroCalls = speech.calls.filter((call) => call.voiceId === 'af_heart' && call.provider === 'kokoro');
    expect(kokoroCalls.length).toBeGreaterThanOrEqual(2);
    expect(kokoroCalls.every((call) => call.audioBytes > 0)).toBe(true);
  }
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

test('a status highlight request uses the visible task region and verifies its receipt without asking for a row title', async ({ page, request }) => {
  await openComposer(page, true);
  await page.evaluate(() => {
    window.__liveuiHighlightTimeline = [];
    const record = () => {
      const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')]
        .map((element) => element.innerText?.trim() || element.tagName)
        .sort();
      const previous = window.__liveuiHighlightTimeline.at(-1);
      if (JSON.stringify(previous) !== JSON.stringify(highlighted)) window.__liveuiHighlightTimeline.push(highlighted);
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true,
      attributeFilter: ['data-openconnect-liveui-highlight'],
      subtree: true,
    });
  });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Highlight the task status.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  const response = page.getByRole('log', { name: 'Conversation' });
  await expect(response).toContainText(
    'I highlighted the Todo task list so you can see the current task statuses.',
    { timeout: 90_000 },
  );
  await expect(response).not.toContainText(/which task|exact task title|what should i highlight/i);
  await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.length), { timeout: 15_000 }).toBeGreaterThan(1);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.some((entry) =>
    entry.some((text) => text.includes('Prepare a launch checklist'))))).toBe(true);

  const run = await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_highlight']);
  expect(run.scenarioName).toBe('status_highlight');
  expect(run.results.at(-1)).toMatchObject({
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    evidence: { outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' },
  });
  await expect.poll(async () => {
    const speech = await (await request.get('http://127.0.0.1:5110/__acceptance/speech')).json();
    return speech.calls.some((call) => call.voiceId === 'af_heart' && call.text.includes('I highlighted the Todo task list'));
  }, { timeout: 15_000 }).toBe(true);
});

test('a failed highlight is retried against the visible task region without asking for a row title', async ({ page, request }) => {
  await openComposer(page, true);
  await page.evaluate(() => {
    window.__liveuiHighlightTimeline = [];
    const record = () => {
      const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')]
        .map((element) => element.innerText?.trim() || element.tagName)
        .sort();
      const previous = window.__liveuiHighlightTimeline.at(-1);
      if (JSON.stringify(previous) !== JSON.stringify(highlighted)) window.__liveuiHighlightTimeline.push(highlighted);
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true,
      attributeFilter: ['data-openconnect-liveui-highlight'],
      subtree: true,
    });
  });
  await page.getByRole('textbox', { name: /message ellai/i }).fill(
    "That highlight didn't come through. The Task list and todo-list region are visible and can be highlighted. Retry highlighting the task list.",
  );
  await page.getByRole('button', { name: /send to ellai/i }).click();
  const response = page.getByRole('log', { name: 'Conversation' });
  await expect(response).toContainText(
    'I retried the highlight and highlighted the Todo task list so you can see the task statuses.',
    { timeout: 90_000 },
  );
  await expect(response).not.toContainText(/which task|exact task title|what should i highlight/i);
  await expect(page.locator('.oc-bubble__live-speak')).toHaveClass(/--active/);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.length), { timeout: 15_000 }).toBeGreaterThan(1);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.some((entry) =>
    entry.some((text) => text.includes('Prepare a launch checklist'))))).toBe(true);

  const run = await expectVerifiedFixtureRun(request, ['liveui_get_current_page', 'liveui_highlight']);
  expect(run.scenarioName).toBe('highlight_retry');
  expect(run.results.at(-1)).toMatchObject({
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    evidence: { outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' },
  });
  await expect.poll(async () => {
    const speech = await (await request.get('http://127.0.0.1:5110/__acceptance/speech')).json();
    return speech.calls.some((call) => call.voiceId === 'af_heart' && call.text.includes('I retried the highlight'));
  }, { timeout: 15_000 }).toBe(true);
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
