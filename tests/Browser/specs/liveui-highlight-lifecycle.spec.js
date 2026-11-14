import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const runtimeScript = fs.readFileSync(
  path.resolve(testDirectory, '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js'),
  'utf8',
);

const route = {
  id: 'home',
  path: '/',
  title: 'LiveUI highlight lifecycle',
  targets: [
    { id: 'region.one', kind: 'Region', label: 'First region', selector: '[data-target="one"]' },
    { id: 'region.two', kind: 'Region', label: 'Second region', selector: '[data-target="two"]' },
  ],
};

const manifest = {
  schemaVersion: 'openconnect.liveui/v1',
  buildId: 'browser-test',
  contentHash: 'sha256:browser-test',
  routes: [route],
};

test.beforeEach(async ({ page }) => {
  await page.route('http://liveui.test/**', async (request) => {
    const url = new URL(request.request().url());
    if (url.pathname === '/_content/OpenConnect.LiveUI.Blazor/liveui.js') {
      await request.fulfill({
        status: 200,
        contentType: 'text/javascript',
        body: runtimeScript,
      });
      return;
    }

    await request.fulfill({
      status: 200,
      contentType: 'text/html',
      body: '<!doctype html><html><head><title>LiveUI highlight lifecycle</title></head><body><main><section data-target="one">First region</section><section data-target="two">Second region</section></main></body></html>',
    });
  });
  await page.goto('http://liveui.test/');
});

test('expires temporary highlights independently and explicit clear cancels their timers', async ({ page }) => {
  const receipts = await page.evaluate(async ({ manifestData, routeData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    const execute = (type, target, parameters = {}) => runtime.execute({
      manifest: manifestData,
      route: routeData,
      target,
      action: { schemaVersion: manifestData.schemaVersion, type, parameters },
    }, 5000, false);

    const first = await execute('Highlight', routeData.targets[0], { highlightDurationMilliseconds: 250 });
    const second = await execute('Highlight', routeData.targets[1], { highlightDurationMilliseconds: 700 });
    return { first, second };
  }, { manifestData: manifest, routeData: route });

  expect(receipts.first).toMatchObject({ outcome: 'Completed', code: 'highlight_temporary' });
  expect(receipts.second).toMatchObject({ outcome: 'Completed', code: 'highlight_temporary' });
  await expect(page.locator('[data-target="one"]')).toHaveAttribute('data-openconnect-liveui-highlight', 'true');
  await expect(page.locator('[data-target="two"]')).toHaveAttribute('data-openconnect-liveui-highlight', 'true');

  await expect(page.locator('[data-target="one"]')).not.toHaveAttribute('data-openconnect-liveui-highlight', 'true', { timeout: 1000 });
  await expect(page.locator('[data-target="two"]')).toHaveAttribute('data-openconnect-liveui-highlight', 'true');

  await page.evaluate(async ({ manifestData, routeData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route: routeData,
      target: null,
      action: { schemaVersion: manifestData.schemaVersion, type: 'ClearHighlight', parameters: {} },
    }, 5000, false);
  }, { manifestData: manifest, routeData: route });

  await expect(page.locator('[data-target="two"]')).not.toHaveAttribute('data-openconnect-liveui-highlight', 'true');
  await page.waitForTimeout(750);
  await expect(page.locator('[data-target="two"]')).not.toHaveAttribute('data-openconnect-liveui-highlight', 'true');
});

test('a repeated temporary highlight replaces the earlier expiry timer', async ({ page }) => {
  const runHighlight = async (durationMilliseconds) => page.evaluate(async ({ manifestData, routeData, duration }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    return runtime.execute({
      manifest: manifestData,
      route: routeData,
      target: routeData.targets[0],
      action: {
        schemaVersion: manifestData.schemaVersion,
        type: 'Highlight',
        parameters: { highlightDurationMilliseconds: duration },
      },
    }, 5000, false);
  }, { manifestData: manifest, routeData: route, duration: durationMilliseconds });

  expect(await runHighlight(250)).toMatchObject({ outcome: 'Completed', code: 'highlight_temporary' });
  await page.waitForTimeout(125);
  expect(await runHighlight(500)).toMatchObject({ outcome: 'Completed', code: 'highlight_temporary' });

  await page.waitForTimeout(200);
  await expect(page.locator('[data-target="one"]')).toHaveAttribute('data-openconnect-liveui-highlight', 'true');
  await expect(page.locator('[data-target="one"]')).not.toHaveAttribute('data-openconnect-liveui-highlight', 'true', { timeout: 1000 });
});
