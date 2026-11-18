import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const manifestPath = path.resolve(
  testDirectory,
  '../../../samples/Blazor/obj/Release/net10.0/openconnect.liveui.manifest.json',
);
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
const route = manifest.routes.find((candidate) => candidate.path === '/');
const target = route?.targets.find((candidate) => candidate.label === 'todo-list');

if (!route || !target) throw new Error('The Release manifest must include the Todo route and list region.');

function percentile(values, fraction) {
  const sorted = [...values].sort((left, right) => left - right);
  return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * fraction) - 1)];
}

function summarize(values) {
  const sorted = [...values].sort((left, right) => left - right);
  if (sorted.length === 0) return { count: 0, p50: null, p95: null, max: null };
  return {
    count: sorted.length,
    p50: sorted[Math.ceil(sorted.length * 0.5) - 1],
    p95: sorted[Math.ceil(sorted.length * 0.95) - 1],
    max: sorted.at(-1),
  };
}

test('measures target resolution and inspection on the running Blazor Todo page', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();

  const samples = await page.evaluate(async ({ manifestData, routeData, targetData }) => {
    const runtime = await import('/_content/OpenConnect.LiveUI.Blazor/liveui.js');
    const action = {
      schemaVersion: manifestData.schemaVersion,
      type: 'Inspect',
      parameters: {},
    };
    const runOnce = async () => {
      const receipt = await runtime.execute({
        manifest: manifestData,
        route: routeData,
        target: targetData,
        action,
      }, 5000, true);
      if (receipt.outcome !== 'Completed' || receipt.postconditionVerified !== true)
        throw new Error(`Inspection failed: ${JSON.stringify(receipt)}`);
      return receipt.measurements;
    };

    for (let index = 0; index < 5; index += 1) await runOnce();

    const values = [];
    const batchSize = 20;
    for (let batch = 0; batch < 50; batch += 1) {
      const startedAt = performance.now();
      const measurements = [];
      for (let index = 0; index < batchSize; index += 1) measurements.push(await runOnce());
      values.push({
        targetResolutionMilliseconds: measurements.map((item) => item.targetResolutionMilliseconds),
        browserActionMilliseconds: measurements.map((item) => item.browserActionMilliseconds),
        runtimeRoundTripMilliseconds: (performance.now() - startedAt) / batchSize,
      });
    }

    return values;
  }, { manifestData: manifest, routeData: route, targetData: target });

  expect(samples).toHaveLength(50);
  for (const sample of samples) {
    expect(Number.isFinite(sample.runtimeRoundTripMilliseconds)).toBeTruthy();
    expect(sample.targetResolutionMilliseconds).toHaveLength(20);
    expect(sample.browserActionMilliseconds).toHaveLength(20);
    expect(sample.targetResolutionMilliseconds.every(Number.isFinite)).toBeTruthy();
    expect(sample.browserActionMilliseconds.every(Number.isFinite)).toBeTruthy();
  }

  const summarize = (values) => ({
    p50: percentile(values, 0.5),
    p95: percentile(values, 0.95),
    max: Math.max(...values),
  });
  const individualResolution = samples.flatMap((sample) => sample.targetResolutionMilliseconds);
  const individualAction = samples.flatMap((sample) => sample.browserActionMilliseconds);
  console.info('[LiveUI Blazor Todo runtime benchmark]', JSON.stringify({
    environment: 'Release sample in Development, local Chromium, deterministic acceptance data',
    warmupActions: 5,
    measuredActions: samples.length * 20,
    batches: samples.length,
    actionsPerBatch: 20,
    action: 'Inspect todo-list region',
    targetResolutionMeasurementRangeMilliseconds: [Math.min(...individualResolution), Math.max(...individualResolution)],
    browserActionMeasurementRangeMilliseconds: [Math.min(...individualAction), Math.max(...individualAction)],
    runtimeRoundTripBatchAverageMilliseconds: summarize(samples.map((sample) => sample.runtimeRoundTripMilliseconds)),
    individualMeasurementResolutionNote: 'Sub-action measurements use performance.now() and are quantized at this browser timer resolution.',
  }));
});

async function measureCompletedTaskFlow(page, request, baseUrl) {
  const reset = await request.post(`${baseUrl}/__acceptance/liveui-metrics/reset`);
  expect(reset.status()).toBe(204);

  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await expect(page.getByRole('textbox', { name: /message ellai/i })).toBeVisible();

  const input = page.getByRole('textbox', { name: /message ellai/i });
  await input.fill('Which tasks are completed?');
  const startedAt = await page.evaluate(() => performance.now());
  await page.getByRole('button', { name: /send to ellai/i }).click();
  const response = page.getByRole('log', { name: 'Conversation' });
  await expect(response).toContainText('I opened the Completed tab so you can see the finished work.', { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  const endToStateMilliseconds = await page.evaluate((start) => performance.now() - start, startedAt);
  await expect.poll(async () => {
    const currentMetrics = await (await request.get(`${baseUrl}/__acceptance/liveui-metrics`)).json();
    return currentMetrics.measurements['openconnect.liveui.speech.highlight.offset']?.length ?? 0;
  }, { timeout: 20_000 }).toBeGreaterThan(0);

  const metricsResponse = await request.get(`${baseUrl}/__acceptance/liveui-metrics`);
  expect(metricsResponse.ok()).toBeTruthy();
  const snapshot = await metricsResponse.json();
  const measurements = snapshot.measurements;
  const names = {
    manifestLoad: 'openconnect.liveui.manifest.load.duration',
    targetResolution: 'openconnect.liveui.target.resolution.duration',
    targetResolutionFailures: 'openconnect.liveui.target.resolution.failures',
    browserAction: 'openconnect.liveui.browser.action.duration',
    actionExecution: 'openconnect.liveui.action.execution.duration',
    confirmedState: 'openconnect.liveui.confirmed.state.duration',
    speechHighlightOffset: 'openconnect.liveui.speech.highlight.offset',
  };
  const summarize = (values) => {
    const sorted = [...values].sort((left, right) => left - right);
    if (sorted.length === 0) return { count: 0, p50: null, p95: null, max: null };
    return {
      count: sorted.length,
      p50: sorted[Math.ceil(sorted.length * 0.5) - 1],
      p95: sorted[Math.ceil(sorted.length * 0.95) - 1],
      max: sorted.at(-1),
    };
  };
  const samples = Object.fromEntries(Object.entries(names).map(([key, name]) => [key, measurements[name] ?? []]));
  const targetFailures = samples.targetResolutionFailures.reduce((total, sample) => total + sample.value, 0);
  const targetFailureRate = samples.targetResolution.length === 0
    ? null
    : targetFailures / samples.targetResolution.length;

  for (const key of ['manifestLoad', 'targetResolution', 'browserAction', 'actionExecution', 'confirmedState', 'speechHighlightOffset']) {
    expect(samples[key].length, `${key} metric was not emitted`).toBeGreaterThan(0);
    expect(samples[key].every((sample) => Number.isFinite(sample.value) && sample.value >= 0)).toBe(true);
  }
  expect(targetFailureRate).toBe(0);
  const browserUserAgent = await page.evaluate(() => navigator.userAgent);
  expect(browserUserAgent).toContain('Chrome');

  return { endToStateMilliseconds, samples, targetFailures, targetFailureRate, snapshot, browserUserAgent };
}

test('measures the completed-task interaction through verified state and speech-linked highlights', async ({ browser, request }) => {
  const baseUrl = 'http://127.0.0.1:5100';
  const warmupPage = await browser.newPage();
  try {
    await measureCompletedTaskFlow(warmupPage, request, baseUrl);
  } finally {
    await warmupPage.close();
  }

  const iterations = [];
  for (let index = 0; index < 3; index += 1) {
    const page = await browser.newPage();
    try {
      iterations.push(await measureCompletedTaskFlow(page, request, baseUrl));
    } finally {
      await page.close();
    }
  }

  const keys = Object.keys(iterations[0].samples);
  const combinedSamples = Object.fromEntries(keys.map((key) => [
    key,
    iterations.flatMap((iteration) => iteration.samples[key]),
  ]));
  const totalTargetFailures = iterations.reduce((total, iteration) => total + iteration.targetFailures, 0);
  const totalResolutions = combinedSamples.targetResolution.length;
  const targetFailureRate = totalResolutions === 0 ? null : totalTargetFailures / totalResolutions;
  expect(targetFailureRate).toBe(0);

  const report = {
    environment: {
      framework: iterations.at(-1).snapshot.framework,
      operatingSystem: iterations.at(-1).snapshot.operatingSystem,
      browser: iterations.at(-1).browserUserAgent,
      workload: 'one completed-task user request; deterministic acceptance Todo records; sequential actions',
      warmupIterations: 1,
      measuredIterations: iterations.length,
    },
    endToVerifiedStateMilliseconds: summarize(iterations.map((iteration) => iteration.endToStateMilliseconds)),
    metrics: Object.fromEntries(Object.entries(combinedSamples).map(([key, values]) =>
      [key, summarize(values.map((sample) => sample.value))])),
    targetResolutionFailureRate: { failures: totalTargetFailures, measuredResolutions: totalResolutions, rate: targetFailureRate },
    measurementNotes: [
      'Manifest load is the .NET manifest-provider retrieval measured for each action; it excludes compile-time manifest generation and first-process resource loading.',
      'Speech/highlight offset ends when the Blazor playback-start callback returns after the matched highlight receipt; it excludes acoustic output latency and word-level alignment.',
      'This single-user acceptance workload is an initial observation, not a production capacity benchmark.',
    ],
  };
  console.info('[LiveUI Blazor Todo end-to-end benchmark]', JSON.stringify(report));
});
