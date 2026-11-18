import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');
const docsUrl = process.env.OPENCONNECT_DOCS_TEST_URL;

test('discovers the unannotated documentation index sections', async ({ page }) => {
  test.skip(!docsUrl, 'A running documentation test instance is required.');
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('**/__openconnect-test-runtime.js', (route) => route.fulfill({ contentType: 'text/javascript', body: runtime }));
  await page.goto(new URL('/docs', docsUrl).href);
  const expected = await page.locator('main section').first().getByRole('heading', { level: 2 }).innerText();
  const result = await page.evaluate(async (expectedLabel) => {
    const { discoverTargets, execute } = await import('/__openconnect-test-runtime.js');
    const targets = discoverTargets(500).map((target, index) => ({ ...target, id: `test-${index}` }));
    const target = targets.find((item) => item.kind === 'Region' && item.label === expectedLabel);
    const receipt = await execute({ manifest: {}, route: { path: '/docs', targets }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type: 'Highlight', parameters: {} } }, 1000, true);
    return { target, receipt, highlighted: [...document.querySelectorAll('main section')]
      .map((section) => section.hasAttribute('data-openconnect-liveui-highlight')) };
  }, expected.trim());
  expect(result.target).toMatchObject({ kind: 'Region', label: expected.trim() });
  expect(result.receipt).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(result.highlighted[0]).toBe(true);
});

test('discovers a concrete documentation link on the rendered index', async ({ page }) => {
  test.skip(!docsUrl, 'A running documentation test instance is required.');
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('**/__openconnect-test-runtime.js', (route) => route.fulfill({ contentType: 'text/javascript', body: runtime }));
  await page.goto(new URL('/docs', docsUrl).href);
  const expectedPath = await page.locator('main section a').first().getAttribute('href');
  const candidates = await page.evaluate(async () => (await import('/__openconnect-test-runtime.js')).discoverTargets(500));
  expect(candidates).toContainEqual(expect.objectContaining({ kind: 'Link', path: expectedPath }));
});

for (const [name, viewport] of [['desktop', { width: 1440, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
  test(`resolves the rendered documentation link on ${name}`, async ({ page }) => {
    test.skip(!docsUrl, 'A running documentation test instance is required.');
    await page.setViewportSize(viewport);
    const runtime = await fs.readFile(runtimePath, 'utf8');
    await page.route('**/__openconnect-test-runtime.js', (route) => route.fulfill({ contentType: 'text/javascript', body: runtime }));
    await page.goto(new URL('/docs', docsUrl).href);
    const section = page.locator('main section').first();
    const parentLabel = (await section.getByRole('heading', { level: 2 }).innerText()).trim();
    const card = section.locator('a').first();
    const targetPath = await card.getAttribute('href');
    const title = (await card.locator('strong').innerText()).trim();
    const observation = await page.evaluate(async ({ targetPath, title, parentLabel }) => {
      const { prepareRoute } = await import('/__openconnect-test-runtime.js');
      return prepareRoute({ id: 'oc.runtime.route.test', path: targetPath, title }, parentLabel);
    }, { targetPath, title, parentLabel });
    expect(observation).not.toBeNull();
    const follow = page.evaluate(async ({ targetPath, title, observation }) => {
      const { followRoute } = await import('/__openconnect-test-runtime.js');
      return followRoute({ id: 'oc.runtime.route.test', path: targetPath, title }, observation);
    }, { targetPath, title, observation }).catch((error) => {
      if (!/Execution context was destroyed|navigation/i.test(error.message)) throw error;
      return true;
    });
    await Promise.all([page.waitForURL(new URL(targetPath, docsUrl).href), follow]);
    await expect(page.getByRole('heading', { level: 1, name: title, exact: true })).toBeVisible();
  });
}
