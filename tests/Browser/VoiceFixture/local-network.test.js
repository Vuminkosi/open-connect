import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isFixtureUrl } from './local-network.js';
import { binaryFrameBytes, isSampleVoiceSocket, voiceReadiness } from './voice-readiness.js';

test('HTTP and WebSocket policy permits only the two explicit loopback listeners', () => {
  for (const value of ['http://127.0.0.1:5187/', 'ws://127.0.0.1:5187/socket', 'http://127.0.0.1:5188/v1/chat/completions', 'ws://127.0.0.1:5188/internal/v1/realtime'])
    assert.equal(isFixtureUrl(value), true, value);
  for (const value of ['http://example.com:5187/', 'ws://example.com:5188/', 'wss://example.com/', 'https://127.0.0.1:5187/', 'file:///tmp/test', 'http://localhost:5187/', 'http://127.0.0.1:5000/', 'http://127.0.0.1:5187@example.com/', 'http://user:pass@127.0.0.1:5187/', 'ws://127.0.0.1:5188.evil.test/', 'invalid'])
    assert.equal(isFixtureUrl(value), false, value);
});

test('voice wire classification counts only exact sample sockets and binary payload length', () => {
  assert.equal(isSampleVoiceSocket('ws://127.0.0.1:5187/openconnect/v1/voice/realtime/run'), true);
  for (const url of ['bad', 'http://127.0.0.1:5187/openconnect/v1/voice/realtime/run', 'ws://127.0.0.1:5188/openconnect/v1/voice/realtime/run', 'ws://example.com:5187/openconnect/v1/voice/realtime/run', 'ws://127.0.0.1:5187/openconnect/v1/voice/realtime/run/extra', 'ws://user@127.0.0.1:5187/openconnect/v1/voice/realtime/run'])
    assert.equal(isSampleVoiceSocket(url), false);
  assert.equal(binaryFrameBytes({ payload: Buffer.alloc(8) }), 8);
  assert.equal(binaryFrameBytes({ payload: 'text' }), 0);
  assert.equal(binaryFrameBytes({ payload: new Uint8Array(8) }), 0);
  assert.equal(binaryFrameBytes(null), 0);
});

test('voice readiness preserves distinct observations and rejects unlisted telemetry', () => {
  assert.deepEqual(voiceReadiness({
    client: { interop: 'called', media: 'stream-returned', diagnosticsPresent: true, processorReady: true,
      sourceFrames: 8, sentBytes: 16, stop: 'socket_error', rawError: 'do not log' },
    wire: { worklet: 'ok', voiceSockets: 1, voiceSocketErrors: 0, voiceSocketCloses: 1,
      browserBinaryFrames: 2, browserBinaryBytes: 12 },
    fixture: { upgrades: 1, acks: 1, pcmBytes: 6, rawId: 'do not log' },
  }), {
    interop: 'called', media: 'stream-returned', worklet: 'ok',
    diagnosticsPresent: true, processorReady: true, sourceFrames: 8, sentBytes: 16,
    stop: 'socket_error', voiceSockets: 1, voiceSocketErrors: 0, voiceSocketCloses: 1,
    browserBinaryFrames: 2, browserBinaryBytes: 12, fixtureUpgrades: 1, fixtureAcks: 1, fixturePcmBytes: 6,
  });
  const rejected = voiceReadiness({
    client: { interop: 'unexpected text', stop: 'unexpected text', sentBytes: 'unexpected text', sourceFrames: -1 },
    wire: { worklet: 'unexpected text', voiceSockets: 10001, browserBinaryBytes: 100000000 },
    fixture: { pcmBytes: Infinity },
  });
  assert.equal(rejected.interop, 'unknown');
  assert.equal(rejected.stop, 'unknown');
  assert.equal(rejected.sentBytes, null);
  assert.equal(rejected.sourceFrames, null);
  assert.equal(rejected.worklet, 'unknown');
  assert.equal(rejected.fixturePcmBytes, null);
  assert.equal(rejected.voiceSockets, 10000);
  assert.equal(rejected.browserBinaryBytes, 32 * 1024 * 1024);
  assert.equal(JSON.stringify(rejected).includes('unexpected text'), false);
});
