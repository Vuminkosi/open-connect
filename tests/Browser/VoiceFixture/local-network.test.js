import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isFixtureUrl } from './local-network.js';

test('HTTP and WebSocket policy permits only the two explicit loopback listeners', () => {
  for (const value of ['http://127.0.0.1:5187/', 'ws://127.0.0.1:5187/socket', 'http://127.0.0.1:5188/v1/chat/completions', 'ws://127.0.0.1:5188/internal/v1/realtime'])
    assert.equal(isFixtureUrl(value), true, value);
  for (const value of ['http://example.com:5187/', 'ws://example.com:5188/', 'wss://example.com/', 'https://127.0.0.1:5187/', 'file:///tmp/test', 'http://localhost:5187/', 'http://127.0.0.1:5000/', 'http://127.0.0.1:5187@example.com/', 'http://user:pass@127.0.0.1:5187/', 'ws://127.0.0.1:5188.evil.test/', 'invalid'])
    assert.equal(isFixtureUrl(value), false, value);
});
