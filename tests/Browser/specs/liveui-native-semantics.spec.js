import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('semantic fallback uses native landmark roles and resolves duplicate headings within their section', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => {
    if (route.request().url().endsWith('/liveui.js'))
      await route.fulfill({ contentType: 'text/javascript', body: runtime });
    else
      await route.fulfill({ contentType: 'text/html', body: `
        <nav aria-label="Documentation navigation"><a href="/docs">Docs</a></nav>
        <main aria-label="Documentation content">
          <input type="search" aria-label="Search documentation">
          <section aria-label="Desktop setup"><h2>Configuration</h2></section>
          <section aria-label="Mobile setup"><h2>Configuration</h2></section>
          <ul aria-label="Topics"><li>Getting started</li></ul>
        </main>` });
  });
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    const targets = [
      { id: 'navigation', kind: 'Region', label: 'Documentation navigation', role: 'navigation' },
      { id: 'main', kind: 'Region', label: 'Documentation content', role: 'main' },
      { id: 'search', kind: 'Input', label: 'Search documentation', role: 'searchbox' },
      { id: 'topics', kind: 'List', label: 'Topics', role: 'list' },
      { id: 'mobile', kind: 'Region', label: 'Mobile setup', role: 'region' },
      { id: 'heading', kind: 'Heading', label: 'Configuration', role: 'heading', parentId: 'mobile' },
    ];
    const route = { path: '/', targets };
    const run = (target, type = 'Inspect') => execute({
      manifest: {}, route, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type, parameters: {} },
    }, 1000, true);
    const landmarks = [];
    for (const target of targets.slice(0, 4)) landmarks.push(await run(target));
    const ambiguous = await run({ id: 'unscoped', kind: 'Heading', label: 'Configuration', role: 'heading' });
    const scoped = await run(targets[5], 'Highlight');
    return { landmarks, ambiguous, scoped,
      highlighted: [...document.querySelectorAll('h2')].map((heading) => heading.hasAttribute('data-openconnect-liveui-highlight')) };
  });

  for (const result of results.landmarks)
    expect(result).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.ambiguous.outcome).toBe('Ambiguous');
  expect(results.scoped).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.highlighted).toEqual([false, true]);
});
