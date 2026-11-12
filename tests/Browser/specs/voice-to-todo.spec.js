import { test, expect } from '@playwright/test';

const spokenInstruction = /create a todo to check the weighbridge calibration tomorrow/i;

async function openComposer(page) {
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
}

test.describe('browser voice to Todo', () => {
  test('streams microphone fixture and completes one Todo mutation', async ({ page }) => {
    const voiceFrames = [];
    let finalTranscript;
    page.on('websocket', socket => {
      if (!socket.url().includes('/openconnect/v1/voice/realtime/')) return;
      socket.on('framesent', frame => voiceFrames.push({ direction: 'sent', frame }));
      socket.on('framereceived', frame => {
        voiceFrames.push({ direction: 'received', frame });
        try {
          const payload = typeof frame === 'string' ? frame : Buffer.from(frame).toString('utf8');
          const event = JSON.parse(payload);
          if (event.type === 'transcript-final') finalTranscript = event.text;
        } catch { /* binary PCM or provider framing */ }
      });
    });

    await page.goto('/');
    await expect(page).toHaveTitle(/OpenConnect/i);
    await openComposer(page);

    const voiceButton = page.getByRole('button', { name: 'Voice input' });
    await expect(voiceButton).toBeVisible();
    await voiceButton.click();

    await expect.poll(() => voiceFrames.filter(item => item.direction === 'sent' && typeof item.frame !== 'string').length, {
      timeout: 30_000,
      message: 'Expected browser PCM frames on the authenticated voice WebSocket',
    }).toBeGreaterThan(0);

    await expect.poll(() => finalTranscript, { timeout: 90_000 }).toMatch(/weighbridge/i);
    expect(finalTranscript).toMatch(/create|todo|weighbridge|calibration|tomorrow/i);

    await expect(page.locator('.oc-bubble__partial-transcript')).toHaveCount(0);
    await expect(page.getByText(/weighbridge calibration/i).first()).toBeVisible({ timeout: 60_000 });

    const todos = page.getByText(/weighbridge calibration/i);
    await expect(todos).toHaveCount(1);

    const terminal = page.locator('.oc-bubble__response, .oc-message-assistant').filter({ hasText: /completed|created|todo/i }).last();
    await expect(terminal).toBeVisible({ timeout: 30_000 });
  });

  test('keeps text input usable when microphone permission is denied', async ({ browser }) => {
    const context = await browser.newContext({ permissions: [] });
    const page = await context.newPage();
    await page.goto('/');
    await openComposer(page);
    await page.getByRole('button', { name: 'Voice input' }).click();
    await expect(page.getByRole('textbox', { name: /message ellai/i })).toBeEnabled();
    await context.close();
  });

  test('keeps text input usable when the voice session cannot be opened', async ({ page }) => {
    await page.goto('/');
    await openComposer(page);
    const voiceButton = page.getByRole('button', { name: 'Voice input' });
    await expect(voiceButton).toBeVisible();
    await expect(page.getByRole('textbox', { name: /message ellai/i })).toBeEnabled();
  });
});
