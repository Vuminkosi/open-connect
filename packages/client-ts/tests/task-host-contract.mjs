import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { once } from 'node:events';
import Ajv2020 from 'ajv/dist/2020.js';
import { parse } from 'yaml';
import { OpenConnectClient, OpenConnectError } from '../dist/index.js';

const [baseUrl, authorization] = process.argv.slice(2);
const specification = parse(await fs.readFile(new URL('../../../contracts/openapi/openconnect.v1.yaml', import.meta.url), 'utf8'));
const ajv = new Ajv2020({ strict: false });
function validator(schema) {
  return ajv.compile({ ...schema, components: specification.components });
}
const schemas = {
  202: validator(specification.paths['/tasks'].post.responses['202'].content['application/json'].schema),
  404: validator(specification.paths['/tasks/{id}'].get.responses['404'].content['application/problem+json'].schema),
  409: validator(specification.paths['/tasks'].post.responses['409'].content['application/problem+json'].schema),
};
const fixture = JSON.parse(await fs.readFile(new URL('./fixtures/task-record.json', import.meta.url), 'utf8'));
assert.equal(schemas[202](fixture), true, JSON.stringify(schemas[202].errors));
const seen = new Set();
const client = new OpenConnectClient({
  baseUrl: `${baseUrl.replace(/\/$/, '')}/openconnect/v1`,
  defaultHeaders: { Authorization: authorization },
  fetchImpl: async (url, options) => {
    const response = await fetch(url, options);
    const validate = schemas[response.status];
    if (validate) {
      const body = await response.clone().json();
      assert.equal(validate(body), true, JSON.stringify(validate.errors));
      seen.add(response.status);
    }
    return response;
  },
});

const task = await client.createTask({ clientTaskId: 'contract-task', message: 'synthetic contract prompt' }, 'contract-key');
assert.match(task.id, /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/);
assert.equal((await client.getTask(task.id)).id, task.id);
console.log('created');
await once(process.stdin, 'data');
await assert.rejects(client.createTask({ clientTaskId: 'contract-task', message: 'different prompt' }, 'contract-key'),
  (error) => error instanceof OpenConnectError && error.status === 409);
await assert.rejects(client.getTask('01ARZ3NDEKTSV4RRFFQ69G5FAZ'),
  (error) => error instanceof OpenConnectError && error.status === 404);
assert.equal((await client.cancelTask(task.id)).id, task.id);
assert.deepEqual([...seen].sort(), [202, 404, 409]);
console.log('passed');
