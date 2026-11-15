import http from 'node:http';

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
  { name: 'liveui_infer_plan', arguments: { intent: 'Activate the tab named Completed.' }, kind: 'worker_plan' },
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
let fixtureError = null;

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

function receiptIsVerified(receipt) {
  return receipt?.outcome === 'Completed' && receipt?.postconditionVerified === true;
}

function receiptIsDenied(receipt) {
  return receipt?.outcome === 'Denied' && receipt?.postconditionVerified === false;
}

function checkPreviousToolResult(run, toolMessages) {
  if (run.results.length === 0) return;
  const priorMessage = toolMessages.at(-1);
  const receipt = parseToolResult(priorMessage);
  const step = run.results.at(-1);
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
  if (request.method === 'GET' && url.pathname === '/__acceptance/runs') {
    return json(response, fixtureError ? 500 : 200, { runs, error: fixtureError });
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
      const recommendation = /work on|recommend/i.test(normalizedUserText);
      const localInference = /on-device model|local model|browser inference/i.test(normalizedUserText);
      const workerInference = /server worker|qwen worker/i.test(normalizedUserText);
      const deniedWrite = /highlight the add task button/i.test(normalizedUserText);
      const explanation = /explain what|what am i looking at/i.test(normalizedUserText);
      const combined = /completed tasks/i.test(normalizedUserText) && /report/i.test(normalizedUserText);
      const activeStatus = /active tasks|pull up my active/i.test(normalizedUserText);
      const unavailableLocalInference = /local inference unavailable|when local inference is unavailable/i.test(normalizedUserText);
      const status = /completed tasks/i.test(normalizedUserText);
      const search = /find tasks|search tasks|tasks containing report/i.test(normalizedUserText);
      const navigation = /open my todo list|open the todo list/i.test(normalizedUserText);
      const scenarioName = deniedWrite ? 'denied_write'
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
        : 'combined';
      const answers = {
        navigation: 'I opened the Todo page, verified it was ready, and highlighted the task region.',
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
      };
      run = {
        userText,
        scenarioName,
        steps: {
          navigation: navigationSteps,
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
      if (step.kind === 'plan') {
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
