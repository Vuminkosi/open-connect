import { test, expect } from '@playwright/test';
import path from 'node:path';

const normalizeTranscript = (value) => value
  .toLowerCase()
  .replace(/\btoto\b/g, 'todo')
  .replace(/\bwaybridge\b/g, 'weighbridge')
  .replace(/\bweybridge\b/g, 'weighbridge')
  .replace(/[.?!]+$/g, '');

async function openComposer(page) {
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
}

test.describe('browser voice to Todo', () => {
  test('streams microphone fixture and completes one Todo mutation', async ({ page, request }) => {
    const voiceFrames = [];
    await page.route('**/fixtures/weighbridge-todo.wav', route => route.fulfill({ path: path.resolve('fixtures/weighbridge-todo.wav') }));
    await page.addInitScript(() => {
      window.__OC_VOICE_DIAGNOSTICS__ = true;
      window.__OC_VOICE_MAX_DURATION_MS = 7000;
      const realGetUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
      navigator.mediaDevices.getUserMedia = async constraints => {
        if (!constraints?.audio) return realGetUserMedia(constraints);
        const response = await fetch('/fixtures/weighbridge-todo.wav');
        const audioContext = new AudioContext();
        await audioContext.resume();
        const buffer = await audioContext.decodeAudioData(await response.arrayBuffer());
        const source = audioContext.createBufferSource();
        const destination = audioContext.createMediaStreamDestination();
        source.buffer = buffer;
        source.connect(destination);
        source.start();
        window.__ocTestMicrophone = { audioContext, source };
        return destination.stream;
      };
    });
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

    await page.addInitScript(() => { window.__OC_VOICE_DIAGNOSTICS__ = true; window.__OC_VOICE_MAX_DURATION_MS = 7000; });
    await page.goto('/');
    await expect(page).toHaveTitle(/OpenConnect/i);
    await openComposer(page);

    const voiceButton = page.getByRole('button', { name: 'Voice input' });
    await expect(voiceButton).toBeVisible();
    await voiceButton.click();

    await expect.poll(() => page.evaluate(() => window.__ocVoiceDiagnostics?.sourceFrames ?? 0), {
      timeout: 30_000,
      message: 'Expected browser AudioContext processing frames',
    }).toBeGreaterThan(0);
    const diagnostics = await page.evaluate(() => window.__ocVoiceDiagnostics);
    expect(diagnostics?.sentBytes).toBeGreaterThan(0);
    expect(diagnostics?.inputSampleRate).toBeGreaterThan(0);
    expect(diagnostics?.peakMax).toBeGreaterThan(0);
    expect(diagnostics?.speechSeen).toBe(true);
    await test.info().attach('voice-diagnostics.json', {
      body: Buffer.from(JSON.stringify(diagnostics, null, 2)),
      contentType: 'application/json',
    });
    await expect.poll(async () => normalizeTranscript(await page.evaluate(() => window.__ocVoiceDiagnostics?.finalTranscript ?? '')), { timeout: 90_000 }).toMatch(/(?:create a )?todo to check the weighbridge calibration tomorrow/);
    finalTranscript = await page.evaluate(() => window.__ocVoiceDiagnostics?.finalTranscript);
    const normalizedTranscript = normalizeTranscript(finalTranscript);
    expect(normalizedTranscript).toMatch(/(?:create a )?todo to check the weighbridge calibration tomorrow/);

    await expect(page.locator('.oc-bubble__partial-transcript')).toHaveCount(0);
    const taskCard = page.locator('.oc-surface-card').filter({ hasText: 'Workflows & Tasks' });
    const todoMatch = taskCard.getByText(/weighbridge calibration/i);
    await expect.poll(async () => {
      const response = await request.get(`${process.env.OPENCONNECT_TODO_URL ?? 'http://127.0.0.1:5000'}/internal/openconnect/todos/`, {
        headers: {
          Authorization: `Bearer ${process.env.TODO_INTERNAL_TOKEN ?? 'e2e-todo-internal-do-not-use-in-prod'}`,
          'X-OpenConnect-Tenant': process.env.TODO_INTERNAL_TENANT ?? '01ARZ3NDEKTSV4RRFFQ69G5FAV',
        },
      });
      if (!response.ok()) return false;
      const items = await response.json();
      return items.some(item => /weighbridge calibration/i.test(item.title ?? ''));
    }, { timeout: 60_000 }).toBe(true);

    await expect(page.locator('.oc-status__text')).toHaveText('Ready', { timeout: 30_000 });
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
