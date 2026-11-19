import { test, expect } from '@playwright/test';
import { isFixtureUrl } from '../VoiceFixture/local-network.js';

const app = 'http://127.0.0.1:5187';
const provider = 'http://127.0.0.1:5188';


test('synthetic voice reaches real Todo UI and persists exactly once', async ({ context, page, request }, testInfo) => {
  const blocked = [];
  const cosmeticStubs = [];
  const errors = [];
  const finalFrames = [];
  await context.route('**/*', route => {
    if (isFixtureUrl(route.request().url())) return route.continue();
    // The real sample references this exact cosmetic font. Never contact Google.
    if (route.request().method() === 'GET' && route.request().resourceType() === 'stylesheet' &&
        route.request().url() === 'https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap') {
      cosmeticStubs.push(route.request().url());
      return route.fulfill({ status: 200, contentType: 'text/css', body: '/* Offline synthetic smoke: system font fallback. */' });
    }
    blocked.push(route.request().url());
    return route.abort('blockedbyclient');
  });
  await context.routeWebSocket(/.*/, socket => {
    if (isFixtureUrl(socket.url())) return socket.connectToServer();
    blocked.push(socket.url());
    socket.close({ code: 1008, reason: 'synthetic smoke permits exact loopback only' });
  });
  page.on('pageerror', error => errors.push(error.message));
  page.on('websocket', socket => {
    if (!socket.url().includes('/voice/realtime/')) return;
    socket.on('framereceived', frame => {
      try {
        const value = JSON.parse(typeof frame.payload === 'string' ? frame.payload : frame.payload.toString('utf8'));
        if (value.type === 'transcript-final') finalFrames.push(value);
      } catch { /* Binary PCM is not a transcript. */ }
    });
  });
  const configResponse = await request.get(`${provider}/_fixture/config`, { maxRedirects: 0 });
  expect(configResponse.ok()).toBe(true);
  const config = await configResponse.json();
  expect(config.app).toBe(app);
  expect(config.provider).toBe(provider);
  const headers = {
    Authorization: `Bearer ${config.todoToken}`,
    'X-OpenConnect-Tenant': config.tenant,
    'X-OpenConnect-Local-Guard': config.guard,
  };
  const matchingTodos = async () => {
    const response = await request.get(`${app}/internal/openconnect/todos/`, { headers, maxRedirects: 0 });
    expect(response.ok()).toBe(true);
    return (await response.json()).filter(item => item.title === config.title);
  };
  const evidence = async () => {
    const response = await request.get(`${provider}/_fixture/evidence`, { maxRedirects: 0 });
    expect(response.ok()).toBe(true);
    return response.json();
  };
  // This must fail if a reused database already satisfies the assertion.
  expect(await matchingTodos()).toHaveLength(0);
  await page.addInitScript(() => {
    window.__OC_VOICE_DIAGNOSTICS__ = true;
    window.__OC_VOICE_MAX_DURATION_MS = 6000;
    window.__OC_VOICE_SPEECH_THRESHOLD = 0.000001;
    // Deterministic PCM source, not ASR accuracy, microphone permission, or a live provider test.
    navigator.mediaDevices.getUserMedia = async constraints => {
      if (!constraints?.audio) throw new Error('Only synthetic audio is supported');
      const audio = new AudioContext();
      await audio.resume();
      const oscillator = audio.createOscillator();
      const gain = audio.createGain();
      const destination = audio.createMediaStreamDestination();
      oscillator.frequency.value = 440;
      gain.gain.value = 0.2;
      oscillator.connect(gain).connect(destination);
      oscillator.start();
      window.__syntheticAudio = { audio, oscillator };
      return destination.stream;
    };
  });
  try {
    await page.goto('/');
    await expect(page).toHaveTitle(/OpenConnect/i);
    await page.locator('.oc-anchored-fab').click();
    await expect(page.locator('section.oc-bubble')).toBeVisible();
    await page.getByRole('button', { name: 'Voice input', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__ocVoiceDiagnostics?.sentBytes ?? 0)).toBeGreaterThan(0);
    await expect.poll(() => page.evaluate(() => window.__ocVoiceDiagnostics?.finalTranscript ?? '')).toBe(config.raw);
    await expect.poll(() => finalFrames.some(frame => frame.text === config.raw && frame.authoritative === true)).toBe(true);
    await expect.poll(async () => (await matchingTodos()).length).toBe(1);
    await expect.poll(async () => (await evidence()).closedAt).toBeGreaterThan(0);
    await expect.poll(async () => (await evidence()).toolResults.length).toBeGreaterThan(0);
    // Wait for app-side processing, not just a fake model's success response.
    await expect(page.locator('.oc-status__text')).toHaveText('Ready');
    const observed = await evidence();
    expect(observed.errors).toEqual([]);
    expect(observed.receivedPcmBytes).toBeGreaterThan(0);
    expect(observed.modelRequests.length).toBeGreaterThanOrEqual(2);
    expect(observed.modelRequests[0].user).toContain(config.normalized);
    expect(observed.modelRequests[0].at).toBeLessThan(observed.closedAt);
    await page.reload();
    await expect(page.getByText(config.title, { exact: true }).first()).toBeVisible();
    expect(await matchingTodos()).toHaveLength(1);
    // Bounded duplicate observation after stream-close fallback and reload.
    await page.waitForTimeout(1000);
    expect(await matchingTodos()).toHaveLength(1);
    expect(blocked).toEqual([]);
    expect(errors).toEqual([]);
    await testInfo.attach('synthetic-voice-evidence.json', {
      body: Buffer.from(JSON.stringify({ title: config.title, raw: config.raw, normalized: config.normalized,
        matchingRecordCount: (await matchingTodos()).length,
        receivedPcmBytes: observed.receivedPcmBytes, finalAt: observed.finalAt, closedAt: observed.closedAt,
        modelRequestCount: observed.modelRequests.length, firstModelRequestAt: observed.modelRequests[0].at,
        normalizedInputMatched: observed.modelRequests[0].user.includes(config.normalized),
        authoritativeRawMatched: finalFrames.some(frame => frame.text === config.raw && frame.authoritative === true),
        toolResultCount: observed.toolResults.length, cosmeticStubCount: cosmeticStubs.length,
        blockedRequestCount: blocked.length, pageErrorCount: errors.length }, null, 2)),
      contentType: 'application/json',
    });
    await testInfo.attach('persisted-todo.png', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
  } finally {
    await page.evaluate(async () => {
      window.__syntheticAudio?.oscillator.stop();
      await window.__syntheticAudio?.audio.close();
    }).catch(() => {});
  }
});
