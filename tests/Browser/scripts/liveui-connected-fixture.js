import http from 'node:http';
import { spawn } from 'node:child_process';

const steps = [
  { name: 'liveui_get_current_page', arguments: {} },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      action: 'SelectTab',
    },
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      stateName: 'selected',
      stateValue: 'true',
    },
  },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description',
      action: 'SetValue',
      value: 'report',
    },
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
      stateName: 'textContains',
      stateValue: 'report',
    },
  },
  {
    name: 'liveui_highlight',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
    },
  },
];

const navigationSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  { name: 'liveui_navigate', arguments: { routeId: 'route.root' }, kind: 'receipt' },
  {
    name: 'liveui_highlight',
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    kind: 'receipt',
  },
];

const tourSteps = [
  { name: 'liveui_infer_plan', arguments: { intent: 'Highlight the task list for an introductory tour.' }, kind: 'unavailable_plan' },
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  { name: 'liveui_highlight', arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' }, kind: 'receipt' },
];

const statusSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      action: 'SelectTab',
    },
    kind: 'receipt',
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      stateName: 'selected',
      stateValue: 'true',
    },
    kind: 'receipt',
  },
];

const activeStatusSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-active',
      action: 'SelectTab',
    },
    kind: 'receipt',
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-active',
      stateName: 'selected',
      stateValue: 'true',
    },
    kind: 'receipt',
  },
];

const unavailableLocalInferenceSteps = [
  { name: 'liveui_infer_plan', arguments: { intent: 'Pull up my active tasks.' }, kind: 'unavailable_plan' },
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-active',
      action: 'SelectTab',
    },
    kind: 'receipt',
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-active',
      stateName: 'selected',
      stateValue: 'true',
    },
    kind: 'receipt',
  },
];

const workerInferenceSteps = [
  { name: 'liveui_infer_plan', arguments: { intent: 'Switch to the tab showing finished work.' }, kind: 'worker_plan' },
  { name: 'liveui_execute', arguments: {}, kind: 'receipt' },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      stateName: 'selected',
      stateValue: 'true',
    },
    kind: 'receipt',
  },
];

const localInferenceSteps = [
  { name: 'liveui_infer_plan', arguments: { intent: 'Show my completed tasks.' }, kind: 'plan' },
  { name: 'liveui_execute', arguments: {}, kind: 'receipt' },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-tab-completed',
      stateName: 'selected',
      stateValue: 'true',
    },
    kind: 'receipt',
  },
];

const searchSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description',
      action: 'SetValue',
      value: 'report',
    },
    kind: 'receipt',
  },
  {
    name: 'liveui_await_state',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
      stateName: 'textContains',
      stateValue: 'report',
    },
    kind: 'receipt',
  },
];

const recommendationSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  { name: 'todo.list', arguments: {}, kind: 'recommendation' },
  {
    name: 'liveui_highlight',
    arguments: {
      targetId: 'route.root.components-todolistcomponent-razor-list-task-list',
      matchText: 'Prepare a launch checklist',
    },
    kind: 'receipt',
  },
];

function createCompletedTaskLookupSteps() {
  const steps = [
    { name: 'todo.list', arguments: {}, kind: 'completed_tasks' },
    {
      name: 'liveui_infer_plan',
      arguments: { intent: realLiveUIWorkerContainer ? 'Switch to the tab showing finished work.' : 'Select the Completed tab.' },
      kind: realLiveUIWorkerContainer ? 'worker_plan' : 'unavailable_plan',
    },
    {
      name: 'liveui_execute',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-tab-completed', action: 'SelectTab' },
      kind: 'receipt',
    },
    {
      name: 'liveui_await_state',
      arguments: {
        targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
        stateName: 'textContains',
        stateValue: '',
      },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list', speechStartCharacter: 0, speechEndCharacter: 0 },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-list-task-list', matchText: '', speechStartCharacter: 0, speechEndCharacter: 0 },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-list-task-list', matchText: '', speechStartCharacter: 0, speechEndCharacter: 0 },
      kind: 'receipt',
    },
  ];

  if (!realLiveUIWorkerContainer) {
    steps.splice(2, 0, { name: 'liveui_get_current_page', arguments: {}, kind: 'page' });
  }

  return steps;
}

function createMostRecentTaskSteps() {
  return [
    { name: 'todo.list', arguments: {}, kind: 'most_recent_task' },
    { name: 'liveui_infer_plan', arguments: { intent: 'Show the most recently created task in the current UI.' }, kind: 'unavailable_plan' },
    { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
    {
      name: 'liveui_execute',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-tab-all', action: 'SelectTab' },
      kind: 'receipt',
    },
    {
      name: 'liveui_await_state',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-tab-all', stateName: 'selected', stateValue: 'true' },
      kind: 'receipt',
    },
    {
      name: 'liveui_execute',
      arguments: {
        targetId: 'route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description',
        action: 'SetValue',
        value: '',
      },
      kind: 'receipt',
    },
    {
      name: 'liveui_await_state',
      arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list', stateName: 'textContains', stateValue: '' },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: {
        targetId: 'route.root.components-todolistcomponent-razor-region-todo-list',
        speechStartCharacter: 0,
        speechEndCharacter: 0,
      },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: {
        targetId: 'route.root.components-todolistcomponent-razor-list-task-list',
        matchText: '',
        speechStartCharacter: 0,
        speechEndCharacter: 0,
      },
      kind: 'receipt',
    },
    {
      name: 'liveui_highlight',
      arguments: {
        targetId: 'route.root.components-todolistcomponent-razor-list-task-list',
        matchText: '',
        speechStartCharacter: 0,
        speechEndCharacter: 0,
      },
      kind: 'receipt',
    },
  ];
}

const explanationSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_inspect',
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    kind: 'receipt',
  },
  {
    name: 'liveui_highlight',
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    kind: 'receipt',
  },
];

const statusHighlightSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_highlight',
    arguments: { targetId: 'route.root.components-todolistcomponent-razor-region-todo-list' },
    kind: 'receipt',
  },
];

const deniedWriteSteps = [
  { name: 'liveui_get_current_page', arguments: {}, kind: 'page' },
  {
    name: 'liveui_execute',
    arguments: {
      targetId: 'route.root.components-todoeditorcomponent-razor-button-add-task',
      action: 'Click',
    },
    kind: 'denied',
  },
];

const runs = [];
const speechCalls = [];
const liveUIWorkerCalls = [];
let fixtureError = null;
let speechUnavailable = false;
const realKokoroWorkerContainer = process.env.OPENCONNECT_LIVEUI_REAL_KOKORO_WORKER;
const realLiveUIWorkerContainer = process.env.OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER;

function requestLiveUIWorker(container, method, path, body) {
  const script = `import json, os, sys, urllib.error, urllib.request
request_data = json.load(sys.stdin)
data = None if request_data["body"] is None else json.dumps(request_data["body"]).encode()
headers = {"Authorization": "Bearer " + os.environ["OPENCONNECT_WORKER_TOKEN"]}
if data is not None:
    headers["Content-Type"] = "application/json"
worker_request = urllib.request.Request(
    "http://127.0.0.1:8080" + request_data["path"], data=data, headers=headers, method=request_data["method"]
)
try:
    with urllib.request.urlopen(worker_request, timeout=60) as response:
        result = {"status": response.status, "body": response.read().decode()}
except urllib.error.HTTPError as error:
    result = {"status": error.code, "body": error.read().decode()}
sys.stdout.write(json.dumps(result))
`;

  return new Promise((resolve, reject) => {
    const child = spawn('docker', ['exec', '-i', container, 'python', '-c', script], { windowsHide: true });
    const output = [];
    let outputBytes = 0;
    const timeout = setTimeout(() => child.kill(), 65_000);
    child.stdout.on('data', (chunk) => {
      outputBytes += chunk.length;
      if (outputBytes > 1024 * 1024) {
        child.kill();
        reject(new Error('liveui_worker_response_too_large'));
        return;
      }
      output.push(chunk);
    });
    child.stderr.resume();
    child.on('error', () => reject(new Error('liveui_worker_unavailable')));
    child.on('close', (code) => {
      clearTimeout(timeout);
      if (code !== 0) {
        reject(new Error('liveui_worker_failed'));
        return;
      }
      try {
        resolve(JSON.parse(Buffer.concat(output).toString('utf8')));
      } catch {
        reject(new Error('liveui_worker_response_invalid'));
      }
    });
    child.stdin.on('error', () => {});
    child.stdin.end(JSON.stringify({ method, path, body }), 'utf8');
  });
}

function synthesizeWithKokoroContainer(container, body) {
  const script = `import json, os, sys, urllib.request
body = json.load(sys.stdin)
request = urllib.request.Request(
    "http://127.0.0.1:8080/internal/v1/execute",
    data=json.dumps(body).encode(),
    headers={"Authorization": "Bearer " + os.environ["OPENCONNECT_WORKER_TOKEN"], "Content-Type": "application/json"},
)
with urllib.request.urlopen(request, timeout=120) as response:
    sys.stdout.write(response.read().decode())
`;

  return new Promise((resolve, reject) => {
    const child = spawn('docker', ['exec', '-i', container, 'python', '-c', script], { windowsHide: true });
    const output = [];
    let outputBytes = 0;
    let timeout;

    child.stdout.on('data', (chunk) => {
      outputBytes += chunk.length;
      if (outputBytes > 12 * 1024 * 1024) {
        child.kill();
        reject(new Error('kokoro_response_too_large'));
        return;
      }
      output.push(chunk);
    });
    child.stderr.resume();
    child.on('error', () => reject(new Error('kokoro_worker_unavailable')));
    child.on('close', (code) => {
      clearTimeout(timeout);
      if (code !== 0) {
        reject(new Error('kokoro_worker_failed'));
        return;
      }
      try {
        resolve(JSON.parse(Buffer.concat(output).toString('utf8')));
      } catch {
        reject(new Error('kokoro_response_invalid'));
      }
    });
    timeout = setTimeout(() => child.kill(), 125_000);
    child.stdin.on('error', () => {});
    child.stdin.end(JSON.stringify(body), 'utf8');
  });
}

function json(response, status, value) {
  const body = Buffer.from(JSON.stringify(value));
  response.writeHead(status, {
    'content-type': 'application/json',
    'content-length': body.length,
  });
  response.end(body);
}

function parseToolResult(message) {
  const content = message?.content;
  if (typeof content !== 'string') return null;
  try {
    const parsed = JSON.parse(content);
    return parsed?.result ?? parsed;
  } catch {
    return null;
  }
}

function describeToolMessage(message) {
  return JSON.stringify({ role: message?.role, content: message?.content, tool_call_id: message?.tool_call_id });
}

function readTodoRecords(value) {
  if (typeof value === 'string') {
    try { return readTodoRecords(JSON.parse(value)); }
    catch { return null; }
  }
  if (Array.isArray(value)) return value.flatMap(readTodoRecords);
  if (!value || typeof value !== 'object') return [];
  const title = value.Title ?? value.title;
  const isCompleted = value.IsCompleted ?? value.isCompleted ?? value.is_completed;
  if (typeof title === 'string' && isCompleted !== undefined && (value.Priority ?? value.priority) !== undefined)
    return [value];
  return Object.values(value).flatMap(readTodoRecords);
}

function priorityRank(value) {
  if (typeof value === 'number') return value;
  if (typeof value === 'string' && /^\d+$/.test(value)) return Number(value);
  return ({ Low: 0, Medium: 1, High: 2, Urgent: 3 })[value] ?? Number.NEGATIVE_INFINITY;
}

function findUniqueUrgentIncompleteTask(value) {
  const incomplete = readTodoRecords(value).filter((task) => {
    const isCompleted = task.IsCompleted ?? task.isCompleted ?? task.is_completed;
    return isCompleted === false || isCompleted === 'false';
  });
  if (incomplete.length === 0) return null;
  const highestPriority = Math.max(...incomplete.map((task) => priorityRank(task.Priority ?? task.priority)));
  const highest = incomplete.filter((task) => priorityRank(task.Priority ?? task.priority) === highestPriority);
  if (highest.length !== 1) return null;
  const task = highest[0];
  const title = task.Title ?? task.title;
  const priority = priorityRank(task.Priority ?? task.priority);
  if (title !== 'Prepare a launch checklist' || priority !== 3) return null;
  return { title, priority, isCompleted: false, uniqueHighestPriority: true };
}

function findUniqueMostRecentTask(value) {
  const tasks = readTodoRecords(value)
    .map((task) => ({
      title: task.Title ?? task.title,
      createdAt: Date.parse(task.CreatedAtUtc ?? task.createdAtUtc ?? task.created_at_utc ?? ''),
    }))
    .filter((task) => typeof task.title === 'string' && Number.isFinite(task.createdAt));
  if (tasks.length === 0) return null;
  const newestTime = Math.max(...tasks.map((task) => task.createdAt));
  const newest = tasks.filter((task) => task.createdAt === newestTime);
  return newest.length === 1 ? newest[0] : null;
}

function receiptIsVerified(receipt) {
  return receipt?.outcome === 'Completed' && receipt?.postconditionVerified === true;
}

function receiptIsDenied(receipt) {
  return receipt?.outcome === 'Denied' && receipt?.postconditionVerified === false;
}

function workerMatrixActions(receipt) {
  if (receipt?.provider !== 'LocalWorker' || receipt.outcome !== 'completed' ||
      !Array.isArray(receipt.plan?.actions) || receipt.plan.actions.length === 0) {
    throw new Error(`The local worker did not return a validated plan: ${JSON.stringify(receipt)}`);
  }
  return receipt.plan.actions.map(action => ({ type: action.type, targetId: action.targetId, value: action.value ?? null }));
}

function workerMatrixSteps(actions) {
  return actions.map(action => {
    if (action.type === 'navigate') return { name: 'liveui_navigate', arguments: { routeId: action.targetId }, kind: 'receipt' };
    if (action.type === 'activate') return { name: 'liveui_execute', arguments: { targetId: action.targetId, action: 'SelectTab' }, kind: 'receipt' };
    if (action.type === 'setValue') return { name: 'liveui_execute', arguments: { targetId: action.targetId, action: 'SetValue', value: action.value }, kind: 'receipt' };
    if (action.type === 'highlight') return { name: 'liveui_highlight', arguments: { targetId: action.targetId }, kind: 'receipt' };
    if (action.type === 'inspect') return { name: 'liveui_inspect', arguments: { targetId: action.targetId }, kind: 'receipt' };
    throw new Error(`Worker matrix plan contains an unsupported action: ${action.type}`);
  });
}

function checkPreviousToolResult(run, toolMessages) {
  if (run.results.length === 0) return;
  const priorMessage = toolMessages.at(-1);
  const receipt = parseToolResult(priorMessage);
  const step = run.results.at(-1);
  if (step.kind === 'worker_matrix_plan') {
    const actions = workerMatrixActions(receipt);
    step.evidence = { provider: receipt.provider, actions };
    run.steps.push(...workerMatrixSteps(actions));
    return;
  }
  if (step.kind === 'plan') {
    const actions = receipt?.plan?.actions;
    if (receipt?.provider !== 'Browser' || !Array.isArray(actions) || actions.length !== 1) {
      throw new Error(`Browser inference did not return one validated action plan: ${JSON.stringify(receipt)}`);
    }
    const action = actions[0];
    const targetId = action.targetId ?? action.TargetId;
    const actionType = action.type ?? action.Type;
    if (targetId !== 'route.root.components-todolistcomponent-razor-tab-completed' || actionType !== 'activate') {
      throw new Error(`Browser inference returned an unexpected target or action: ${JSON.stringify(action)}`);
    }
    step.evidence = { provider: receipt.provider, targetId, action: actionType, outcome: receipt.outcome };
    run.steps[1].arguments = { targetId, action: 'SelectTab' };
    return;
  }
  if (step.kind === 'unavailable_plan') {
    if (receipt?.outcome !== 'inference_unavailable' || receipt.plan != null) {
      throw new Error(`Unavailable local inference did not return a clean fallback result: ${JSON.stringify(receipt)}`);
    }
    step.evidence = { outcome: receipt.outcome, provider: receipt.provider, fallback: 'server-model' };
    return;
  }
  if (step.kind === 'worker_plan') {
    const actions = receipt?.plan?.actions;
    if (receipt?.provider !== 'LocalWorker' || receipt.outcome !== 'completed' || !Array.isArray(actions) || actions.length !== 1) {
      throw new Error(`The local worker did not return one validated action plan: parsed=${JSON.stringify(receipt)} message=${describeToolMessage(priorMessage)}`);
    }
    const action = actions[0];
    const targetId = action.targetId ?? action.TargetId;
    const actionType = action.type ?? action.Type;
    if (targetId !== 'route.root.components-todolistcomponent-razor-tab-completed' || actionType !== 'activate') {
      throw new Error(`The local worker returned an unexpected target or action: ${JSON.stringify(action)}`);
    }
    step.evidence = { provider: receipt.provider, targetId, action: actionType, outcome: receipt.outcome };
    run.steps[1].arguments = { targetId, action: 'SelectTab' };
    return;
  }
  if (step.kind === 'page') {
    if (!receipt || !Array.isArray(receipt.targets) ||
        (receipt.targets.length === 0 &&
         (!Array.isArray(receipt.navigableRoutes) || receipt.navigableRoutes.length === 0))) {
      throw new Error('Current-page discovery returned neither authorized targets nor permitted navigation routes.');
    }
    step.evidence = {
      routeId: receipt.routeId,
      targetCount: receipt.targets.length,
      navigableRouteCount: receipt.navigableRoutes?.length ?? 0,
      authorizedTargetIds: receipt.targets.map((target) => target.targetId),
    };
    return;
  }
  if (step.kind === 'recommendation') {
    const candidate = findUniqueUrgentIncompleteTask(receipt);
    if (!candidate) throw new Error(`todo.list did not return a unique highest-priority Urgent incomplete task: ${JSON.stringify(receipt)}`);
    step.evidence = candidate;
    return;
  }
  if (step.kind === 'most_recent_task') {
    const task = findUniqueMostRecentTask(receipt);
    if (!task) throw new Error(`todo.list did not return a unique most recently created task: ${JSON.stringify(receipt)}`);
    run.answer = `I opened your task list so you can see the available work. The most recently created task is ${task.title}. I brought its row into focus while I explained it.`;
    run.steps[1].arguments = { intent: `Show the task “${task.title}” in the current UI.` };
    run.steps[5].arguments.value = task.title;
    run.steps[6].arguments.stateValue = task.title;
    run.steps[7].arguments.speechEndCharacter = run.answer.indexOf('.') + 1;
    const titleStart = run.answer.indexOf(task.title);
    run.steps[8].arguments.matchText = task.title;
    run.steps[8].arguments.speechStartCharacter = titleStart;
    run.steps[8].arguments.speechEndCharacter = titleStart + task.title.length + 1;
    run.steps[9].arguments.matchText = task.title;
    run.steps[9].arguments.speechStartCharacter = run.answer.indexOf('I brought its row');
    run.steps[9].arguments.speechEndCharacter = run.answer.length;
    step.evidence = { title: task.title, createdAt: new Date(task.createdAt).toISOString() };
    return;
  }
  if (step.kind === 'completed_tasks') {
    const completed = readTodoRecords(receipt).filter((task) => {
      const value = task.IsCompleted ?? task.isCompleted ?? task.is_completed;
      return value === true || value === 'true';
    });
    if (completed.length !== 2) throw new Error(`Expected two deterministic completed acceptance tasks: ${JSON.stringify(completed)}`);
    const titles = completed.map((task) => task.Title ?? task.title);
    run.answer = `I opened the Completed tab so you can see the finished work. One completed task is ${titles[0]}. Another is ${titles[1]}.`;
    const listContentWait = run.steps.find((candidate) => candidate.name === 'liveui_await_state');
    if (!listContentWait) throw new Error('Completed-task narration must verify the rendered task list after tab selection.');
    listContentWait.arguments.stateValue = titles[0];
    run.steps[1].arguments = {
      intent: realLiveUIWorkerContainer ? 'Switch to the tab showing finished work.' : 'Show my completed tasks in the current UI.',
    };
    const firstSentenceEnd = run.answer.indexOf('.') + 1;
    const firstTitleStart = run.answer.indexOf(titles[0]);
    const secondTitleStart = run.answer.indexOf(titles[1]);
    const highlightSteps = run.steps.filter((candidate) => candidate.name === 'liveui_highlight');
    if (highlightSteps.length !== 3) throw new Error('Completed-task narration requires one region highlight and two row highlights.');
    highlightSteps[0].arguments.speechEndCharacter = firstSentenceEnd;
    highlightSteps[1].arguments.matchText = titles[0];
    highlightSteps[1].arguments.speechStartCharacter = firstTitleStart;
    highlightSteps[1].arguments.speechEndCharacter = firstTitleStart + titles[0].length + 1;
    highlightSteps[2].arguments.matchText = titles[1];
    highlightSteps[2].arguments.speechStartCharacter = secondTitleStart;
    highlightSteps[2].arguments.speechEndCharacter = secondTitleStart + titles[1].length + 1;
    step.evidence = { titles };
    return;
  }
  if (step.kind === 'denied') {
    if (!receiptIsDenied(receipt)) {
      throw new Error(`LiveUI tool ${step.name} did not return a policy denial: ${JSON.stringify(receipt)}`);
    }
    step.evidence = receipt;
    return;
  }
  if (!receiptIsVerified(receipt)) {
    throw new Error(`LiveUI tool ${step.name} returned an unverified receipt: ${JSON.stringify(receipt)}`);
  }
  step.evidence = receipt;
}

async function readRequest(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', 'http://127.0.0.1');
  if (request.method === 'GET' && url.pathname === '/health') {
    return json(response, 200, { status: 'ready' });
  }
  if (request.method === 'GET' && url.pathname === '/__acceptance/liveui-worker-calls') {
    return json(response, 200, { calls: liveUIWorkerCalls });
  }
  if (url.pathname.startsWith('/__acceptance/liveui-worker/')) {
    if (!realLiveUIWorkerContainer ||
        !/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(realLiveUIWorkerContainer) ||
        request.headers.authorization !== `Bearer ${process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN}`) {
      return json(response, 404, { error: 'worker_bridge_unavailable' });
    }
    if (!['GET', 'POST'].includes(request.method)) return json(response, 405, { error: 'method_not_allowed' });
    try {
      const body = request.method === 'POST' ? await readRequest(request) : null;
      const workerPath = url.pathname.slice('/__acceptance/liveui-worker'.length);
      const result = await requestLiveUIWorker(realLiveUIWorkerContainer, request.method, workerPath, body);
      if (request.method === 'POST' && workerPath === '/v1/liveui/inference/plan') {
        try {
          const workerResult = JSON.parse(result.body);
          liveUIWorkerCalls.push({
            status: result.status,
            usedParaphrasedAcceptanceIntent: body.intent === 'Switch to the tab showing finished work.',
            provider: workerResult.provider,
            actions: workerResult.plan?.actions?.map((action) => ({ type: action.type, target: action.target })),
          });
        } catch {
          liveUIWorkerCalls.push({ status: result.status, provider: 'invalid_response' });
        }
      }
      response.writeHead(result.status, { 'content-type': 'application/json' });
      return response.end(result.body);
    } catch {
      return json(response, 502, { error: 'liveui_worker_bridge_failed' });
    }
  }
  if (request.method === 'POST' && url.pathname === '/__acceptance/speech/unavailable') {
    speechUnavailable = true;
    return json(response, 200, { status: 'unavailable' });
  }
  if (request.method === 'POST' && url.pathname === '/__acceptance/speech/available') {
    speechUnavailable = false;
    return json(response, 200, { status: 'available' });
  }
  if (request.method === 'GET' && url.pathname === '/__acceptance/runs') {
    return json(response, fixtureError ? 500 : 200, { runs, error: fixtureError });
  }
  if (request.method === 'GET' && url.pathname === '/__acceptance/speech') {
    return json(response, 200, { calls: speechCalls });
  }
  if (request.method === 'POST' && url.pathname === '/internal/v1/execute') {
    if (request.headers.authorization !== 'Bearer local-acceptance-kokoro-token')
      return json(response, 401, { error: 'unauthorized' });
    if (speechUnavailable) return json(response, 503, { error: 'speech_worker_unavailable' });
    const body = await readRequest(request);
    if (body.kind !== 'speech.synthesize' || typeof body.payload?.text !== 'string')
      return json(response, 400, { error: 'invalid_speech_request' });
    if (realKokoroWorkerContainer) {
      if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(realKokoroWorkerContainer))
        return json(response, 500, { error: 'invalid_kokoro_container_configuration' });
      try {
        const synthesis = await synthesizeWithKokoroContainer(realKokoroWorkerContainer, body);
        const audioBytes = Buffer.from(synthesis.result?.audioBase64 ?? '', 'base64').length;
        if (audioBytes === 0) return json(response, 502, { error: 'kokoro_returned_empty_audio' });
        speechCalls.push({ text: body.payload.text, voiceId: body.payload.voiceId, format: body.payload.format,
          provider: 'kokoro', audioBytes });
        return json(response, 200, synthesis);
      } catch {
        return json(response, 502, { error: 'real_kokoro_worker_failed' });
      }
    }
    const sampleRateHz = 24_000;
    const sampleCount = 48_000;
    const pcm = Buffer.alloc(sampleCount * 2);
    for (let index = 0; index < sampleCount; index += 1) {
      const sample = Math.round(Math.sin(2 * Math.PI * 220 * index / sampleRateHz) * 2_000);
      pcm.writeInt16LE(sample, index * 2);
    }
    speechCalls.push({ text: body.payload.text, voiceId: body.payload.voiceId, format: body.payload.format,
      provider: 'fixture-tone', audioBytes: pcm.length });
    return json(response, 200, {
      result: { audioBase64: pcm.toString('base64'), sampleRateHz, channels: 1 },
    });
  }
  if (request.method !== 'POST' || !url.pathname.endsWith('/chat/completions')) {
    return json(response, 404, { error: { message: 'not_found' } });
  }

  try {
    const body = await readRequest(request);
    const messages = Array.isArray(body.messages) ? body.messages : [];
    const toolMessages = messages.filter((message) => message?.role === 'tool');
    let run;
    if (toolMessages.length === 0) {
      const userText = messages.filter((item) => item?.role === 'user').at(-1)?.content ?? '';
      const normalizedUserText = typeof userText === 'string' ? userText : JSON.stringify(userText);
      const completedTaskLookup = /which task is completed|which tasks are completed|what task is completed|what tasks are completed/i.test(normalizedUserText);
      const mostRecentTask = /most recent|latest task|last task/i.test(normalizedUserText);
      const recommendation = /work on|recommend/i.test(normalizedUserText);
      const localInference = /on-device model|local model|browser inference/i.test(normalizedUserText);
      const workerInference = /server worker|qwen worker/i.test(normalizedUserText);
      const deniedWrite = /highlight the add task button/i.test(normalizedUserText);
      const highlightRetry = /highlight didn't come through|retry(?:ing)? .*highlight|highlight .*retry/i.test(normalizedUserText);
      const statusHighlight = /highlight (?:the )?(?:task )?(?:status|list|progress)/i.test(normalizedUserText);
      const explanation = /explain what|what am i looking at/i.test(normalizedUserText);
      const combined = /completed tasks/i.test(normalizedUserText) && /report/i.test(normalizedUserText);
      const activeStatus = /active tasks|pull up my active/i.test(normalizedUserText);
      const unavailableLocalInference = /local inference unavailable|when local inference is unavailable/i.test(normalizedUserText);
      const status = /completed tasks/i.test(normalizedUserText);
      const search = /find tasks|search tasks|tasks containing report/i.test(normalizedUserText);
      const navigation = /open my todo list|open the todo list/i.test(normalizedUserText);
      const tour = /show me around|walk me through/i.test(normalizedUserText);
      const workerMatrix = realLiveUIWorkerContainer && [
        'Open my tasks', 'Open my Todo list.', 'Show completed tasks', 'Show active tasks', 'Show all tasks',
        'Search for report', 'Highlight the tasks', 'Highlight the results.', 'Show completed tasks containing report',
        'Explain the current view.',
      ].includes(normalizedUserText);
      const scenarioName = workerMatrix ? 'worker_matrix'
        : deniedWrite ? 'denied_write'
        : highlightRetry ? 'highlight_retry'
        : statusHighlight ? 'status_highlight'
        : completedTaskLookup ? 'completed_task_lookup'
        : mostRecentTask ? 'most_recent_task'
        : recommendation ? 'recommendation'
        : localInference ? 'local_inference'
        : workerInference ? 'worker_inference'
        : unavailableLocalInference ? 'unavailable_local_inference'
        : explanation ? 'explanation'
        : combined ? 'combined'
        : activeStatus ? 'active_status'
        : status ? 'status'
        : search ? 'search'
        : navigation ? 'navigation'
        : tour ? 'tour'
        : 'combined';
      const answers = {
        navigation: 'I opened the Todo page, verified it was ready, and highlighted the task region.',
        tour: 'This task region shows your work and its filters. Would you like to continue the tour?',
        worker_matrix: normalizedUserText === 'Explain the current view.'
          ? 'The task region is visible.' : 'The requested task view is ready.',
        highlight_retry: 'I retried the highlight and highlighted the Todo task list so you can see the task statuses.',
        status_highlight: 'I highlighted the Todo task list so you can see the current task statuses.',
        status: 'The Completed tab is selected and shows the completed tasks.',
        active_status: 'The Active tab is selected and shows your active tasks.',
        unavailable_local_inference: 'The Active tab is selected and shows your active tasks.',
        local_inference: 'The on-device model selected and verified the Completed tab.',
        worker_inference: 'The local inference worker selected and verified the Completed tab.',
        search: 'I searched the existing task list for report.',
        combined: 'I filtered the completed tasks to those containing report. The matching completed tasks are Review the quarterly report and Draft the annual report.',
        recommendation: 'I recommend Prepare a launch checklist because it is the only incomplete task with Urgent priority. I highlighted that task.',
        explanation: 'You are viewing Workflows & Tasks. The existing controls filter, search, and display the tasks, and I highlighted this task region.',
        denied_write: 'The page policy does not permit activating Add Task, so I left the list unchanged.',
        most_recent_task: 'I found and opened the most recently created task.',
      };
      run = {
        userText,
        scenarioName,
        steps: {
          navigation: navigationSteps,
          tour: tourSteps,
          worker_matrix: [{ name: 'liveui_infer_plan', arguments: { intent: normalizedUserText }, kind: 'worker_matrix_plan' }],
          highlight_retry: statusHighlightSteps,
          status_highlight: statusHighlightSteps,
          status: statusSteps,
          active_status: activeStatusSteps,
          unavailable_local_inference: unavailableLocalInferenceSteps,
          local_inference: localInferenceSteps,
          worker_inference: workerInferenceSteps,
          search: searchSteps,
          combined: steps,
          recommendation: recommendationSteps,
          explanation: explanationSteps,
          denied_write: deniedWriteSteps,
          completed_task_lookup: createCompletedTaskLookupSteps(),
          most_recent_task: createMostRecentTaskSteps(),
        }[scenarioName],
        answer: answers[scenarioName],
        results: [],
      };
      runs.push(run);
    } else {
      run = runs.at(-1);
      if (!run) throw new Error('A tool continuation arrived without an acceptance run.');
      checkPreviousToolResult(run, toolMessages);
    }

    const stepIndex = toolMessages.length;
    if (stepIndex < run.steps.length) {
      const step = run.steps[stepIndex];
      run.results.push({
        name: step.name,
        arguments: step.arguments,
        kind: step.kind ?? (step.name === 'liveui_get_current_page' ? 'page' : 'receipt'),
      });
      return json(response, 200, {
        id: `liveui-acceptance-${runs.length}-${stepIndex}`,
        object: 'chat.completion',
        created: 0,
        model: body.model ?? 'openconnect-liveui-fixture',
        choices: [{
          index: 0,
          message: {
            role: 'assistant',
            content: null,
            tool_calls: [{
              id: `liveui-tool-${runs.length}-${stepIndex}`,
              type: 'function',
              function: { name: step.name, arguments: JSON.stringify(step.arguments) },
            }],
          },
          finish_reason: 'tool_calls',
        }],
        usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
      });
    }

    if (run.results.length !== run.steps.length || toolMessages.length !== run.steps.length) {
      throw new Error('The model fixture reached final narration before all tools completed.');
    }
    for (const [index, message] of toolMessages.entries()) {
      const step = run.results[index];
      const result = parseToolResult(message);
      if (step.kind === 'worker_matrix_plan') {
        step.evidence = { provider: result?.provider, actions: workerMatrixActions(result) };
      } else if (step.kind === 'plan') {
        const action = result?.plan?.actions?.[0];
        if (result?.provider !== 'Browser' || !action ||
            (action.targetId ?? action.TargetId) !== 'route.root.components-todolistcomponent-razor-tab-completed' ||
            (action.type ?? action.Type) !== 'activate') {
          throw new Error(`Browser inference plan was missing or invalid: ${JSON.stringify(result)}`);
        }
        step.evidence = {
          provider: result.provider,
          targetId: action.targetId ?? action.TargetId,
          action: action.type ?? action.Type,
          outcome: result.outcome,
        };
      } else if (step.kind === 'unavailable_plan') {
        if (result?.outcome !== 'inference_unavailable' || result.plan != null) {
          throw new Error(`Unavailable local inference did not return a clean fallback result: ${JSON.stringify(result)}`);
        }
        step.evidence = { outcome: result.outcome, provider: result.provider, fallback: 'server-model' };
      } else if (step.kind === 'worker_plan') {
        const actions = result?.plan?.actions;
        if (result?.provider !== 'LocalWorker' || result.outcome !== 'completed' || !Array.isArray(actions) || actions.length !== 1) {
          throw new Error(`The local worker did not return one validated action plan: parsed=${JSON.stringify(result)} message=${describeToolMessage(message)}`);
        }
        const action = actions[0];
        const targetId = action.targetId ?? action.TargetId;
        const actionType = action.type ?? action.Type;
        if (targetId !== 'route.root.components-todolistcomponent-razor-tab-completed' || actionType !== 'activate') {
          throw new Error(`The local worker returned an unexpected target or action: ${JSON.stringify(action)}`);
        }
        step.evidence = { provider: result.provider, targetId, action: actionType, outcome: result.outcome };
        run.steps[1].arguments = { targetId, action: 'SelectTab' };
      } else if (step.kind === 'page') {
        if (!result || !Array.isArray(result.targets) ||
            (result.targets.length === 0 &&
             (!Array.isArray(result.navigableRoutes) || result.navigableRoutes.length === 0))) {
          throw new Error(`Current-page discovery returned neither authorized targets nor permitted navigation routes: ${JSON.stringify({ routeId: result?.routeId, targetCount: result?.targets?.length, routeCount: result?.navigableRoutes?.length })}`);
        }
        step.evidence = {
          routeId: result.routeId,
          targetCount: result.targets.length,
          navigableRouteCount: result.navigableRoutes?.length ?? 0,
          authorizedTargetIds: result.targets.map((target) => target.targetId),
        };
      } else if (step.kind === 'recommendation') {
        const candidate = findUniqueUrgentIncompleteTask(result);
        if (!candidate) throw new Error(`todo.list did not return a unique highest-priority Urgent incomplete task: ${JSON.stringify(result)}`);
        step.evidence = candidate;
      } else if (step.kind === 'most_recent_task') {
        const candidate = findUniqueMostRecentTask(result);
        if (!candidate || step.evidence?.title !== candidate.title || !run.answer.includes(candidate.title)) {
          throw new Error(`todo.list did not identify the task named in the interactive narration: ${JSON.stringify(result)}`);
        }
      } else if (step.kind === 'completed_tasks') {
        const completed = readTodoRecords(result).filter((task) => {
          const value = task.IsCompleted ?? task.isCompleted ?? task.is_completed;
          return value === true || value === 'true';
        }).map((task) => task.Title ?? task.title);
        if (completed.length !== 2 || JSON.stringify(completed) !== JSON.stringify(step.evidence?.titles) ||
            completed.some((title) => !run.answer.includes(title))) {
          throw new Error(`todo.list did not confirm every completed task named in the UI narration: ${JSON.stringify(result)}`);
        }
      } else if (step.kind === 'denied') {
        if (!receiptIsDenied(result)) throw new Error(`LiveUI tool ${step.name} did not return a policy denial.`);
        step.evidence = result;
      } else {
        if (!receiptIsVerified(result)) throw new Error(`LiveUI tool ${step.name} returned an unverified receipt.`);
        step.evidence = result;
      }
    }
    run.completed = true;
    return json(response, 200, {
      id: `liveui-acceptance-${runs.length}-final`,
      object: 'chat.completion',
      created: 0,
      model: body.model ?? 'openconnect-liveui-fixture',
      choices: [{
        index: 0,
        message: {
          role: 'assistant',
          content: run.answer,
        },
        finish_reason: 'stop',
      }],
      usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
    });
  } catch (error) {
    fixtureError = error instanceof Error ? error.message : String(error);
    return json(response, 500, { error: { message: fixtureError } });
  }
});

server.listen(5110, process.env.OPENCONNECT_LIVEUI_FIXTURE_HOST ?? '127.0.0.1');

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
