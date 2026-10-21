// Global teardown for the screenshot pass (playwright.screenshots.config.ts):
// writes ui-review/index.html, a local gallery over the PNGs the run left.
//
// Plain relative <img> tags, no script and no external reference — the page has
// to render from file:// on a machine with the network off, because reviewing
// the set must never mean uploading it (app/ui-review/README.md).
//
// Untyped .mjs beside generate-types.mjs rather than a spec-dir .ts: the app's
// tsconfig has no node types, and this is node-side tooling, not app code.

import { readdir, stat, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const dirUrl = new URL("../ui-review/", import.meta.url);

const escape = (value) =>
  value.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

async function collect() {
  const groups = new Map();
  for (const file of await readdir(dirUrl)) {
    const match = /^(.+)-(\d+)\.png$/.exec(file);
    if (!match) continue;
    const { mtimeMs } = await stat(new URL(file, dirUrl));
    const shots = groups.get(match[1]) ?? [];
    shots.push({ file, width: Number(match[2]), mtime: mtimeMs });
    groups.set(match[1], shots);
  }
  return groups;
}

function render(groups) {
  // Ordered by capture time, not alphabetically: the run walks the screens in
  // the order a recorder does.
  const sections = [...groups.entries()]
    .map(([screen, shots]) => ({ screen, shots: shots.sort((a, b) => a.width - b.width) }))
    .sort((a, b) => Math.min(...a.shots.map((s) => s.mtime)) - Math.min(...b.shots.map((s) => s.mtime)))
    .map(
      ({ screen, shots }) => `  <section>
    <h2>${escape(screen)}</h2>
    <div class="shots">
${shots
  .map(
    (shot) => `      <figure>
        <a href="${escape(shot.file)}"><img src="${escape(shot.file)}" alt="${escape(screen)} at ${shot.width}px" loading="lazy"></a>
        <figcaption>${escape(shot.file)}</figcaption>
      </figure>`,
  )
  .join("\n")}
    </div>
  </section>`,
    )
    .join("\n");

  const total = [...groups.values()].reduce((n, shots) => n + shots.length, 0);
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>UI review captures</title>
<style>
  :root { color-scheme: light dark; --bg: #fff; --fg: #111; --muted: #666; --line: #d6d6d6; --card: #fafafa; }
  @media (prefers-color-scheme: dark) {
    :root { --bg: #16181c; --fg: #e9e9ea; --muted: #9aa0a6; --line: #33363b; --card: #1e2126; }
  }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 2rem 1.5rem 4rem; background: var(--bg); color: var(--fg);
         font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
  header { border-bottom: 1px solid var(--line); padding-bottom: 1rem; margin-bottom: 2rem; }
  h1 { font-size: 1.35rem; margin: 0 0 .35rem; }
  p { margin: 0; color: var(--muted); font-size: .875rem; }
  h2 { font-size: 1rem; margin: 0 0 .75rem; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
  section { margin-bottom: 2.5rem; }
  .shots { display: flex; flex-wrap: wrap; gap: 1.25rem; align-items: flex-start; }
  /* Fixed column, not intrinsic width: an 820 capture sized by its own content
     is wider than the row and would wrap away from its 390 twin. */
  figure { flex: 0 1 420px; max-width: 100%; margin: 0; background: var(--card);
           border: 1px solid var(--line); border-radius: 6px; padding: .5rem; }
  img { display: block; width: 100%; height: auto; border-radius: 3px; }
  figcaption { margin-top: .5rem; color: var(--muted); font-size: .75rem;
               font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
</style>
</head>
<body>
<header>
  <h1>UI review captures</h1>
  <p>${total} capture${total === 1 ? "" : "s"} across ${groups.size} screen${groups.size === 1 ? "" : "s"} &middot; generated ${escape(new Date().toLocaleString())} &middot; local only, never uploaded</p>
</header>
${sections}
</body>
</html>
`;
}

export default async function writeGallery() {
  const groups = await collect();
  const out = new URL("index.html", dirUrl);
  await writeFile(out, render(groups), "utf8");
  console.log(`\nUI review gallery: ${fileURLToPath(out)} (open it in a browser)`);
}
