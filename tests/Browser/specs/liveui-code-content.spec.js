import { expect, test } from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const runtimePath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../src/OpenConnect.LiveUI.Blazor/wwwroot/liveui.js');

test('code examples are not discovered as interactive controls', async ({ page }) => {
  const runtime = await fs.readFile(runtimePath, 'utf8');
  await page.route('http://liveui.test/**', async (route) => {
    if (route.request().url().endsWith('/liveui.js'))
      await route.fulfill({ contentType: 'text/javascript', body: runtime });
    else
      await route.fulfill({ contentType: 'text/html', body: `
        <pre><code><button data-openconnect-liveui-id="code-save">Save</button>
        <button id="code-only">Code only</button></code></pre>
        <button id="real-save">Save</button>` });
  });
  await page.goto('http://liveui.test/');
  const results = await page.evaluate(async () => {
    const { execute } = await import('/liveui.js');
    let realClicks = 0;
    let codeClicks = 0;
    let containerClicks = 0;
    document.querySelector('#real-save').addEventListener('click', () => realClicks++);
    document.querySelector('pre').addEventListener('click', () => containerClicks++);
    for (const button of document.querySelectorAll('pre button'))
      button.addEventListener('click', () => codeClicks++);
    const run = (target, type = 'Click') => execute({
      manifest: {}, route: { path: '/', targets: [] }, target,
      action: { schemaVersion: 'openconnect.liveui/v1', type, parameters: {} },
    }, 1000, true);
    const semantic = await run({ id: 'save', kind: 'Button', label: 'Save' });
    const codeOnly = await run({ id: 'missing', kind: 'Button', label: 'Code only' });
    const markedCode = await run({ id: 'code-save', kind: 'Button', label: 'Save' });
    const selectedCode = await run({ id: 'selected-code', kind: 'Button', label: 'Code only', selector: '#code-only' });
    const codeContent = await run({ id: 'code-content', kind: 'Region', label: 'Code example', selector: 'pre' }, 'Highlight');
    const codeContainerClick = await run({ id: 'code-content', kind: 'Region', label: 'Code example', selector: 'pre' });
    return { semantic, codeOnly, markedCode, selectedCode, codeContent, codeContainerClick, realClicks, codeClicks, containerClicks };
  });

  expect(results.semantic).toMatchObject({ outcome: 'Uncertain', code: 'click_dispatched_without_postcondition' });
  expect(results.realClicks).toBe(1);
  expect(results.codeClicks).toBe(0);
  expect(results.codeOnly.outcome).toBe('MissingTarget');
  expect(results.markedCode.outcome).toBe('MissingTarget');
  expect(results.selectedCode.outcome).toBe('MissingTarget');
  expect(results.codeContent).toMatchObject({ outcome: 'Completed', postconditionVerified: true });
  expect(results.codeContainerClick).toMatchObject({ outcome: 'Denied', code: 'code_content_not_interactive' });
  expect(results.containerClicks).toBe(0);
});
