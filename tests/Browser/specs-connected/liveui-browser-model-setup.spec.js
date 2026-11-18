import { expect, test } from '@playwright/test';

test('downloadable browser model starts from the native user gesture and reports readiness', async ({ page }) => {
  await page.addInitScript(() => {
    let modelReady = false;
    globalThis.LanguageModel = {
      availability: async () => modelReady ? 'available' : 'downloadable',
      create: options => {
        globalThis.__liveUiModelActivationAtCreate = navigator.userActivation?.isActive ?? false;
        options.monitor({
          addEventListener: (name, handler) => {
            if (name === 'downloadprogress') handler({ loaded: 50, total: 100 });
          },
        });
        return new Promise(resolve => setTimeout(() => {
          modelReady = true;
          resolve({
            prompt: async prompt => {
              globalThis.__liveUiPromptCalls = (globalThis.__liveUiPromptCalls ?? 0) + 1;
              const capabilityText = prompt.split('Capabilities: ').at(-1);
              const capabilities = JSON.parse(capabilityText);
              const target = capabilities.find(capability =>
                capability.targetId.endsWith('tab-completed') && capability.actions.includes('activate'));
              const schemaVersion = prompt.match(/Schema: ([^\n]+)/)?.[1];
              return JSON.stringify({
                schemaVersion,
                actions: [{ type: 'activate', targetId: target.targetId }],
              });
            },
            destroy() {},
          });
        }, 250));
      },
    };
  });

  await page.goto('/');
  const enableButton = page.getByRole('button', { name: 'Enable on-device AI' });
  await expect(enableButton).toBeVisible();
  await enableButton.click();
  await page.waitForFunction(() => globalThis.__liveUiModelActivationAtCreate === true);
  await expect(page.getByRole('progressbar', { name: 'On-device model download progress' })).toHaveAttribute('value', '50');
  await expect(page.getByText('On-device AI ready')).toBeVisible();

  await page.locator('.oc-anchored-fab').click();
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Use the on-device model to show my completed tasks.');
  await page.getByRole('button', { name: /send to ellai/i }).click();
  await expect(page.getByRole('log', { name: 'Conversation' }))
    .toContainText('The on-device model selected and verified the Completed tab.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await page.waitForFunction(() => globalThis.__liveUiPromptCalls > 0);

  const response = await page.request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const run = (await response.json()).runs.at(-1);
  expect(run.results.map(result => result.name)).toEqual([
    'liveui_infer_plan', 'liveui_execute', 'liveui_await_state',
  ]);
  expect(run.results[0].evidence).toMatchObject({
    provider: 'Browser',
    targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
    action: 'activate',
  });
});
