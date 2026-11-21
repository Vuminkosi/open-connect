import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

test('voice client sends its short-lived ticket as a WebSocket subprotocol', async () => {
  let socket;
  class Socket {
    static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
    constructor(url, protocols) { this.url = url; this.protocols = protocols; this.readyState = 1; socket = this; }
    send() {}
  }
  class Context {
    state = 'running'; sampleRate = 24000; destination = {};
    audioWorklet = { addModule: async () => {} };
    async resume() {} async close() {}
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
  }
  class Processor {
    port = { postMessage() {} };
    constructor() { setTimeout(() => this.port.onmessage({ data: { type: 'ready' } }), 0); }
    connect() {} disconnect() {}
  }
  const window = { location: { origin: 'https://docs.example' } };
  const context = vm.createContext({ window, WebSocket: Socket, AudioContext: Context, AudioWorkletNode: Processor,
    navigator: { mediaDevices: { getUserMedia: async () => ({ getAudioTracks: () => [], getTracks: () => [] }) } },
    document: { baseURI: 'https://docs.example/' }, crypto: { randomUUID: () => 'capture' },
    URL, setTimeout, clearTimeout, Float32Array, ArrayBuffer, DataView });
  const source = await readFile(new URL('../../src/OpenConnect.Components.Blazor/wwwroot/js/composer-voice.js', import.meta.url), 'utf8');
  vm.runInContext(source, context);
  await window.ocComposerVoice.start({ invokeMethodAsync: async () => {} }, 'session', 1, null, 'one-use-ticket');
  assert.equal(socket.url, 'wss://docs.example/openconnect/v1/voice/realtime/session');
  assert.deepEqual(Array.from(socket.protocols), ['openconnect.voice.v1', 'oc-ticket.one-use-ticket']);
  await window.ocComposerVoice.stop(false);
});
