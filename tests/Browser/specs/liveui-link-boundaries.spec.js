import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('link activation rejects external destinations, new windows, downloads and script URLs', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: '<main></main>' }));
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    let clicks = 0;
    document.addEventListener('click', (event) => { clicks++; event.preventDefault(); });
    const run = async (html) => {
      document.querySelector('main').innerHTML = html;
      const target = { id: 'link', kind: 'Link', label: 'Open', role: 'link' };
      return execute({ manifest: {}, route: { path: '/', targets: [target] }, target,
        action: { schemaVersion: 'openconnect.liveui/v1', type: 'Click', parameters: {} },
      }, 100, true);
    };
    const external = await run('<a href="https://elsewhere.example/">Open</a>');
    const newWindow = await run('<a href="/docs" target="_blank">Open</a>');
    const download = await run('<a href="/docs" download>Open</a>');
    const script = await run('<a href="javascript:void(0)">Open</a>');
    const internal = await run('<a href="/docs">Open</a>');
    return { external, newWindow, download, script, internal, clicks };
  });
  for (const key of ['external', 'newWindow', 'download', 'script'])
    expect(results[key]).toMatchObject({ outcome: 'Denied', postconditionVerified: false });
  expect(results.internal.outcome).toBe('Uncertain');
  expect(results.clicks).toBe(1);
});
