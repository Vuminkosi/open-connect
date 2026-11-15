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

function receiptIsVerified(receipt) {
  return receipt?.outcome === 'Completed' && receipt?.postconditionVerified === true;
}

function checkPreviousToolResult(run, toolMessages) {
  if (run.results.length === 0) return;
  const priorMessage = toolMessages.at(-1);
  const receipt = parseToolResult(priorMessage);
  if (run.results.length === 1) {
    if (!receipt || !Array.isArray(receipt.targets) || receipt.targets.length === 0) {
      throw new Error('Current-page discovery did not return a page with targets.');
    }
    run.results.at(-1).receipt = receipt;
    return;
  }
  if (!receiptIsVerified(receipt)) {
    throw new Error(`LiveUI tool ${run.results.at(-1).name} returned an unverified receipt: ${JSON.stringify(receipt)}`);
  }
  run.results.at(-1).receipt = receipt;
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
      run = { userText: messages.filter((item) => item?.role === 'user').at(-1)?.content ?? '', results: [] };
      runs.push(run);
    } else {
      run = runs.at(-1);
      if (!run) throw new Error('A tool continuation arrived without an acceptance run.');
      checkPreviousToolResult(run, toolMessages);
    }

    const stepIndex = toolMessages.length;
    if (stepIndex < steps.length) {
      const step = steps[stepIndex];
      run.results.push({ name: step.name, arguments: step.arguments });
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

    if (run.results.length !== steps.length) throw new Error('The model fixture reached final narration before all actions ran.');
    if (!toolMessages.every((message, index) => {
      const receipt = parseToolResult(message);
      return index === 0 ? Array.isArray(receipt?.targets) && receipt.targets.length > 0 : receiptIsVerified(receipt);
    })) {
      throw new Error('The model fixture refused success because at least one tool result was unverified.');
    }
    run.results.at(-1).receipt = parseToolResult(toolMessages.at(-1));
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
          content: 'I filtered the completed tasks to those containing report. The matching completed tasks are Review the quarterly report and Draft the annual report.',
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

server.listen(5110, '127.0.0.1');

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
