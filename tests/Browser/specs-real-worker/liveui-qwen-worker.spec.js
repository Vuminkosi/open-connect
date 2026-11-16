import { expect, test } from '@playwright/test';

test('connected Blazor Server answers which tasks are completed through the private worker while narrating the visible rows', async ({ page, request }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Task Management' })).toBeVisible();
  await page.locator('.oc-anchored-fab').click();
  await expect(page.locator('section.oc-bubble')).toBeVisible();
  await page.evaluate(() => {
    window.__liveuiHighlightTimeline = [];
    window.__speechPlaybackTimeline = [];
    const playPcm = window.ocComposerAudio.playPcm;
    let endedEventPrototype = AudioBufferSourceNode.prototype;
    let endedEventDescriptor;
    while (endedEventPrototype && !endedEventDescriptor) {
      endedEventDescriptor = Object.getOwnPropertyDescriptor(endedEventPrototype, 'onended');
      if (!endedEventDescriptor) endedEventPrototype = Object.getPrototypeOf(endedEventPrototype);
    }
    if (!endedEventDescriptor?.set || !endedEventDescriptor?.get) throw new Error('Audio source onended event is unavailable');
    Object.defineProperty(endedEventPrototype, 'onended', {
      configurable: endedEventDescriptor.configurable,
      enumerable: endedEventDescriptor.enumerable,
      get() { return endedEventDescriptor.get.call(this); },
      set(handler) {
        const source = this;
        const timestampedHandler = typeof handler === 'function' ? function (event) {
          if (source.__liveuiPlayback) source.__liveuiPlayback.endedAt = performance.now();
          return handler.call(this, event);
        } : handler;
        endedEventDescriptor.set.call(this, timestampedHandler);
      },
    });
    const sourceStart = AudioBufferSourceNode.prototype.start;
    AudioBufferSourceNode.prototype.start = function (...args) {
      const playback = [...window.__speechPlaybackTimeline].reverse().find((item) => item.startedAt === null);
      if (playback) {
        playback.startedAt = performance.now();
        this.__liveuiPlayback = playback;
      }
      return sourceStart.apply(this, args);
    };
    window.ocComposerAudio.playPcm = async (...args) => {
      const playback = {
        sampleRateHz: args[1], channels: args[2], audioBase64Length: args[0]?.length ?? 0,
        startCharacter: args[5], endCharacter: args[6], startedAt: null, endedAt: null, completed: false,
      };
      window.__speechPlaybackTimeline.push(playback);
      await playPcm(...args);
      playback.completed = true;
    };
    const record = () => {
      const highlighted = [...document.querySelectorAll('[data-openconnect-liveui-highlight="true"]')]
        .map((element) => ({
          text: element.innerText?.trim() || element.tagName,
          isTaskRow: element.matches('.mud-list-item'),
        }))
        .sort((left, right) => left.text.localeCompare(right.text));
      const previous = window.__liveuiHighlightTimeline.at(-1)?.highlighted;
      if (JSON.stringify(previous) !== JSON.stringify(highlighted)) {
        window.__liveuiHighlightTimeline.push({ at: performance.now(), highlighted });
      }
    };
    new MutationObserver(record).observe(document.body, {
      attributes: true,
      attributeFilter: ['data-openconnect-liveui-highlight'],
      subtree: true,
    });
    record();
  });
  await page.getByRole('textbox', { name: /message ellai/i }).fill('Which task is completed? Please use the server worker.');
  await page.getByRole('button', { name: /send to ellai/i }).click();

  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
    if (!response.ok()) throw new Error(`Acceptance fixture returned ${response.status()}: ${await response.text()}`);
    const payload = await response.json();
    if (payload.error) throw new Error(`Acceptance fixture rejected the tool result: ${JSON.stringify(payload.error)}`);
    return payload.runs.at(-1)?.completed ? 'completed' : 'running';
  }, { timeout: 90_000 }).toBe('completed');

  await expect(page.getByRole('log', { name: 'Conversation' })).toContainText(
    'I opened the Completed tab so you can see the finished work. One completed task is Review the quarterly report. Another is Draft the annual report.',
    { timeout: 90_000 });
  await expect(page.getByRole('tab', { name: 'Completed' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Review the quarterly report', { exact: true })).toBeVisible();
  await expect(page.getByText('Draft the annual report', { exact: true })).toBeVisible();
  await expect(page.getByText('Gather feedback for report', { exact: true })).toHaveCount(0);

  const response = await request.get('http://127.0.0.1:5110/__acceptance/runs');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  expect(payload.error).toBeNull();
  const run = payload.runs.at(-1);
  expect(run).toMatchObject({ completed: true, scenarioName: 'completed_task_lookup' });
  expect(run.results.map(result => result.name)).toEqual([
    'todo.list', 'liveui_infer_plan', 'liveui_execute', 'liveui_await_state',
    'liveui_highlight', 'liveui_highlight', 'liveui_highlight',
  ]);
  expect(run.results[0].evidence.titles).toEqual(['Review the quarterly report', 'Draft the annual report']);
  expect(run.results[1].evidence).toMatchObject({
    provider: 'LocalWorker',
    targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
    action: 'activate',
  });
  expect(run.results.at(-1).evidence).toMatchObject({
    outcome: 'Completed',
    postconditionVerified: true,
  });

  const workerCallsResponse = await request.get('http://127.0.0.1:5110/__acceptance/liveui-worker-calls');
  expect(workerCallsResponse.ok()).toBeTruthy();
  const workerCalls = (await workerCallsResponse.json()).calls;
  expect(workerCalls).toHaveLength(1);
  expect(workerCalls[0]).toMatchObject({
    status: 200,
    usedParaphrasedAcceptanceIntent: true,
    provider: 'qwen3-0.6b',
    actions: [{ type: 'activate', target: 'route.root.components-todolistcomponent-razor-tab-completed' }],
  });

  await expect.poll(async () => {
    const response = await request.get('http://127.0.0.1:5110/__acceptance/speech');
    if (!response.ok()) throw new Error(`Speech fixture returned ${response.status()}: ${await response.text()}`);
    const calls = (await response.json()).calls;
    return calls.filter((call) => call.text.includes('Review the quarterly report') ||
      call.text.includes('Draft the annual report')).length;
  }, { timeout: 90_000 }).toBeGreaterThanOrEqual(2);
  await expect.poll(() => page.evaluate(() => window.__speechPlaybackTimeline.length > 0 &&
    window.__speechPlaybackTimeline.every((playback) => playback.completed)), { timeout: 90_000 }).toBe(true);
  await expect.poll(() => page.evaluate(() => window.__liveuiHighlightTimeline.length), { timeout: 15_000 }).toBeGreaterThanOrEqual(5);
  const timeline = await page.evaluate(() => window.__liveuiHighlightTimeline);
  expect(timeline.some((event) => event.highlighted.some((target) =>
    !target.isTaskRow && target.text.includes('Workflows & Tasks')))).toBe(true);

  const speechResponse = await request.get('http://127.0.0.1:5110/__acceptance/speech');
  expect(speechResponse.ok()).toBeTruthy();
  const speech = await speechResponse.json();
  const taskSpeech = speech.calls.filter((call) => call.text.includes('Review the quarterly report') ||
    call.text.includes('Draft the annual report'));
  const playbacks = await page.evaluate(() => window.__speechPlaybackTimeline);
  expect(taskSpeech.length, `speech requests: ${JSON.stringify(speech.calls)}; browser PCM playback: ${JSON.stringify(playbacks)}`).toBeGreaterThanOrEqual(2);
  expect(taskSpeech.every((call) => call.audioBytes > 0)).toBe(true);
  if (process.env.OPENCONNECT_LIVEUI_REAL_KOKORO_WORKER) {
    expect(taskSpeech.filter((call) => call.provider === 'kokoro').length).toBeGreaterThanOrEqual(2);
  }
  const conversationText = await page.getByRole('log', { name: 'Conversation' }).innerText();
  const assistantResponseStart = conversationText.indexOf('I’ll look through your tasks now.');
  expect(assistantResponseStart).toBeGreaterThanOrEqual(0);
  expect(conversationText.indexOf(run.answer, assistantResponseStart)).toBeGreaterThanOrEqual(assistantResponseStart);
  for (const title of run.results[0].evidence.titles) {
    const titleStart = conversationText.indexOf(title, assistantResponseStart) - assistantResponseStart;
    expect(titleStart, `the spoken response must include ${title}`).toBeGreaterThanOrEqual(0);
    const spokenSegment = playbacks.find((playback) => playback.startCharacter <= titleStart &&
      playback.endCharacter >= titleStart + title.length);
    expect(spokenSegment, `no completed PCM segment covered ${title}: ${JSON.stringify(playbacks)}`).toBeTruthy();
    expect(spokenSegment.startedAt, `PCM playback never started for ${title}`).toBeGreaterThan(0);
    expect(spokenSegment.endedAt, `PCM playback never ended for ${title}`).toBeGreaterThan(spokenSegment.startedAt);
    const highlightedDuringSpeech = timeline.some((event, index) => {
      const activeUntil = timeline[index + 1]?.at ?? Number.POSITIVE_INFINITY;
      return event.highlighted.some((target) => target.isTaskRow && target.text.includes(title)) &&
        event.at <= spokenSegment.endedAt && activeUntil >= spokenSegment.startedAt;
    });
    expect(highlightedDuringSpeech, `${title} was not highlighted during its spoken segment`).toBe(true);
  }
});
