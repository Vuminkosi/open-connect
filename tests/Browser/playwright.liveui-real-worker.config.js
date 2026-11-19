import { defineConfig } from '@playwright/test';
import connectedConfig from './playwright.liveui-connected.config.js';
import { randomBytes } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const endpointText = process.env.OPENCONNECT_LIVEUI_WORKER_ENDPOINT;
const bearerToken = process.env.OPENCONNECT_LIVEUI_WORKER_TOKEN;
const workerContainer = process.env.OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER;
if (process.env.OPENCONNECT_LIVEUI_REAL_WORKER_RUN !== '1') {
  throw new Error('Set OPENCONNECT_LIVEUI_REAL_WORKER_RUN=1 to enable local worker inference acceptance.');
}
if (!endpointText || !bearerToken || !workerContainer) {
  throw new Error('Set the loopback worker endpoint, acceptance token, and real worker container in the test environment.');
}
if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(workerContainer)) {
  throw new Error('OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER must be a container name, not a shell expression.');
}

const endpoint = new URL(endpointText);
if (endpoint.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(endpoint.hostname) ||
    endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
  throw new Error('Real worker browser acceptance accepts only a credential-free loopback HTTP endpoint.');
}

const [fixtureServer, appServer] = connectedConfig.webServer;
const gatewayPort = Number(process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_PORT ?? '5120');
if (!Number.isInteger(gatewayPort) || gatewayPort < 1024 || gatewayPort > 65535)
  throw new Error('OPENCONNECT_ACCEPTANCE_GATEWAY_PORT must be a valid non-privileged port.');
const gatewayUrl = `http://127.0.0.1:${gatewayPort}`;
const appUrl = new URL(connectedConfig.use.baseURL).origin;
const noBuild = process.env.OPENCONNECT_ACCEPTANCE_NO_BUILD === '1' ? ' --no-build' : '';
const isolatedVariant = process.env.OPENCONNECT_ACCEPTANCE_ISOLATED_GATEWAY;
if (isolatedVariant && !['1', '2'].includes(isolatedVariant))
  throw new Error('OPENCONNECT_ACCEPTANCE_ISOLATED_GATEWAY must be 1 or 2.');
const isolatedGateway = path.resolve(path.dirname(fileURLToPath(import.meta.url)),
  `test-results-parallel-bin/${isolatedVariant === '2' ? 'gateway-v2' : 'gateway'}/OpenConnect.Samples.Workers.Host.dll`);
if (/["%&|<>^!`$\r\n]/.test(isolatedGateway))
  throw new Error('The isolated gateway path contains shell metacharacters.');
const gatewayCommand = isolatedVariant
  ? `dotnet "${isolatedGateway}"`
  : `dotnet run --no-launch-profile --project samples/workers/shared/OpenConnect.Samples.Workers.Host/OpenConnect.Samples.Workers.Host.csproj -c Release${noBuild}`;
process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_URL = gatewayUrl;
const localGatewayBearer = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER ?? randomBytes(32).toString('base64');
process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER = localGatewayBearer;
process.env.OPENCONNECT_ACCEPTANCE_PLANNER_KIND = 'LocalWorker';
const gatewayEnvironment = {
  ...appServer.env,
  ASPNETCORE_URLS: gatewayUrl,
  OpenConnect__LiveUI__Inference__Worker__Endpoint: endpointText,
  OpenConnect__LiveUI__Inference__Worker__Token: bearerToken,
  TODO_SERVICE_URL: appUrl,
  TODO_INTERNAL_TOKEN: 'local-acceptance-todo-internal-token',
  TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
  TODO_ALLOW_MUTATIONS: 'false',
  TODO_MUTATION_APPROVAL: 'not-approved',
  OC_LOCAL_SERVICE_AUTH_ENABLED: '1',
  OC_LOCAL_SERVICE_AUTH_SECRET: localGatewayBearer,
  OC_LOCAL_SERVICE_AUTH_TENANT_ID: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
  OC_LOCAL_SERVICE_AUTH_PRINCIPAL_ID: '01ARZ3NDEKTSV4RRFFQ69G5FB0',
};
const appEnvironment = {
  ...appServer.env,
  OpenConnect__Gateway__Mode: 'ExternalGateway',
  GATEWAY_URL: `${gatewayUrl}/openconnect/v1/`,
  OpenConnect__Gateway__LocalServiceBearer: localGatewayBearer,
  TODO_INTERNAL_TOKEN: 'local-acceptance-todo-internal-token',
  TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
};

export default defineConfig({
  ...connectedConfig,
  testDir: './specs-real-worker',
  outputDir: './test-results-real-worker',
  webServer: [
    {
      ...fixtureServer,
      env: {
        ...fixtureServer.env,
        OPENCONNECT_LIVEUI_REAL_WORKER_CONTAINER: workerContainer,
        OPENCONNECT_LIVEUI_REAL_KOKORO_WORKER: process.env.OPENCONNECT_LIVEUI_REAL_KOKORO_WORKER,
        OPENCONNECT_LIVEUI_WORKER_TOKEN: bearerToken,
      },
      reuseExistingServer: false,
    },
    {
      command: gatewayCommand,
      cwd: connectedConfig.webServer[1].cwd,
      url: `${gatewayUrl}/openconnect/v1/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: gatewayEnvironment,
    },
    { ...appServer, env: appEnvironment, reuseExistingServer: false },
  ],
});
