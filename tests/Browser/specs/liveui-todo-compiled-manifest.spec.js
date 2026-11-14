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

test('combines the existing Completed tab and search controls while retaining filtered state', async ({ page }) => {
  const tabReceipt = await executeTarget(page, 'Completed', 'SelectTab');
  expect(tabReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  const searchReceipt = await executeTarget(page, 'Search tasks by title or description...', 'SetValue', 'report');
  expect(searchReceipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });

  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
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
