import { expect, test } from '@playwright/test';

test('MiniComposer audio player reports real playback progress and completion without narration text', async ({ page }) => {
  await page.goto('/');
  await page.mouse.click(20, 20);
  await page.addScriptTag({ url: '/_content/OpenConnect.Components.Blazor/js/composer-audio.js' });

  const events = await page.evaluate(async () => {
    const eventLog = [];
    const dotnet = {
      async invokeMethodAsync(method, ...args) {
        eventLog.push({ method, args });
      },
    };
    const sampleCount = 14_400;
    const pcm = new Uint8Array(sampleCount * 2);
    let bytes = '';
    for (const byte of pcm) bytes += String.fromCharCode(byte);
    await window.ocComposerAudio.playPcm(btoa(bytes), 24_000, 1, 42, dotnet, 12, 48);
    return eventLog;
  });

  expect(events.map((event) => event.method)).toContain('OnSpeechPlaybackStarted');
  expect(events.map((event) => event.method)).toContain('OnSpeechPlaybackSynchronizationMeasured');
  expect(events.map((event) => event.method)).toContain('OnSpeechPlaybackProgress');
  expect(events.map((event) => event.method)).toContain('OnSpeechPlaybackEnded');
  expect(events[0]).toMatchObject({ method: 'OnSpeechPlaybackStarted', args: [42, 12, 48, expect.any(Number)] });
  const synchronization = events.find((event) => event.method === 'OnSpeechPlaybackSynchronizationMeasured');
  expect(synchronization.args).toEqual([42, 12, 48, expect.any(Number)]);
  expect(synchronization.args[3]).toBeGreaterThanOrEqual(0);
  const ended = events.find((event) => event.method === 'OnSpeechPlaybackEnded');
  expect(ended.args).toEqual([42, 12, 48, expect.any(Number)]);
  expect(events.every((event) => !JSON.stringify(event).includes('narration'))).toBe(true);
});
