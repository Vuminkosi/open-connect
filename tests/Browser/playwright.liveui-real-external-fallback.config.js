import { defineConfig } from '@playwright/test';
import { randomBytes } from 'node:crypto';
import base from './playwright.liveui-connected.config.js';

const endpoint = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_ENDPOINT;
const apiKey = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_API_KEY;
const model = process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_MODEL;
if (process.env.OPENCONNECT_LIVEUI_REAL_PROVIDER_RUN !== '1' || !endpoint || !apiKey || !model) {
  throw new Error('Explicit real-provider acceptance configuration is required.');
}
const [fixture, app] = base.webServer;
const gatewayUrl = 'http://127.0.0.1:5120';
const bearer = process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER ?? randomBytes(32).toString('base64');
process.env.OPENCONNECT_ACCEPTANCE_GATEWAY_BEARER = bearer;

export default defineConfig({
  ...base,
  testDir: './specs-real-external-fallback',
  outputDir: './test-results-real-external-fallback',
  timeout: 300_000,
  webServer: [
    { ...fixture, reuseExistingServer: false },
    {
      command: 'node scripts/liveui-unavailable-worker.js',
      url: 'http://127.0.0.1:5311/__stats',
      timeout: 30_000,
      reuseExistingServer: false,
    },
    {
      command: 'dotnet run --no-launch-profile --project samples/workers/shared/OpenConnect.Samples.Workers.Host/OpenConnect.Samples.Workers.Host.csproj -c Release',
      cwd: app.cwd,
      url: `${gatewayUrl}/openconnect/v1/health`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: { ...app.env,
        ASPNETCORE_URLS: gatewayUrl,
        OpenConnect__ApiUrl: endpoint,
        OpenConnect__ApiKey: apiKey,
        OpenConnect__ReasoningModel: model,
        OpenConnect__LiveUI__ExternalPlanner__Enabled: 'true',
        OpenConnect__LiveUI__Inference__Worker__Endpoint: 'http://127.0.0.1:5311/',
        OpenConnect__LiveUI__Inference__Worker__Token: 'liveui-fallback-acceptance-token',
        TODO_SERVICE_URL: 'http://127.0.0.1:5100',
        TODO_INTERNAL_TOKEN: 'local-acceptance-todo-internal-token',
        TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
        TODO_ALLOW_MUTATIONS: 'false',
        TODO_MUTATION_APPROVAL: 'not-approved',
        OC_LOCAL_SERVICE_AUTH_ENABLED: '1',
        OC_LOCAL_SERVICE_AUTH_SECRET: bearer,
        OC_LOCAL_SERVICE_AUTH_TENANT_ID: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
        OC_LOCAL_SERVICE_AUTH_PRINCIPAL_ID: '01ARZ3NDEKTSV4RRFFQ69G5FB0',
      },
    },
    { ...app, reuseExistingServer: false, env: { ...app.env,
      OpenConnect__Gateway__Mode: 'ExternalGateway',
      GATEWAY_URL: `${gatewayUrl}/openconnect/v1/`,
      OpenConnect__Gateway__LocalServiceBearer: bearer,
      OpenConnect__LiveUI__ExternalGatewayPlanner: 'true',
      TODO_INTERNAL_TOKEN: 'local-acceptance-todo-internal-token',
      TODO_INTERNAL_TENANT: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
    } },
  ],
});
