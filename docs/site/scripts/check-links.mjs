// Basic internal link check for the static docs site (SITE-001 stub).
// Verifies every sidebar slug resolves to a content file and no markdown
// links point at invented routes. Full lychee/link validation lands in Phase 6.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const docsDir = join(root, 'src', 'content', 'docs');

const slugs = new Set();
const walk = (dir, prefix = '') => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name.startsWith('_')) continue;
    const full = join(dir, e.name);
    if (e.isDirectory()) walk(full, `${prefix}${e.name}/`);
    else if (/\.(md|mdx)$/.test(e.name)) {
      const base = e.name.replace(/\.(md|mdx)$/, '');
      slugs.add(`${prefix}${base === 'index' && prefix === '' ? '' : base}`.replace(/\/$/, ''));
    }
  }
};
walk(docsDir);

const config = readFileSync(join(root, 'astro.config.mjs'), 'utf8');
const cfgSlugs = [...config.matchAll(/slug:\s*'([^']+)'/g)].map((m) => m[1]);
let failures = 0;
for (const s of cfgSlugs) {
  if (!slugs.has(s)) {
    console.error(`FAIL sidebar slug has no content file: ${s}`);
    failures++;
  }
}

// Markdown links to /docs/... must resolve to a known slug or the landing page.
const mdFiles = [];
const collect = (dir) => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, e.name);
    if (e.isDirectory()) collect(full);
    else if (/\.(md|mdx)$/.test(e.name)) mdFiles.push(full);
  }
};
collect(docsDir);
for (const f of mdFiles) {
  const text = readFileSync(f, 'utf8');
  for (const m of text.matchAll(/\]\((\/docs\/[^)]+)\)/g)) {
    const target = m[1].replace(/^\/docs\//, '').replace(/\/$/, '');
    if (target !== '' && !slugs.has(target)) {
      console.error(`FAIL ${f}: broken internal link ${m[1]}`);
      failures++;
    }
  }
}

if (failures > 0) {
  console.error(`${failures} link check failure(s)`);
  process.exit(1);
}
console.log(`OK: ${cfgSlugs.length} sidebar slugs resolve; ${mdFiles.length} content files link-checked.`);
