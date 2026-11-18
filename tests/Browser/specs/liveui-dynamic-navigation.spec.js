import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('dynamic navigation requires a newly rendered matching heading', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<a href="/docs/v1/install">Installation</a><main><h1>Installation</h1></main>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { prepareRoute, awaitRoute } = await import('/liveui.js');
    const route = { id: 'oc.runtime.route.test', path: '/docs/v1/install', title: 'Installation', targets: [] };
    const noObservation = await awaitRoute(route.path, route, 50, true);
    const staleToken = prepareRoute(route);
    history.pushState({}, '', route.path);
    const stale = await awaitRoute(route.path, route, 50, true, staleToken);
    history.replaceState({}, '', '/');
    const freshToken = prepareRoute(route);
    history.pushState({}, '', route.path);
    setTimeout(() => { document.querySelector('main').innerHTML = '<h1>Installation</h1>'; }, 10);
    const fresh = await awaitRoute(route.path, route, 250, true, freshToken);
    history.replaceState({}, '', '/');
    document.querySelector('a').setAttribute('href', '/docs/v1/other');
    const changedLink = prepareRoute(route);
    return { stale, fresh, changedLink, noObservation };
  });
  expect(results.stale).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
  expect(results.fresh).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.changedLink).toBeNull();
  expect(results.noObservation).toMatchObject({ outcome: 'Uncertain', postconditionVerified: false });
});
