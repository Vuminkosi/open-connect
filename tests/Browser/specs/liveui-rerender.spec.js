import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('tab state verification follows rerenders without clicking twice or trusting detached elements', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<button role="tab" aria-selected="false">Completed</button>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const target = { id: 'completed', kind: 'Tab', label: 'Completed', role: 'tab' };
    const route = { path: '/', targets: [target] };
    async function run(mode) {
      document.body.innerHTML = '<button role="tab" aria-selected="false">Completed</button>';
      let clicks = 0;
      const original = document.querySelector('button');
      original.addEventListener('click', () => {
        clicks++;
        const replacement = original.cloneNode(true);
        replacement.setAttribute('aria-selected', mode === 'stale' ? 'false' : 'true');
        original.replaceWith(replacement);
        if (mode === 'stale') original.setAttribute('aria-selected', 'true');
        if (mode === 'navigation') history.pushState({}, '', '/other');
      });
      const result = await execute({ manifest: {}, route, target, action: {
        schemaVersion: 'openconnect.liveui/v1', type: 'SelectTab', parameters: {},
      } }, 100, true);
      history.replaceState({}, '', '/');
      return { result, clicks };
    }
    return { replaced: await run('replaced'), stale: await run('stale'), navigation: await run('navigation') };
  });
  expect(results.replaced.result).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.stale.result.outcome).not.toBe('Completed');
  expect(results.navigation.result.outcome).not.toBe('Completed');
  for (const result of Object.values(results)) expect(result.clicks).toBe(1);
});
