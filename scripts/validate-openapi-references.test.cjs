'use strict';

const { mkdtempSync, mkdirSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const assert = require('node:assert/strict');
const { test } = require('node:test');

const entries = [
    ['valid local, escaped, array, root and relative targets',
        ['#/components/A', '#/components/slash~1tilde~0%20key/0', '#', '../jsonschema/example.json#/value', '../jsonschema/example.json'],
        null],
    ['missing or mis-cased internal key', ['#/components/a'], /JSON Pointer target does not exist/],
    ['missing relative file', ['../jsonschema/missing.json'], /ENOENT/],
    ['missing target in a relative file', ['../jsonschema/example.json#/missing'], /JSON Pointer target does not exist/],
    ['malformed escape', ['#/components/~2'], /Invalid JSON Pointer escape/],
    ['malformed array index', ['#/components/slash~1tilde~0%20key/00'], /JSON Pointer target does not exist/],
    ['external URL', ['https://example.invalid/contract.yaml#/a'], /Only local relative/],
    ['Windows absolute path', ['C:\\tmp\\contract.yaml#/a'], /Only local relative/],
    ['encoded traversal', ['%2e%2e/%2e%2e/outside.json'], /leaves the contracts directory/],
];

for (const [name, references, expected] of entries) {
    test(name, () => {
        const temp = mkdtempSync(path.join(tmpdir(), 'openconnect-openapi-'));
        try {
            const root = path.join(temp, 'contracts');
            const openapi = path.join(root, 'openapi', 'fixture.yaml');
            mkdirSync(path.dirname(openapi), { recursive: true });
            mkdirSync(path.join(root, 'jsonschema'));
            writeFileSync(path.join(root, 'jsonschema', 'example.json'), '{"value": false}');
            writeFileSync(path.join(temp, 'outside.json'), '{}');
            writeFileSync(openapi, [
                'components:',
                '  A: &self { cycle: *self }',
                '  "slash/tilde~ key": [false]',
                'references:',
                ...references.map(ref => '  - $ref: ' + JSON.stringify(ref)),
            ].join('\n'));
            const result = spawnSync(process.execPath, [
                path.join(__dirname, 'validate-openapi-references.cjs'), openapi, root,
            ], { encoding: 'utf8', timeout: 5000 });
            assert.ifError(result.error);
            if (expected) {
                assert.equal(result.status, 1, result.stderr);
                assert.match(result.stderr, expected);
            } else {
                assert.equal(result.status, 0, result.stderr);
            }
        } finally {
            rmSync(temp, { recursive: true });
        }
    });
}
