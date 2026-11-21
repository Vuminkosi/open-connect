import { test, expect } from '@playwright/test';
import path from 'node:path';

for (const scenario of ['unexpected', 'completed', 'during startup']) {
test(`${scenario} voice socket close stops microphone capture`, async ({ page }) => {
  await page.route('https://voice-test.invalid/', route => route.fulfill({ status: 200, body: '<html></html>' }));
  await page.goto('https://voice-test.invalid/');
  await page.evaluate(scenario => {
    window.__voiceTest = { stopped: false, contextClosed: false, calls: [] };
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: {
      getUserMedia: async () => ({ id: 'stream', getAudioTracks: () => [],
        getTracks: () => [{ stop: () => { window.__voiceTest.stopped = true; } }] })
    } });
    window.WebSocket = class {
      static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
      constructor() { this.readyState = 1; window.__voiceTest.socket = this; }
      send() {}
    };
    window.AudioContext = class {
      state = 'running'; sampleRate = 24000; destination = {};
      audioWorklet = { addModule: async () => {} };
      async resume() {};
      async close() { window.__voiceTest.contextClosed = true; }
      createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
    };
    window.AudioWorkletNode = class {
      port = { postMessage() {} };
      constructor() { setTimeout(() => {
        if (scenario === 'during startup') {
          window.__voiceTest.socket.readyState = WebSocket.CLOSED;
          window.__voiceTest.socket.onclose({ code: 1013 });
        } else this.port.onmessage({ data: { type: 'ready' } });
      }, 0); }
      connect() {} disconnect() {}
    };
  }, scenario);
  await page.addScriptTag({ path: path.resolve('../../src/OpenConnect.Components.Blazor/wwwroot/js/composer-voice.js') });
  await page.evaluate(async scenario => {
    const dotnet = { invokeMethodAsync: async (...args) => { window.__voiceTest.calls.push(args); } };
    try { await window.ocComposerVoice.start(dotnet, 'session', 1); }
    catch (error) { if (scenario === 'during startup') return; throw error; }
    if (scenario === 'completed') await window.__voiceTest.socket.onmessage({ data: JSON.stringify({
      type: 'transcript-final', authoritative: true, text: 'example'
    }) });
    window.__voiceTest.socket.readyState = WebSocket.CLOSED;
    await window.__voiceTest.socket.onclose({ code: scenario === 'completed' ? 1000 : 1013 });
  }, scenario);
  expect(await page.evaluate(() => window.__voiceTest.stopped)).toBe(true);
  expect(await page.evaluate(() => window.__voiceTest.contextClosed)).toBe(true);
  await expect.poll(() => page.evaluate(expected => window.__voiceTest.calls.some(call =>
    call[0] === expected), scenario === 'completed' ? 'OnVoiceStopped' : 'OnVoiceError')).toBe(true);
});
}
