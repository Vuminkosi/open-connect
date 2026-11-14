#!/usr/bin/env node
/**
 * OpenConnect LiveUI compile-time Razor inventory (v1).
 * This intentionally recognizes only statically expressed controls. It does
 * not claim to discover business methods, conditional state or permissions.
 * Usage: node build/scripts/compile-liveui.mjs samples/Blazor/Components samples/Blazor/wwwroot/liveui.manifest.json
 */
import { readFile, readdir, mkdir, writeFile } from "node:fs/promises";
import { resolve, join, dirname, relative, basename } from "node:path";
import { createHash } from "node:crypto";

if (!process.argv[2] || !process.argv[3]) {\n  process.stderr.write("Usage: openconnect-liveui-compile <razor-components-dir> <manifest-output-file>\\n");\n  process.exit(2);\n}\nconst sourceRoot = resolve(process.argv[2]);
const outputPath = resolve(process.argv[3]);
const slug = text => text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 64);
const files = new Map();
async function scan(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const file = join(directory, entry.name);
    if (entry.isDirectory()) await scan(file);
    else if (entry.isFile() && entry.name.endsWith(".razor")) {
      files.set(basename(file, ".razor"), { file, source: await readFile(file, "utf8") });
    }
  }
}
await scan(sourceRoot);

function extractTags(source) {
  const out = [];
  const tagPattern = /<([A-Za-z][A-Za-z0-9_.]*)\b([^<>]*?)\/?\s*>/g;
  let match;
  while ((match = tagPattern.exec(source)) !== null) {
    const attributes = {};
    for (const attr of match[2].matchAll(/([\w-]+)\s*=\s*"([^"]*)"/g))
      attributes[attr[1]] = attr[2];
    out.push({ name: match[1], attributes });
  }
  return out;
}

function discover(component, visited = new Set()) {
  if (visited.has(component)) return [];
  const record = files.get(component);
  if (!record) return [];
  visited.add(component);
  const controls = [];
  for (const tag of extractTags(record.source)) {
    const { name, attributes: a } = tag;
    if ((name === "MudTabPanel" || name === "TabPanel") && a.Text && !a.Text.startsWith("@")) {
      controls.push({ kind: "tab", label: a.Text, locator: { role: "tab", text: a.Text },
        actions: ["highlight", "activate"] });
    } else if ((name === "MudTextField" || name === "input") && (a.Placeholder || a.placeholder)) {
      const placeholder = a.Placeholder ?? a.placeholder;
      if (placeholder && !placeholder.startsWith("@")) {
        controls.push({ kind: "input", label: placeholder, locator: { css: "input", placeholder },
          actions: ["highlight", "setValue"] });
      }
    }
    if (files.has(name) && name !== component) controls.push(...discover(name, visited));
  }
  return controls;
}

const routes = [];
for (const [name, record] of files) {
  const match = record.source.match(/@page\s+"([^"]+)"/);
  if (!match) continue;
  const path = match[1];
  const title = record.source.match(/<PageTitle>([^<]+)<\/PageTitle>/)?.[1]?.trim()
    ?? record.source.match(/<h[1-3][^>]*>([^<]+)<\/h[1-3]>/i)?.[1]?.trim()
    ?? name;
  const seen = new Map();
  const targets = discover(name).map(control => {
    const base = slug(control.kind + "-" + control.label) || control.kind;
    const count = (seen.get(base) ?? 0) + 1;
    seen.set(base, count);
    return { id: count === 1 ? base : `${base}-${count}`, ...control };
  });
  routes.push({ id: slug(path) || "home", path, title, targets });
}
routes.sort((a, b) => a.path.localeCompare(b.path));
const manifest = { schemaVersion: "openconnect.liveui.v1", source: "razor-static-analysis",
  sourceHash: createHash("sha256").update([...files.values()].map(x => x.source).sort().join("\n")).digest("hex"),
  routes };
await mkdir(dirname(outputPath), { recursive: true });
await writeFile(outputPath, JSON.stringify(manifest, null, 2) + "\n");
process.stdout.write(`LiveUI: compiled ${routes.length} routes, ${routes.reduce((sum, r) => sum + r.targets.length, 0)} controls -> ${relative(process.cwd(), outputPath)}\n`);
