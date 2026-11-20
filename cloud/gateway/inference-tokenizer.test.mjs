import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHmac } from 'node:crypto';
import { createLocalInferenceTokenizer } from './inference-tokenizer.mjs';

const model = { path: '/models/model.gguf', id: 'model', revision: 'revision', digest: 'sha256:' + 'a'.repeat(64),
    format: 'chatml', licenseAccepted: true, artifactReviewed: true, contextTokens: 512, threads: 2 };
const deployment = { modelRevision: model.revision, modelDigest: model.digest };
const childProgram = `
const readline = require('node:readline'); const crypto = require('node:crypto');
console.log(JSON.stringify({status:'ready'}));
readline.createInterface({input:process.stdin}).on('line', line => {
  const request = JSON.parse(line); const payload = Buffer.from(request.payload, 'base64'); const input = JSON.parse(payload);
  if (input.modelId === 'hang') return;
  console.log(JSON.stringify({requestId:input.requestId,modelId:input.modelId,maxOutputTokens:input.input.maxTokens,
    inputTokenUpperBound:7,workerPayloadFingerprint:crypto.createHmac('sha256',Buffer.from(request.key,'base64')).update(payload).digest('base64')}));
});`;
const start = (command, args, options) => {
    assert.equal(options.env.OPENCONNECT_CLOUD_MODEL_DIGEST, model.digest);
    assert.equal(options.env.OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED, 'true');
    return spawn(process.execPath, ['-e', childProgram], { stdio: options.stdio });
};
const body = (modelId = 'model') => Buffer.from(JSON.stringify({ requestId: '11111111-1111-1111-1111-111111111111', modelId,
    input: { maxTokens: 5 } }));

test('local tokenizer uses an isolated child and reuses it for bounded matching quotes', async () => {
    let launches = 0;
    const tokenizer = createLocalInferenceTokenizer({ python: '/python', pythonPath: '/app', model }, (...args) => { launches++; return start(...args); });
    try {
        const payload = body(); const key = Buffer.alloc(32, 2);
        for (let i = 0; i < 2; i++) {
            const result = await tokenizer.quote({ payload, key, deployment });
            assert.equal(result.inputTokenUpperBound, 7);
            assert.equal(result.workerPayloadFingerprint, createHmac('sha256', key).update(payload).digest('base64'));
        }
        assert.equal(launches, 1);
        assert.equal(await tokenizer.quote({ payload, key, deployment: { ...deployment, modelRevision: 'wrong' } }), null);
    } finally { tokenizer.dispose(); }
});

test('local tokenizer fails closed on cancellation, contention, timeout and invalid configuration', async () => {
    assert.throws(() => createLocalInferenceTokenizer({ python: '/python', pythonPath: '/app', model: { ...model, artifactReviewed: false } }));
    const tokenizer = createLocalInferenceTokenizer({ python: '/python', pythonPath: '/app', model, quoteTimeoutMs: 50 }, start);
    try {
        const key = Buffer.alloc(32); const running = tokenizer.quote({ payload: body('hang'), key, deployment });
        assert.equal(await tokenizer.quote({ payload: body(), key, deployment }), null);
        assert.equal(await running, null);
        const controller = new AbortController(); controller.abort();
        assert.equal(await tokenizer.quote({ payload: body(), key, deployment, signal: controller.signal }), null);
    } finally { tokenizer.dispose(); }
});

test('cancellation interrupts an active tokenizer process', async () => {
    const tokenizer = createLocalInferenceTokenizer({ python: '/python', pythonPath: '/app', model }, start);
    try {
        const controller = new AbortController();
        const pending = tokenizer.quote({ payload: body('hang'), key: Buffer.alloc(32), deployment, signal: controller.signal });
        setTimeout(() => controller.abort(), 100);
        assert.equal(await pending, null);
        const result = await tokenizer.quote({ payload: body(), key: Buffer.alloc(32), deployment });
        assert.equal(result.inputTokenUpperBound, 7);
    } finally { tokenizer.dispose(); }
});
