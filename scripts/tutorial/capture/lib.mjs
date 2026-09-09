// Shared plumbing for the walkthrough capture: signing the browser in as one of the demo actors,
// drawing annotations on the live page before a screenshot, rendering terminal output as an image,
// and talking to the local API.
//
// Everything here reads .local/tutorial-scenes.json, which seed-tutorial.ps1 writes at the end of a
// run — the ids of the staged scenes change on every reseed, so nothing is hardcoded.

import { chromium } from 'playwright';
import { readFileSync, mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = dirname(fileURLToPath(import.meta.url));
export const repoRoot = join(here, '..', '..', '..');
export const stateDir = join(repoRoot, '.local');
export const outDir = join(stateDir, 'tutorial-walkthrough');
export const imagesDir = join(outDir, 'images');

export const scenes = JSON.parse(readFileSync(join(stateDir, 'tutorial-scenes.json'), 'utf8'));
export const webBase = scenes.webBase ?? 'http://localhost:5173';
export const apiBase = scenes.apiBase ?? 'http://localhost:5259';

export const VIEWPORT = { width: 1440, height: 900 };

// ── API ──────────────────────────────────────────────────────────────────────────────────────

export async function api(method, path, { token, apiKey, body } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (apiKey) headers['X-Api-Key'] = apiKey;
  const res = await fetch(`${apiBase}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = text; }
  return { status: res.status, body: json, text };
}

const tokens = new Map();

/** JWT for one of the demo accounts (admin | qa | user), cached for the run. */
export async function tokenFor(role) {
  if (tokens.has(role)) return tokens.get(role);
  const account = scenes.accounts.find((a) => a.role.toLowerCase() === role.toLowerCase());
  if (!account) throw new Error(`No account with role ${role} in tutorial-scenes.json`);
  const password = process.env[`TUTORIAL_${role.toUpperCase()}_PASSWORD`] ?? account.password;
  const res = await api('POST', '/api/auth/login', { body: { email: account.email, password } });
  if (res.status !== 200) throw new Error(`Login as ${account.email} failed: HTTP ${res.status}`);
  tokens.set(role, res.body.token);
  return res.body.token;
}

export function accountFor(role) {
  return scenes.accounts.find((a) => a.role.toLowerCase() === role.toLowerCase());
}

// ── Browser ──────────────────────────────────────────────────────────────────────────────────

export async function launch() {
  return chromium.launch({ headless: true });
}

/**
 * A browser context signed in as `role`. The token goes into localStorage under the key the web app
 * reads (src/Platform.Web/src/lib/localAuth.ts), the theme is pinned to light for print, and the
 * viewport is the one every screenshot uses.
 */
export async function actorContext(browser, role) {
  const token = await tokenFor(role);
  const context = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: 1, colorScheme: 'light' });
  await context.addInitScript(([t]) => {
    window.localStorage.setItem('platform_auth_token', t);
    window.localStorage.setItem('theme-mode', 'light');
    // Reduce motion so screenshots never catch a half-finished transition.
    const style = document.createElement('style');
    style.textContent = '*, *::before, *::after { transition-duration: 0s !important; animation-duration: 0s !important; }';
    document.addEventListener('DOMContentLoaded', () => document.head.appendChild(style));
  }, [token]);
  return context;
}

/** Navigate and wait until the page has stopped fetching. */
export async function go(page, path, { settle = 800 } = {}) {
  await page.goto(path.startsWith('http') ? path : `${webBase}${path}`, { waitUntil: 'domcontentloaded' });
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(settle);
}

// ── Annotations ──────────────────────────────────────────────────────────────────────────────

/**
 * Draws numbered callouts on the live page. Each mark names a target — a Playwright locator, or a
 * `box` {x,y,width,height} in viewport pixels — and gets a red outline, a numbered badge, and an
 * optional label with an arrow. Marks live in a fixed overlay so they scroll with nothing and never
 * change layout; `clearMarks` removes them again.
 *
 * mark: { target: Locator | {x,y,width,height}, label?: string, shape?: 'box'|'circle',
 *         labelAt?: 'right'|'left'|'above'|'below', pad?: number }
 */
export async function drawMarks(page, marks, { anchor } = {}) {
  // Pass 1: make sure every target exists and has rendered (lazy lists render on scroll).
  const live = [];
  for (const m of marks) {
    if (m.target && typeof m.target.boundingBox === 'function') {
      const locator = m.target.first();
      try {
        await locator.waitFor({ state: 'visible', timeout: 4000 });
        await locator.scrollIntoViewIfNeeded();
        live.push({ ...m, locator });
      } catch {
        console.warn(`  ! annotation target not found: ${m.label ?? '(unlabelled)'}`);
      }
    } else {
      live.push({ ...m, locator: null });
    }
  }
  // Pass 2: settle the scroll position — the anchor in view, else everything back at the top — and
  // only then measure, so no box is stale by the time the screenshot is taken. The app scrolls its
  // <main> internally, so every scroller is reset, not just the window.
  if (anchor) {
    await anchor.first().scrollIntoViewIfNeeded().catch(() => {});
  } else {
    await page.evaluate(() => {
      window.scrollTo(0, 0);
      for (const el of document.querySelectorAll('*')) { if (el.scrollTop) el.scrollTop = 0; if (el.scrollLeft) el.scrollLeft = 0; }
    });
  }
  await page.waitForTimeout(250);
  const viewport = page.viewportSize();
  const { sx, sy } = await page.evaluate(() => ({ sx: window.scrollX, sy: window.scrollY }));
  const resolved = [];
  for (const m of live) {
    let box = m.locator ? await m.locator.boundingBox() : m.target;
    if (!box) { console.warn(`  ! annotation target has no box: ${m.label ?? '(unlabelled)'}`); continue; }
    if (m.locator && (box.y + box.height < 0 || box.y > viewport.height || box.x + box.width < 0 || box.x > viewport.width)) {
      console.warn(`  ! annotation target outside the viewport, skipped: ${m.label ?? '(unlabelled)'}`);
      continue;
    }
    if (m.locator) box = { x: box.x + sx, y: box.y + sy, width: box.width, height: box.height };
    resolved.push({ ...m, target: undefined, locator: undefined, box });
  }
  await page.evaluate((items) => {
    document.getElementById('__tutorial-marks')?.remove();
    const NS = 'http://www.w3.org/2000/svg';
    const docW = Math.max(document.documentElement.scrollWidth, window.innerWidth);
    const docH = Math.max(document.documentElement.scrollHeight, window.innerHeight);
    const svg = document.createElementNS(NS, 'svg');
    svg.id = '__tutorial-marks';
    Object.assign(svg.style, { position: 'absolute', left: '0', top: '0', width: `${docW}px`, height: `${docH}px`, pointerEvents: 'none', zIndex: '2147483647', overflow: 'visible' });
    svg.setAttribute('width', docW);
    svg.setAttribute('height', docH);
    const RED = '#e11d48';
    const el = (name, attrs, text) => {
      const n = document.createElementNS(NS, name);
      for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
      if (text !== undefined) n.textContent = text;
      return n;
    };
    const defs = el('defs', {});
    const marker = el('marker', { id: '__tutorial-arrow', viewBox: '0 0 10 10', refX: '9', refY: '5', markerWidth: '7', markerHeight: '7', orient: 'auto-start-reverse' });
    marker.appendChild(el('path', { d: 'M 0 0 L 10 5 L 0 10 z', fill: RED }));
    defs.appendChild(marker);
    svg.appendChild(defs);

    items.forEach((m, i) => {
      const pad = m.pad ?? 6;
      const b = m.box;
      const x = b.x - pad, y = b.y - pad, w = b.width + pad * 2, h = b.height + pad * 2;
      if ((m.shape ?? 'box') === 'circle') {
        svg.appendChild(el('ellipse', { cx: x + w / 2, cy: y + h / 2, rx: w / 2 + 6, ry: h / 2 + 6, fill: 'none', stroke: RED, 'stroke-width': '3' }));
      } else {
        svg.appendChild(el('rect', { x, y, width: w, height: h, rx: '8', ry: '8', fill: 'none', stroke: RED, 'stroke-width': '3' }));
      }
      const bx = x - 4, by = y - 4;
      svg.appendChild(el('circle', { cx: bx, cy: by, r: '13', fill: RED }));
      svg.appendChild(el('text', { x: bx, y: by + 5, 'text-anchor': 'middle', fill: '#fff', 'font-size': '14', 'font-weight': '700', 'font-family': 'system-ui, sans-serif' }, String(i + 1)));

      if (m.label) {
        const at = m.labelAt ?? (x + w + 280 < docW ? 'right' : (y > 90 ? 'above' : 'below'));
        const gap = 46;
        let lx, ly, ax1, ay1, ax2, ay2, anchor = 'start';
        if (at === 'right') { ax2 = x + w + 4; ay2 = y + h / 2; ax1 = ax2 + gap; ay1 = ay2; lx = ax1 + 6; ly = ay1 + 5; }
        else if (at === 'left') { ax2 = x - 4; ay2 = y + h / 2; ax1 = ax2 - gap; ay1 = ay2; lx = ax1 - 6; ly = ay1 + 5; anchor = 'end'; }
        else if (at === 'above') { ax2 = x + w / 2; ay2 = y - 4; ax1 = ax2; ay1 = ay2 - gap; lx = ax1; ly = ay1 - 8; anchor = 'middle'; }
        else { ax2 = x + w / 2; ay2 = y + h + 4; ax1 = ax2; ay1 = ay2 + gap; lx = ax1; ly = ay1 + 18; anchor = 'middle'; }
        svg.appendChild(el('line', { x1: ax1, y1: ay1, x2: ax2, y2: ay2, stroke: RED, 'stroke-width': '3', 'marker-end': 'url(#__tutorial-arrow)' }));
        const text = el('text', { x: lx, y: ly, 'text-anchor': anchor, fill: RED, 'font-size': '15', 'font-weight': '700', 'font-family': 'system-ui, sans-serif' }, m.label);
        svg.appendChild(text);
        let tb = text.getBBox();
        // Keep the label on the page: a label centred above a control near the right edge would
        // otherwise run out of the screenshot. Slide it back in and let the arrow stay where it is.
        const overflowRight = tb.x + tb.width + 8 - docW;
        const overflowLeft = 8 - tb.x;
        if (overflowRight > 0) { text.setAttribute('x', lx - overflowRight); tb = text.getBBox(); }
        else if (overflowLeft > 0) { text.setAttribute('x', lx + overflowLeft); tb = text.getBBox(); }
        const plate = el('rect', { x: tb.x - 6, y: tb.y - 4, width: tb.width + 12, height: tb.height + 8, rx: '5', fill: 'rgba(255,255,255,0.94)', stroke: RED, 'stroke-width': '1.5' });
        svg.insertBefore(plate, text);
      }
    });
    document.body.appendChild(svg);
  }, resolved);
  await page.waitForTimeout(100);
  return resolved.map((r) => r.box);
}

export async function clearMarks(page) {
  await page.evaluate(() => document.getElementById('__tutorial-marks')?.remove());
}

// ── Screenshots ──────────────────────────────────────────────────────────────────────────────

mkdirSync(imagesDir, { recursive: true });

let counter = 0;
/** Saves the current viewport (or `fullPage`) as images/NN-name.png and returns the file name. */
export async function shot(page, name, { fullPage = false, clip } = {}) {
  counter += 1;
  const file = `${String(counter).padStart(2, '0')}-${name}.png`;
  await page.screenshot({ path: join(imagesDir, file), fullPage, clip, animations: 'disabled', caret: 'hide' });
  return file;
}

// ── Terminal cards ───────────────────────────────────────────────────────────────────────────

function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/**
 * Renders "what the presenter's terminal shows" as an image: the command as typed, then its output.
 * Long JSON is pretty-printed by the caller; this only lays it out.
 */
export async function terminalCard(browser, name, { title = 'Terminal', command, output, width = 1100 }) {
  const context = await browser.newContext({ viewport: { width, height: 200 }, deviceScaleFactor: 1 });
  const page = await context.newPage();
  const html = `<!doctype html><html><head><meta charset="utf-8"><style>
    body { margin: 0; background: #fff; }
    .win { background: #0f172a; color: #e2e8f0; font: 14px/1.5 ui-monospace, Consolas, "Cascadia Mono", Menlo, monospace; border-radius: 10px; overflow: hidden; }
    .bar { background: #1e293b; padding: 8px 14px; display: flex; gap: 8px; align-items: center; color: #94a3b8; font: 13px system-ui, sans-serif; }
    .dot { width: 12px; height: 12px; border-radius: 50%; display: inline-block; }
    .body { padding: 14px 18px; white-space: pre-wrap; word-break: break-all; }
    .prompt { color: #34d399; }
    .cmd { color: #f8fafc; }
    .out { color: #cbd5e1; display: block; margin-top: 10px; }
  </style></head><body><div class="win">
    <div class="bar"><span class="dot" style="background:#ef4444"></span><span class="dot" style="background:#f59e0b"></span><span class="dot" style="background:#22c55e"></span><span style="margin-left:8px">${escapeHtml(title)}</span></div>
    <div class="body"><span class="prompt">$ </span><span class="cmd">${escapeHtml(command)}</span>${output ? `<span class="out">${escapeHtml(output)}</span>` : ''}</div>
  </div></body></html>`;
  await page.setContent(html);
  const win = page.locator('.win');
  counter += 1;
  const file = `${String(counter).padStart(2, '0')}-${name}.png`;
  await win.screenshot({ path: join(imagesDir, file) });
  await context.close();
  return file;
}

// ── Manifest ─────────────────────────────────────────────────────────────────────────────────

/** The capture's record of what it produced; build-doc.mjs turns it into the document. */
export function writeManifest(manifest) {
  writeFileSync(join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2), 'utf8');
}

export function readManifest() {
  const p = join(outDir, 'manifest.json');
  if (!existsSync(p)) throw new Error(`No manifest at ${p} — run capture.mjs first`);
  return JSON.parse(readFileSync(p, 'utf8'));
}

/** Shell-ready curl for a JSON POST with the pipeline API key — printed in terminal cards. */
export function curlFor(path, body) {
  return `curl -s -X POST ${apiBase}${path} -H 'X-Api-Key: ${scenes.apiKey}' -H 'Content-Type: application/json' -d '${JSON.stringify(body)}'`;
}

export function pretty(json) {
  return typeof json === 'string' ? json : JSON.stringify(json, null, 2);
}
