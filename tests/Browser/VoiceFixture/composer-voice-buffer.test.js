import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';

// Exercise the shipped JS with controlled capture/socket timing, without a browser or a network.
const source = readFileSync(new URL('../../../src/OpenConnect.Components.Blazor/wwwroot/js/composer-voice.js', import.meta.url), 'utf8');

async function startConnecting() {
  let socket;
  let processor;
  let trackStopped = false;
  let attached;
  const workletAttached = new Promise(resolve => { attached = resolve; });

  class FakeSocket {
    static OPEN = 1;
    readyState = 0;
    bufferedAmount = 0;
    messages = [];
    listeners = new Map();
    constructor() { socket = this; }
    addEventListener(name, callback) { this.listeners.set(name, callback); }
    send(payload) {
      assert.equal(this.readyState, FakeSocket.OPEN);
      this.messages.push(typeof payload === 'string'
        ? JSON.parse(payload).type
        : new DataView(payload).getInt16(0, true));
    }
    open() {
      this.readyState = FakeSocket.OPEN;
      this.onopen?.();
      this.listeners.get('open')?.();
    }
  }
  class FakeContext {
    state = 'running';
    sampleRate = 24000;
    audioWorklet = { addModule: async () => {} };
    async resume() {}
    async close() {}
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
  }
  class FakeWorklet {
    port = { postMessage() {} };
    constructor() { processor = this; }
    connect() { attached(); }
    disconnect() {}
    emit(data) { this.port.onmessage({ data }); }
  }
  const track = { stop: () => { trackStopped = true; } };
  const stream = { getAudioTracks: () => [track], getTracks: () => [track] };
  const env = {
    AudioContext: FakeContext, AudioWorkletNode: FakeWorklet, WebSocket: FakeSocket,
    URL, Float32Array, document: { baseURI: 'http://127.0.0.1:5187/' },
    location: { origin: 'http://127.0.0.1:5187' },
    crypto: { randomUUID: () => 'synthetic-capture' },
    navigator: { mediaDevices: { getUserMedia: async () => stream } },
    // Socket resolution is driven explicitly, never by process timers.
    setTimeout: () => 0, clearTimeout: () => {},
  };
  env.window = env;
  runInNewContext(source, env);
  const voice = env.ocComposerVoice;
  const started = voice.start({ invokeMethodAsync: async () => {} }, 'synthetic-session', 1);
  await workletAttached;
  processor.emit({ type: 'ready' });
  await Promise.resolve();

  return {
    socket, started, voice, trackStopped: () => trackStopped,
    pcm: ordinal => processor.emit(new Float32Array([-ordinal / 32768])),
  };
}

test('delayed voice socket sends only the first 256 queued PCM frames in order after its open control', { timeout: 1000 }, async () => {
  const capture = await startConnecting();
  const queued = Array.from({ length: 256 }, (_, index) => -index - 1);
  for (let ordinal = 1; ordinal <= 260; ordinal++) capture.pcm(ordinal);
  assert.deepEqual(capture.socket.messages, []);

  capture.socket.open();
  await capture.started;
  assert.deepEqual(capture.socket.messages, ['open', ...queued]);

  capture.pcm(261);
  await capture.voice.stop(false);
  capture.pcm(262);
  assert.deepEqual(capture.socket.messages, ['open', ...queued, -261, 'endStream']);
  assert.equal(capture.trackStopped(), true);
});

test('stopping while the voice socket is still connecting does not later transmit queued PCM', { timeout: 1000 }, async () => {
  const capture = await startConnecting();
  capture.pcm(1);
  await capture.voice.stop(false);
  capture.socket.open();
  await capture.started;
  assert.deepEqual(capture.socket.messages, []);
  assert.equal(capture.trackStopped(), true);
});
