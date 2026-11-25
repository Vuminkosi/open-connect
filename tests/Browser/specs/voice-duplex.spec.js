import { test, expect } from '@playwright/test';
import path from 'node:path';

const COMPOSER_VOICE_PATH = path.resolve('../../src/OpenConnect.Components.Blazor/wwwroot/js/composer-voice.js');
const PLAYBACK_WORKLET_PATH = path.resolve('../../src/OpenConnect.Components.Blazor/wwwroot/js/voice-playback-worklet.js');

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

test('LV-02 & LV-28: persistent mic across 5 consecutive utterances with 1 getUserMedia call and clean stopSession disposal', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-multi', 1, null, 'ticket-v2');

    // Simulate 5 consecutive utterances separated by endUtterance() without tearing down mic/context/socket
    const captureIds = [window.__ocVoiceDiagnostics.captureId];
    for (let turn = 0; turn < 5; turn++) {
      await window.ocComposerVoice.endUtterance();
      captureIds.push(window.__ocVoiceDiagnostics.captureId);
    }

    const midSession = {
      getUserMediaCalls: window.__duplexTest.getUserMediaCalls,
      tracksStopped: window.__duplexTest.tracksStopped,
      contextClosed: window.__duplexTest.contextClosed,
      socketClosed: window.__duplexTest.socketClosed,
      utteranceCount: window.__ocVoiceDiagnostics.utteranceCount,
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
  expect(summary.midSession.utteranceCount).toBe(5);
  expect(summary.midSession.speechEndedCount).toBe(5);
  expect(summary.midSession.uniqueCaptureIds).toBe(6);

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

test('LV-07: interrupt before first audio chunk suppresses late obsolete epoch frames', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-lv07', 1, null, 'ticket-v2');
    const ws = window.__duplexTest.socket;
    const { encodeV2Frame } = window.__ocVoiceTestHooks;

    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'session.ready', voiceConnectionId: 'vc-lv07', speechEpoch: 2 }) });
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.started', responseId: 'resp-early', speechEpoch: 2 }) });

    // Interrupt BEFORE any TTS binary frame has arrived
    const interrupt = await window.ocComposerVoice.interruptPlayback('barge_in');

    // Late-arriving obsolete epoch 2 binary frames arrive after local suppression
    const pcm = new Int16Array([900, -900, 450, -450]).buffer;
    await ws.onmessage({ data: encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 2, sequence: 1, sampleOffset: 0 }, pcm) });
    await ws.onmessage({ data: encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 2, sequence: 2, sampleOffset: 4 }, pcm) });

    const beforeNextEpoch = {
      receivedAudioFrames: window.__ocVoiceDiagnostics.receivedAudioFrames,
      staleFramesRejected: window.__ocVoiceDiagnostics.staleFramesRejected,
      pushedCount: window.__duplexTest.playbackMessages.filter(m => m.type === 'push').length,
      isPlaying: window.__ocVoiceTestHooks.getDuplexState().isPlaying
    };

    // Server acknowledges cancellation and starts epoch 3
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.cancelled', responseId: 'resp-early', speechEpoch: 3, reason: 'barge_in' }) });
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.started', responseId: 'resp-next', speechEpoch: 3 }) });
    await ws.onmessage({ data: encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 3, sequence: 1, sampleOffset: 0 }, pcm) });

    const interruptMsgs = window.__duplexTest.sentJson.filter(m => m.type === 'response.interrupt');
    const pushedEpochs = window.__duplexTest.playbackMessages.filter(m => m.type === 'push').map(m => m.speechEpoch);

    await window.ocComposerVoice.stopSession(false, 'done');

    return {
      interrupt,
      beforeNextEpoch,
      pushedEpochs,
      interruptMsg: interruptMsgs[0]
    };
  });

  expect(summary.beforeNextEpoch.receivedAudioFrames).toBe(0);
  expect(summary.beforeNextEpoch.pushedCount).toBe(0);
  expect(summary.beforeNextEpoch.staleFramesRejected).toBe(2);
  expect(summary.beforeNextEpoch.isPlaying).toBe(false);
  expect(summary.pushedEpochs).toEqual([3]);
  expect(summary.interruptMsg.responseId).toBe('resp-early');
  expect(summary.interruptMsg.speechEpoch).toBe(2);
  expect(summary.interruptMsg.playedSamples).toBe(0);
});

test('LV-10: playback echo debounce ignores transient spike and triggers immediate local stop on sustained speech', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    window.__OC_VOICE_BARGE_IN_DEBOUNCE_MS = 120;
    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-lv10', 1, null, 'ticket-v2');
    const ws = window.__duplexTest.socket;
    const capture = window.__duplexTest.captureNode;
    const { encodeV2Frame } = window.__ocVoiceTestHooks;

    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'session.ready', voiceConnectionId: 'vc-lv10', speechEpoch: 2 }) });
    await ws.onmessage({ data: JSON.stringify({ version: 2, type: 'response.started', responseId: 'resp-echo', speechEpoch: 2 }) });

    // Start playback so current.isPlaying === true
    const pcm = new Int16Array([1200, -1200, 600, -600]).buffer;
    await ws.onmessage({ data: encodeV2Frame({ streamKind: 2, streamId: 1, speechEpoch: 2, sequence: 1, sampleOffset: 0 }, pcm) });

    const loudFrame = new Float32Array(256).fill(0.25);
    const silentFrame = new Float32Array(256).fill(0.0);

    // 1. Short transient echo spike below 120ms debounce window followed by silence
    const originalNow = Date.now;
    let fakeNow = 10000;
    Date.now = () => fakeNow;
    try {
      capture.port.onmessage({ data: loudFrame });
      fakeNow += 50; // 50ms < 120ms debounce
      capture.port.onmessage({ data: loudFrame });
      fakeNow += 20;
      capture.port.onmessage({ data: silentFrame }); // echo dies out

      const afterTransientEcho = {
        isPlaying: window.__ocVoiceTestHooks.getDuplexState().isPlaying,
        interruptCount: window.__duplexTest.sentJson.filter(m => m.type === 'response.interrupt').length,
        speechStartedCount: window.__duplexTest.sentJson.filter(m => m.type === 'speech.started').length,
        clearCount: window.__duplexTest.playbackMessages.filter(m => m.type === 'clear').length
      };

      // 2. Sustained user speech exceeding 120ms debounce window
      fakeNow += 100;
      capture.port.onmessage({ data: loudFrame });
      fakeNow += 60;
      capture.port.onmessage({ data: loudFrame });
      fakeNow += 70; // 130ms >= 120ms debounce
      capture.port.onmessage({ data: loudFrame });

      const afterSustainedSpeech = {
        isPlaying: window.__ocVoiceTestHooks.getDuplexState().isPlaying,
        interruptMsgs: window.__duplexTest.sentJson.filter(m => m.type === 'response.interrupt'),
        speechStartedCount: window.__duplexTest.sentJson.filter(m => m.type === 'speech.started').length,
        clearCount: window.__duplexTest.playbackMessages.filter(m => m.type === 'clear').length
      };

      await window.ocComposerVoice.stopSession(false, 'done');
      return { afterTransientEcho, afterSustainedSpeech };
    } finally {
      Date.now = originalNow;
    }
  });

  expect(summary.afterTransientEcho.isPlaying).toBe(true);
  expect(summary.afterTransientEcho.interruptCount).toBe(0);
  expect(summary.afterTransientEcho.speechStartedCount).toBe(0);
  expect(summary.afterTransientEcho.clearCount).toBe(0);

  expect(summary.afterSustainedSpeech.isPlaying).toBe(false);
  expect(summary.afterSustainedSpeech.clearCount).toBeGreaterThanOrEqual(1);
  expect(summary.afterSustainedSpeech.speechStartedCount).toBe(1);
  expect(summary.afterSustainedSpeech.interruptMsgs).toHaveLength(1);
  expect(summary.afterSustainedSpeech.interruptMsgs[0].reason).toBe('barge_in');
  expect(summary.afterSustainedSpeech.interruptMsgs[0].responseId).toBe('resp-echo');
});

test('LV-26: playback worklet bounded ring buffer drops overflow safely and accounts for underruns', async ({ page }) => {
  await page.route('https://worklet-test.invalid/', route =>
    route.fulfill({ status: 200, body: '<html><body></body></html>' })
  );
  await page.goto('https://worklet-test.invalid/');

  await page.evaluate(() => {
    window.sampleRate = 24000;
    window.__workletRegistry = {};
    window.AudioWorkletProcessor = class {
      constructor() {
        this._messages = [];
        this.port = {
          onmessage: null,
          postMessage: (msg) => { this._messages.push(msg); }
        };
      }
    };
    window.registerProcessor = (name, ctor) => {
      window.__workletRegistry[name] = ctor;
    };
  });

  await page.addScriptTag({ path: PLAYBACK_WORKLET_PATH });

  const result = await page.evaluate(() => {
    const Processor = window.__workletRegistry['openconnect-pcm-playback'];
    const proc = new Processor({
      processorOptions: {
        sourceSampleRate: 24000,
        maxBufferMs: 100, // 2400 samples capacity at 24 kHz
        initialBufferMs: 10, // 240 samples
        receiptIntervalMs: 40,
        fadeOutMs: 5
      }
    });

    proc.port.onmessage({ data: { type: 'reset', speechEpoch: 4, responseId: 'resp-lv26' } });

    // Push 3600 samples into a 2400-sample bounded ring buffer -> 1200 overflow samples must be dropped safely
    const overflowPcm = new Int16Array(3600);
    overflowPcm.fill(8192);
    proc.port.onmessage({ data: { type: 'push', pcm16: overflowPcm.buffer, sampleRate: 24000, speechEpoch: 4 } });

    proc.port.onmessage({ data: { type: 'stats' } });
    const afterPushStats = proc._messages.filter(m => m.type === 'receipt').at(-1);

    // Render 22 blocks of 128 samples (2816 samples > 2400 buffered) without calling drain -> triggers underruns
    const out = [new Float32Array(128)];
    for (let i = 0; i < 22; i++) {
      proc.process([], [out]);
    }
    const lastBlockAllZero = out[0].every(sample => sample === 0);

    proc.port.onmessage({ data: { type: 'stats' } });
    const afterUnderrunStats = proc._messages.filter(m => m.type === 'receipt').at(-1);

    return {
      readyMsg: proc._messages[0],
      afterPushStats,
      afterUnderrunStats,
      lastBlockAllZero
    };
  });

  expect(result.readyMsg).toMatchObject({ type: 'ready', sampleRate: 24000, capacity: 2400 });
  expect(result.afterPushStats.bufferedSamples).toBe(2400);
  expect(result.afterPushStats.droppedSamples).toBe(1200);
  expect(result.afterUnderrunStats.bufferedSamples).toBe(0);
  expect(result.afterUnderrunStats.playedSamples).toBe(2400);
  expect(result.afterUnderrunStats.underruns).toBeGreaterThanOrEqual(3);
  expect(result.afterUnderrunStats.droppedSamples).toBe(1200);
  expect(result.lastBlockAllZero).toBe(true);
});

test('LV-28: stopSession cleanly stops all mic tracks, closes WebSocket, and closes AudioContext', async ({ page }) => {
  await setupDuplexHarness(page);

  const summary = await page.evaluate(async () => {
    let extraTrackStopped = false;
    let captureDisconnected = false;
    let playbackDisconnected = false;
    let sourceDisconnected = false;

    const origGetUserMedia = navigator.mediaDevices.getUserMedia;
    navigator.mediaDevices.getUserMedia = async () => {
      const base = await origGetUserMedia();
      const baseTracks = base.getTracks();
      const extraTrack = {
        enabled: true,
        readyState: 'live',
        stop() {
          this.readyState = 'ended';
          extraTrackStopped = true;
          window.__duplexTest.tracksStopped++;
        }
      };
      const allTracks = [...baseTracks, extraTrack];
      return {
        id: 'duplex-multi-track-stream',
        getAudioTracks: () => allTracks,
        getTracks: () => allTracks
      };
    };

    const dotnet = { invokeMethodAsync: async (...args) => { window.__duplexTest.dotnetCalls.push(args); } };
    await window.ocComposerVoice.startDuplex(dotnet, 'sess-lv28', 1, null, 'ticket-v2');

    const duplex = window.__ocVoiceTestHooks.getDuplexState();
    const origCapDisc = duplex.captureNode.disconnect.bind(duplex.captureNode);
    duplex.captureNode.disconnect = () => { captureDisconnected = true; origCapDisc(); };
    const origPbDisc = duplex.playback.node.disconnect.bind(duplex.playback.node);
    duplex.playback.node.disconnect = () => { playbackDisconnected = true; origPbDisc(); };
    const origSrcDisc = duplex.source.disconnect.bind(duplex.source);
    duplex.source.disconnect = () => { sourceDisconnected = true; origSrcDisc(); };

    await window.ocComposerVoice.stopSession(true, 'user_exit');

    return {
      tracksStopped: window.__duplexTest.tracksStopped,
      extraTrackStopped,
      socketClosed: window.__duplexTest.socketClosed,
      contextClosed: window.__duplexTest.contextClosed,
      captureDisconnected,
      playbackDisconnected,
      sourceDisconnected,
      duplexStateAfterStop: window.__ocVoiceTestHooks.getDuplexState(),
      closeEvent: window.__duplexTest.sentJson.find(m => m.type === 'session.close'),
      stoppedCallback: window.__duplexTest.dotnetCalls.some(c => c[0] === 'OnDuplexVoiceStopped')
    };
  });

  expect(summary.tracksStopped).toBe(2);
  expect(summary.extraTrackStopped).toBe(true);
  expect(summary.socketClosed).toBe(true);
  expect(summary.contextClosed).toBe(true);
  expect(summary.captureDisconnected).toBe(true);
  expect(summary.playbackDisconnected).toBe(true);
  expect(summary.sourceDisconnected).toBe(true);
  expect(summary.duplexStateAfterStop).toBeNull();
  expect(summary.closeEvent).toMatchObject({ type: 'session.close', reason: 'user_exit' });
  expect(summary.stoppedCallback).toBe(true);
});
