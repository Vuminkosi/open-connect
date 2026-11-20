// A green process exit is insufficient: Playwright can exit successfully with skipped tests.
export function classifyScenario(exitCode, report, interrupted = false) {
  if (interrupted) return 'INTERRUPTED';
  const stats = report?.stats;
  if (!stats || !Number.isInteger(stats.expected) || !Number.isInteger(stats.unexpected) ||
      !Number.isInteger(stats.skipped) || !Number.isInteger(stats.flaky)) return 'NOT_RUN';
  if (stats.unexpected > 0 || stats.flaky > 0) return 'FAIL';
  if (exitCode !== 0 || stats.expected !== 1 || stats.skipped !== 0) return 'NOT_RUN';
  return 'PASS';
}

// Each parallel fixture owns its bridge; direct worker endpoints remain unchanged.
export function workerEndpointForFixture(endpoint, port) {
  const url = new URL(endpoint);
  if (url.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) &&
      url.pathname === '/__acceptance/liveui-worker/' && !url.username && !url.password && !url.search && !url.hash) {
    url.port = String(port);
    return url.href;
  }
  return endpoint;
}
