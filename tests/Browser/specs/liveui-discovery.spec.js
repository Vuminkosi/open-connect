import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('discovers unique visible semantic controls without exposing input values or code examples', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => route.request().url().endsWith('/liveui.js')
    ? route.fulfill({ contentType: 'text/javascript', body: runtime })
    : route.fulfill({ contentType: 'text/html', body: `
      <h1>Tasks</h1><label>Task title <input value="private task value"></label>
      <button>Add task</button><button>Duplicate</button><button>Duplicate</button>
      <button hidden>Hidden</button><pre><button>Fake action</button></pre>
      <input type="radio" aria-label="Unsupported radio">
      <input type="password" aria-label="Password" value="secret">
      <a href="/docs">Documentation</a><a href="https://elsewhere.example/">External</a>` }));
  await page.goto('http://liveui.test/');
  const result = await page.evaluate(async () => (await import('/liveui.js')).discoverTargets(100));
  expect(result).toEqual(expect.arrayContaining([
    expect.objectContaining({ kind: 'Heading', label: 'Tasks' }),
    expect.objectContaining({ kind: 'Input', label: 'Task title' }),
    expect.objectContaining({ kind: 'Button', label: 'Add task' }),
    expect.objectContaining({ kind: 'Link', label: 'Documentation' }),
  ]));
  const serialized = JSON.stringify(result);
  for (const forbidden of ['private task value', 'secret', 'Password', 'Duplicate', 'Hidden', 'Fake action', 'External', 'Unsupported radio'])
    expect(serialized).not.toContain(forbidden);
});
