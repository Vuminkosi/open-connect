import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const manifestPath = path.resolve(testDirectory, '../../../samples/Blazor/obj/Debug/net10.0/openconnect.liveui.manifest.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
const todoRoute = manifest.routes.find((route) => route.path === '/');

if (!todoRoute) throw new Error('The compiled LiveUI manifest does not contain the Todo route.');

async function executeTarget(page, targetLabel, type, value) {
  const target = todoRoute.targets.find((candidate) => candidate.label === targetLabel);
  if (!target) throw new Error('The compiled manifest does not contain target: ' + targetLabel);

  return page.evaluate(async ({ manifestData, route, targetData, actionType, actionValue }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: {
        schemaVersion: manifestData.schemaVersion,
        type: actionType,
        parameters: actionValue === undefined ? {} : { value: actionValue },
      },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target, actionType: type, actionValue: value });
}

async function waitForTargetState(page, targetLabel, stateName, stateValue) {
  const target = todoRoute.targets.find((candidate) => candidate.label === targetLabel);
  if (!target) throw new Error('The compiled manifest does not contain target: ' + targetLabel);

  return page.evaluate(async ({ manifestData, route, targetData, name, value }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: {
        schemaVersion: manifestData.schemaVersion,
        type: 'WaitForState',
        parameters: { stateName: name, stateValue: value },
      },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target, name: stateName, value: stateValue });
}

async function highlightTarget(page, targetLabel) {
  const target = todoRoute.targets.find((candidate) => candidate.label === targetLabel);
  if (!target) throw new Error('The compiled manifest does not contain target: ' + targetLabel);

  return page.evaluate(async ({ manifestData, route, targetData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: { schemaVersion: manifestData.schemaVersion, type: 'Highlight', parameters: {} },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target });
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
});

test('selects the existing Completed tab and verifies its filtered tasks', async ({ page }) => {
  const receipt = await executeTarget(page, 'Completed', 'SelectTab');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, verifiedState: 'state_verified' });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
});

test('searches through the existing Todo search input and verifies rendered results', async ({ page }) => {
  const receipt = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'report');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'value_set' });
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toBeVisible();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
});

test('scopes the compiled search target to its parent region when an outside duplicate exists', async ({ page }) => {
  const region = todoRoute.targets.find((candidate) => candidate.label === 'todo-list');
  const search = todoRoute.targets.find((candidate) => candidate.label === 'Search tasks by title or description...');
  expect(region).toBeDefined();
  expect(search).toBeDefined();
  expect(search.parentId).toBe(region.id);

  await page.evaluate(() => {
    const duplicate = document.createElement('input');
    duplicate.placeholder = 'Search tasks by title or description...';
    duplicate.setAttribute('aria-label', 'Search tasks by title or description...');
    document.body.append(duplicate);
  });

  const receipt = await executeTarget(page, search.label, 'SetValue', 'scoped report');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'value_set' });
  await expect(page.locator('[data-oc-target="todo-list"] input[placeholder="Search tasks by title or description..."]')).toHaveValue('scoped report');
  await expect(page.locator('body > input[placeholder="Search tasks by title or description..."]')).toHaveValue('');
});

test('waits for the compiled task list to reflect the existing search control', async ({ page }) => {
  const setValue = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'report');
  expect(setValue).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  const receipt = await waitForTargetState(page, 'todo-list', 'textContains', 'report');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
});

test('verifies the stable Todo region after filtering to the rendered empty state', async ({ page }) => {
  const setValue = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'no-matching-task');
  expect(setValue).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  const receipt = await waitForTargetState(page, 'todo-list', 'textContains', 'No tasks to display');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  await expect(page.getByText('No tasks to display', { exact: true })).toBeVisible();
  await expect(page.getByText('Review the quarterly report', { exact: true })).toHaveCount(0);
});

test('keeps Todo filtering available when composer voice output is disabled', async ({ page }) => {
  test.setTimeout(30000);
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
  await page.locator('.oc-bubble__header-actions button').first().click();
  const voiceOutput = page.getByText('Voice output', { exact: true });
  await expect(voiceOutput).toBeVisible();
  await voiceOutput.click();
  await expect(page.locator('.oc-bubble__live-speak')).not.toHaveClass(/--active/);

  const setValue = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'report');
  expect(setValue).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  const receipt = await waitForTargetState(page, 'todo-list', 'textContains', 'report');

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Prepare a launch checklist', { exact: true })).toHaveCount(0);
});

test('combines the existing Completed tab and search controls while retaining filtered state', async ({ page }) => {
  const tabReceipt = await executeTarget(page, 'Completed', 'SelectTab');
  expect(tabReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  const searchReceipt = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'report');
  expect(searchReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  const selectedReceipt = await waitForTargetState(page, 'Completed', 'selected', 'true');
  const resultsReceipt = await waitForTargetState(page, 'Task list', 'textContains', 'report');
  const excludedReceipt = await waitForTargetState(page, 'Task list', 'textNotContains', 'Gather feedback for report');
  const highlightReceipt = await highlightTarget(page, 'todo-list');

  expect(selectedReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  expect(resultsReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  expect(excludedReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, code: 'state_verified' });
  expect(highlightReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' });

  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-oc-target="todo-list"]')).toHaveAttribute('data-openconnect-liveui-highlight', 'true');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('highlights the existing task region without opening a tour dialog', async ({ page }) => {
  const target = todoRoute.targets.find((candidate) => candidate.selector === '[data-oc-target=\"todo-list\"]');
  expect(target).toBeDefined();

  const receipt = await page.evaluate(async ({ manifestData, route, targetData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: { schemaVersion: manifestData.schemaVersion, type: 'Highlight', parameters: {} },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target });

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' });
  await expect(page.locator('[data-oc-target=\"todo-list\"][data-openconnect-liveui-highlight=\"true\"]')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('resolves the compiled MudBlazor task list and highlights it without a popup', async ({ page }) => {
  const target = todoRoute.targets.find((candidate) => candidate.kind === 'List' && candidate.label === 'Task list');
  expect(target).toBeDefined();
  expect(target.selector).toBe('.mud-list');

  const receipt = await page.evaluate(async ({ manifestData, route, targetData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: { schemaVersion: manifestData.schemaVersion, type: 'Highlight', parameters: {} },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target });

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' });
  await expect(page.locator('.mud-list[data-openconnect-liveui-highlight=\"true\"]')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('highlights the unique visible task row selected by its exact title', async ({ page }) => {
  const target = todoRoute.targets.find((candidate) => candidate.kind === 'List' && candidate.label === 'Task list');
  const title = 'Review the quarterly report';
  const receipt = await page.evaluate(async ({ manifestData, route, targetData, text }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: { schemaVersion: manifestData.schemaVersion, type: 'Highlight', parameters: { value: text } },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target, text: title });

  expect(receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true, verifiedState: 'highlighted' });
  const highlighted = page.locator('.mud-list [data-openconnect-liveui-highlight="true"]');
  await expect(highlighted).toHaveCount(1);
  await expect(highlighted).toContainText(title);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('does not highlight when the visible task title matches multiple rows', async ({ page }) => {
  const target = todoRoute.targets.find((candidate) => candidate.kind === 'List' && candidate.label === 'Task list');
  const title = 'Review the quarterly report';
  await page.locator('.mud-list-item').filter({ hasText: title }).first().evaluate((item) => {
    const duplicate = item.cloneNode(true);
    duplicate.querySelector('[data-openconnect-liveui-highlight]')?.removeAttribute('data-openconnect-liveui-highlight');
    item.parentElement.append(duplicate);
  });

  const receipt = await page.evaluate(async ({ manifestData, route, targetData, text }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route,
      target: targetData,
      action: { schemaVersion: manifestData.schemaVersion, type: 'Highlight', parameters: { value: text } },
    }, 5000, true);
  }, { manifestData: manifest, route: todoRoute, targetData: target, text: title });

  expect(receipt).toMatchObject({ outcome: 'Ambiguous', code: 'text_match_ambiguous' });
  await expect(page.locator('.mud-list [data-openconnect-liveui-highlight="true"]')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
