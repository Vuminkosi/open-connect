import { test, expect } from '@playwright/test';
import path from 'node:path';

const COMPOSER_VOICE_PATH = path.resolve('../../src/OpenConnect.Components.Blazor/wwwroot/js/composer-voice.js');

async function setupDuplexHarness(page) {
  await page.route('https://voice-duplex-test.invalid/', route =>
    route.fulfill({ status: 200, body: '<html><body></body></html>' })
  );
  await page.goto('https://voice-duplex-test.invalid/');
  await page.evaluate(() => {
    window.__OC_VOICE_DISABLE_SILERO__ = true;
    window.__duplexTest = {
      getUserMediaCalls: 0,
      tracksStopped: 0,
      contextClosed: false,
      socketClosed: false,
      protocols: null,
      sentJson: [],
      sentBinary: [],
      playbackMessages: [],
      dotnetCalls: [],
      socket: null,
      captureNode: null,
      playbackNode: null
    };

    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        getUserMedia: async () => {
          window.__duplexTest.getUserMediaCalls++;
          const track = {
            enabled: true,
            readyState: 'live',
            stop() {
              this.readyState = 'ended';
              window.__duplexTest.tracksStopped++;
            }
          };
          return {
            id: 'duplex-stream-1',
            getAudioTracks: () => [track],
            getTracks: () => [track]
          };
        }
      }
    });

    window.WebSocket = class {
      static OPEN = 1;
      static CLOSING = 2;
      static CLOSED = 3;
      constructor(url, protocols) {
        this.url = url;
        this.protocols = protocols;
        this.readyState = 1;
        this.bufferedAmount = 0;
        window.__duplexTest.socket = this;
        window.__duplexTest.protocols = protocols;
      }
      send(data) {
        if (typeof data === 'string') {
          window.__duplexTest.sentJson.push(JSON.parse(data));
        } else {
          window.__duplexTest.sentBinary.push(data);
        }
      }
      close() {
        this.readyState = 3;
        window.__duplexTest.socketClosed = true;
      }
    };

    window.AudioContext = class {
      state = 'running';
      sampleRate = 24000;
      destination = {};
      audioWorklet = { addModule: async () => {} };
      async resume() {}
      async close() {
        this.state = 'closed';
        window.__duplexTest.contextClosed = true;
      }
      createMediaStreamSource() {
        return { connect() {}, disconnect() {} };
      }
    };

    window.AudioWorkletNode = class {
      constructor(_ctx, name) {
        this.name = name;
        this.port = {
          onmessage: null,
          postMessage: (msg) => {
            if (name === 'openconnect-pcm-playback') {
              window.__duplexTest.playbackMessages.push(msg);
            }
          }
        };
        if (name === 'openconnect-pcm-capture') {
          window.__duplexTest.captureNode = this;
          setTimeout(() => this.port.onmessage?.({ data: { type: 'ready' } }), 0);
        } else if (name === 'openconnect-pcm-playback') {
          window.__duplexTest.playbackNode = this;
        }
      }
      connect() {}
      disconnect() {}
    };
  });

  await page.addScriptTag({ path: COMPOSER_VOICE_PATH });
}

test('v2 32-byte binary frame encode/decode roundtrip and malformed header rejection', async ({ page }) => {
  await setupDuplexHarness(page);

  const result = await page.evaluate(() => {
    const { encodeV2Frame, decodeV2Frame } = window.__ocVoiceTestHooks;

    const pcm = new Int16Array([0, 1200, -1200, 32767, -32768, 42]);
    const micFrame = encodeV2Frame({
      streamKind: 1,
      streamId: 7,
      speechEpoch: 4,
      sequence: 19,
      sampleOffset: 48000
    }, pcm.buffer);

    const decodedMic = decodeV2Frame(micFrame);

    const ttsFrame = encodeV2Frame({
      streamKind: 2,
      streamId: 2,
      speechEpoch: 5,
      sequence: 100,
      sampleOffset: 9600
    }, pcm.buffer);
    const decodedTts = decodeV2Frame(ttsFrame);

    // Malformed cases
    const badMagic = new Uint8Array(micFrame.slice(0));
    badMagic[0] = 0x00;

    const badVersion = new Uint8Array(micFrame.slice(0));
    badVersion[2] = 1;

    const badKind = new Uint8Array(micFrame.slice(0));
    badKind[3] = 9;

    const oddPayload = new Uint8Array(35);
    oddPayload[0] = 0x4f; oddPayload[1] = 0x43; oddPayload[2] = 2; oddPayload[3] = 2;

    const truncated = new Uint8Array(16);
    const oversized = new Uint8Array(96 * 1024 + 2);
    oversized[0] = 0x4f; oversized[1] = 0x43; oversized[2] = 2; oversized[3] = 2;

    const negativeEpoch = new Uint8Array(micFrame.slice(0));
    new DataView(negativeEpoch.buffer).setBigInt64(8, -1n, true);

    return {
      micFrameByteLength: micFrame.byteLength,
      decodedMicHeader: decodedMic?.header,
      decodedMicSamples: Array.from(new Int16Array(decodedMic.payload)),
      decodedTtsHeader: decodedTts?.header,
      badMagic: decodeV2Frame(badMagic),
      badVersion: decodeV2Frame(badVersion),
      badKind: decodeV2Frame(badKind),
      oddPayload: decodeV2Frame(oddPayload),
      truncated: decodeV2Frame(truncated),
      oversized: decodeV2Frame(oversized),
      negativeEpoch: decodeV2Frame(negativeEpoch),
      encodeOddRejected: encodeV2Frame({ streamKind: 1, streamId: 1, speechEpoch: 1, sequence: 1, sampleOffset: 0 }, new Uint8Array(3))
    };
  });

  expect(result.micFrameByteLength).toBe(32 + 12);
  expect(result.decodedMicHeader).toEqual({
    streamKind: 1,
    streamId: 7,
    speechEpoch: 4,
    sequence: 19,
    sampleOffset: 48000
  });
  expect(result.decodedMicSamples).toEqual([0, 1200, -1200, 32767, -32768, 42]);
  expect(result.decodedTtsHeader).toEqual({
    streamKind: 2,
    streamId: 2,
    speechEpoch: 5,
    sequence: 100,
    sampleOffset: 9600
  });
  expect(result.badMagic).toBeNull();
  expect(result.badVersion).toBeNull();
  expect(result.badKind).toBeNull();
  expect(result.oddPayload).toBeNull();
  expect(result.truncated).toBeNull();
  expect(result.oversized).toBeNull();
  expect(result.negativeEpoch).toBeNull();
  expect(result.encodeOddRejected).toBeNull();
});

test('persistent mic across multiple utterances with endUtterance vs stopSession', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-multi', 1, null, 'ticket-v2');

    // Simulate 3 utterances separated by endUtterance() without tearing down mic/context/socket
    const captureIds = [window.__ocVoiceDiagnostics.captureId];
    await window.ocComposerVoice.endUtterance();
    captureIds.push(window.__ocVoiceDiagnostics.captureId);
    await window.ocComposerVoice.endUtterance();
    captureIds.push(window.__ocVoiceDiagnostics.captureId);

    const midSession = {
      getUserMediaCalls: window.__duplexTest.getUserMediaCalls,
      tracksStopped: window.__duplexTest.tracksStopped,
      contextClosed: window.__duplexTest.contextClosed,
      socketClosed: window.__duplexTest.socketClosed,
      speechEndedCount: window.__duplexTest.sentJson.filter(m => m.type === 'speech.ended').length,
      uniqueCaptureIds: new Set(captureIds).size
    };

    await window.ocComposerVoice.stopSession(true, 'done');

    return {
      protocols: window.__duplexTest.protocols,
      midSession,
      afterStop: {
        tracksStopped: window.__duplexTest.tracksStopped,
        contextClosed: window.__duplexTest.contextClosed,
        socketClosed: window.__duplexTest.socketClosed,
        sessionCloseSent: window.__duplexTest.sentJson.some(m => m.type === 'session.close'),
        stoppedCallbackInvoked: window.__duplexTest.dotnetCalls.some(c => c[0] === 'OnDuplexVoiceStopped')
      }
    };
  });

  expect(summary.protocols).toEqual(['openconnect.voice.v2', 'oc-ticket.ticket-v2']);
  expect(summary.midSession.getUserMediaCalls).toBe(1);
  expect(summary.midSession.tracksStopped).toBe(0);
  expect(summary.midSession.contextClosed).toBe(false);
  expect(summary.midSession.socketClosed).toBe(false);
  expect(summary.midSession.speechEndedCount).toBe(2);
  expect(summary.midSession.uniqueCaptureIds).toBe(3);

  expect(summary.afterStop.tracksStopped).toBe(1);
  expect(summary.afterStop.contextClosed).toBe(true);
  expect(summary.afterStop.socketClosed).toBe(true);
  expect(summary.afterStop.sessionCloseSent).toBe(true);
  expect(summary.afterStop.stoppedCallbackInvoked).toBe(true);
});

test('immediate local barge-in playback clear and stale-epoch frame rejection', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-barge', 1, null, 'ticket-v2');
    const ws = window.__duplexTest.socket;
    const { encodeV2Frame } = window.__ocVoiceTestHooks;

    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'session.ready', voiceConnectionId: 'vc-1', speechEpoch: 2 }) });
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.started', responseId: 'resp-2', speechEpoch: 2 }) });

    // Deliver a valid epoch 2 TTS audio frame
    const pcm = new Int16Array([1000, -1000, 500, -500]).buffer;
    const epoch2Frame = encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 2, sequence: 1, sampleOffset: 0 }, pcm);
    await ws.onmessage({ data: epoch2Frame });

    // Simulate playback worklet reporting 480 rendered samples
    window.__duplexTest.playbackNode.port.onmessage({
      data: { type: 'receipt', playedSamples: 480, underruns: 0, speechEpoch: 2, responseId: 'resp-2' }
    });

    // Trigger immediate local barge-in interruption
    const interrupt = await window.ocComposerVoice.interruptPlayback('barge_in');

    // Now deliver a stale epoch 2 TTS frame after local suppression (before server response.cancelled)
    const staleEpoch2Frame = encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 2, sequence: 2, sampleOffset: 4 }, pcm);
    await ws.onmessage({ data: staleEpoch2Frame });

    // Server acknowledges cancellation and advances to epoch 3
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.cancelled', responseId: 'resp-2', speechEpoch: 3, reason: 'barge_in' }) });
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.started', responseId: 'resp-3', speechEpoch: 3 }) });

    // Epoch 3 TTS frame is accepted
    const epoch3Frame = encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 3, sequence: 1, sampleOffset: 0 }, pcm);
    await ws.onmessage({ data: epoch3Frame });

    const playbackPushes = window.__duplexTest.playbackMessages.filter(m => m.type === 'push');
    const playbackClears = window.__duplexTest.playbackMessages.filter(m => m.type === 'clear');
    const interruptMsgs = window.__duplexTest.sentJson.filter(m => m.type === 'response.interrupt');

    await window.ocComposerVoice.stopSession(false, 'done');

    return {
      interrupt,
      playbackPushEpochs: playbackPushes.map(m => m.speechEpoch),
      clearCount: playbackClears.length,
      staleFramesRejected: window.__ocVoiceDiagnostics.staleFramesRejected,
      interruptMsg: interruptMsgs[0]
    };
  });

  expect(summary.clearCount).toBeGreaterThanOrEqual(1);
  expect(summary.staleFramesRejected).toBe(1);
  expect(summary.playbackPushEpochs).toEqual([2, 3]);
  expect(summary.interruptMsg.type).toBe('response.interrupt');
  expect(summary.interruptMsg.responseId).toBe('resp-2');
  expect(summary.interruptMsg.speechEpoch).toBe(2);
  expect(summary.interruptMsg.playedSamples).toBe(480);
  expect(summary.interruptMsg.requestId).toBeTruthy();
});
