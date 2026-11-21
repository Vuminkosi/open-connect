import { isFixtureUrl } from './local-network.js';

export const count = (value, max = 10000) =>
  Number.isSafeInteger(value) && value >= 0 ? Math.min(value, max) : null;

export function isSampleVoiceSocket(value) {
  if (!isFixtureUrl(value)) return false;
  const url = new URL(value);
  return url.protocol === 'ws:' && url.port === '5187' &&
    /^\/openconnect\/v1\/voice\/realtime\/[^/]+$/.test(url.pathname);
}

// Playwright 1.65 maps WebSocket opcode 2 to Buffer. Never keep the payload.
export const binaryFrameBytes = frame => Buffer.isBuffer(frame?.payload) ? frame.payload.length : 0;

const member = (value, allowed) => allowed.includes(value) ? value : 'unknown';
const scalarBytes = value => count(value, 32 * 1024 * 1024);

export function voiceReadiness({ client, wire, fixture }) {
  return {
    interop: member(client?.interop, ['missing', 'available', 'called', 'resolved', 'rejected']),
    media: member(client?.media, ['idle', 'entered', 'resume-pending', 'stream-returned', 'rejected']),
    worklet: member(wire?.worklet, ['idle', 'requested', 'ok', 'failed']),
    diagnosticsPresent: client?.diagnosticsPresent === true,
    processorReady: client?.processorReady === true,
    sourceFrames: count(client?.sourceFrames, 1000000),
    sentBytes: scalarBytes(client?.sentBytes),
    stop: member(client?.stop, [null, 'stopped', 'restarted', 'max-duration', 'silence', 'socket_error']),
    voiceSockets: count(wire?.voiceSockets),
    voiceSocketErrors: count(wire?.voiceSocketErrors),
    voiceSocketCloses: count(wire?.voiceSocketCloses),
    browserBinaryFrames: count(wire?.browserBinaryFrames),
    browserBinaryBytes: scalarBytes(wire?.browserBinaryBytes),
    fixtureUpgrades: count(fixture?.upgrades),
    fixtureAcks: count(fixture?.acks),
    fixturePcmBytes: scalarBytes(fixture?.pcmBytes),
  };
}
