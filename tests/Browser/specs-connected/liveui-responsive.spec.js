import { expect, test } from '@playwright/test';

for (const width of [320, 360, 390, 768, 1280, 1920]) {
  test(`LiveUI composer fits and remains usable at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    const circuitConnected = page.waitForEvent('websocket', {
      predicate: socket => socket.url().includes('/_blazor'),
    });
    await page.goto('/');
    await circuitConnected;
    await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();

    const launcher = page.locator('.oc-anchored-fab');
    await launcher.click();
    await expect(launcher, `launcher should open at ${width}px`).toHaveAttribute('aria-label', 'Close Ellai');
    const composer = page.getByRole('dialog', { name: 'Ellai composer' });
    await expect(composer).toBeVisible();
    const message = page.getByRole('textbox', { name: /message ellai/i });
    await expect(message).toBeVisible();
    await message.fill('viewport check');
    const send = page.getByRole('button', { name: /send to ellai/i });
    await expect(send).toBeVisible();
    await expect(send).toBeEnabled();

    const layout = await page.evaluate(() => {
      const dialogElement = document.querySelector('[role="dialog"][aria-label="Ellai composer"]');
      const dialog = dialogElement?.getBoundingClientRect();
      const launcherElement = document.querySelector('.oc-anchored-fab');
      const launcher = launcherElement?.getBoundingClientRect();
      const launcherHost = launcherElement?.parentElement?.getBoundingClientRect();
      const overflowingElements = [...document.querySelectorAll('body *')]
        .map(element => ({
          tag: element.tagName,
          className: typeof element.className === 'string' ? element.className : '',
          left: Math.round(element.getBoundingClientRect().left),
          right: Math.round(element.getBoundingClientRect().right),
          width: Math.round(element.getBoundingClientRect().width),
        }))
        .filter(element => element.width > 0 && (element.left < 0 || element.right > window.innerWidth))
        .slice(0, 12);
      return {
        viewportWidth: window.innerWidth,
        documentWidth: document.documentElement.scrollWidth,
        dialogLeft: dialog?.left ?? Number.NaN,
        dialogRight: dialog?.right ?? Number.NaN,
        launcherBottom: launcher?.bottom ?? Number.NaN,
        launcherHostBottom: launcherHost?.bottom ?? Number.NaN,
        launcherRight: launcher?.right ?? Number.NaN,
        launcherHostRight: launcherHost?.right ?? Number.NaN,
        overflowingElements,
      };
    });
    expect(layout.documentWidth, `horizontal overflow at ${width}px: ${JSON.stringify(layout.overflowingElements)}`)
      .toBeLessThanOrEqual(layout.viewportWidth);
    expect(layout.dialogLeft, `composer left edge at ${width}px`).toBeGreaterThanOrEqual(0);
    expect(layout.dialogRight, `composer right edge at ${width}px`).toBeLessThanOrEqual(width);
    expect(Math.abs(layout.launcherBottom - layout.launcherHostBottom), `launcher vertical anchor at ${width}px`)
      .toBeLessThanOrEqual(2);
    expect(Math.abs(layout.launcherRight - layout.launcherHostRight), `launcher horizontal anchor at ${width}px`)
      .toBeLessThanOrEqual(2);

    await page.getByRole('button', { name: 'Close Ellai' }).click();
    await expect(composer).toBeHidden();
  });
}
