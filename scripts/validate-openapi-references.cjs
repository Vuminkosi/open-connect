'use strict';

const { readFileSync, realpathSync, statSync } = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const { parse } = createRequire(path.join(__dirname, '../packages/client-ts/package.json'))('yaml');

function within(root, target) {
    const relative = path.relative(root, target);
    return relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

function validate(openApi, contractsRoot) {
    const root = realpathSync(contractsRoot);
    const documents = new Map();

    function load(target) {
        if (!within(root, target)) throw new Error('Reference leaves the contracts directory.');
        const real = realpathSync(target);
        if (!within(root, real) || !statSync(real).isFile()) {
            throw new Error('Reference is not a file within the contracts directory.');
        }
        if (!documents.has(real)) {
            const content = readFileSync(real, 'utf8');
            documents.set(real, path.extname(real).toLowerCase() === '.json'
                ? JSON.parse(content)
                : parse(content));
        }
        return documents.get(real);
    }

    const document = load(openApi);
    const pending = [document];
    const seen = new Set();
    while (pending.length) {
        const node = pending.pop();
        if (!node || typeof node !== 'object' || seen.has(node)) continue;
        seen.add(node);

        if (Object.hasOwn(node, '$ref')) {
            const reference = node.$ref;
            try {
                if (typeof reference !== 'string') throw new Error('Reference must be a string.');
                const separator = reference.indexOf('#');
                const file = decodeURIComponent(separator < 0 ? reference : reference.slice(0, separator));
                const pointer = decodeURIComponent(separator < 0 ? '' : reference.slice(separator + 1));
                if (file.startsWith('/') || /[:\\?\u0000-\u001f\u007f]/u.test(file)) {
                    throw new Error('Only local relative reference paths are supported.');
                }
                let value = file ? load(path.resolve(path.dirname(openApi), file)) : document;
                if (pointer && !pointer.startsWith('/')) throw new Error('Expected a JSON Pointer.');
                for (const token of pointer ? pointer.slice(1).split('/') : []) {
                    if (/~(?![01])/.test(token)) throw new Error('Invalid JSON Pointer escape.');
                    const key = token.replace(/~1/g, '/').replace(/~0/g, '~');
                    if (!value || typeof value !== 'object'
                        || (Array.isArray(value) && !/^(0|[1-9][0-9]*)$/.test(key))
                        || !Object.hasOwn(value, key)) {
                        throw new Error('JSON Pointer target does not exist.');
                    }
                    value = value[key];
                }
            } catch (error) {
                throw new Error(`OpenAPI reference does not resolve: ${JSON.stringify(reference)}. ${error.message}`);
            }
        }

        for (const value of Object.values(node)) pending.push(value);
    }
}

try {
    validate(path.resolve(process.argv[2]), path.resolve(process.argv[3]));
} catch (error) {
    console.error(error.message);
    process.exitCode = 1;
}
