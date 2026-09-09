// Assembles the walkthrough document from the capture manifest: a self-contained HTML page (images
// inlined) and a PDF of it. The PDF is one page per step, each page as tall as its content — the
// screenshots stay at full, readable width instead of being squeezed into A4 — with a clickable
// table of contents.
//
//   node build-doc.mjs
//
// Output: .local/tutorial-walkthrough/InfraPortal-tutorial-walkthrough.html and .pdf

import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from 'playwright';
import { PDFDocument, PDFName, PDFString, PDFNumber } from 'pdf-lib';
import { readManifest, outDir, imagesDir } from './lib.mjs';

const manifest = readManifest();

const PARTS = {
  0: { title: 'Before you start', blurb: 'The three actors, how to sign in, what the audience is looking at.' },
  1: { title: 'What is deployed where', blurb: 'The read-only half of the portal: overview, matrix, deployment detail, service page, artifacts, analytics. Shown as the plain user.' },
  2: { title: 'Moving a version to production', blurb: 'The core process: QA signs off tickets, the release manager approves, the pipeline deploys and the promotion closes itself. Plus the audit feed and what pipelines send.' },
  3: { title: 'When it goes wrong: rollbacks', blurb: 'QA raises a rollback, the release manager approves, automation performs it.' },
  4: { title: 'Telling people what shipped', blurb: 'Release notes generated from deploy events, and webhooks into the channels people read.' },
  5: { title: 'Running the platform', blurb: 'The Settings tour: environments and aliases, promotion and rollback policies, roles, feature flags, service products, maintenance — and what a plain user cannot do.' },
};

// Escapes for text and for attribute values alike — captions end up in alt attributes.
const esc = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
const img64 = (file) => `data:image/png;base64,${readFileSync(join(imagesDir, file)).toString('base64')}`;
const roleClass = (role) => ({ admin: 'admin', qa: 'qa', user: 'user', terminal: 'terminal', none: 'none' })[role] ?? 'none';

const builtAt = new Date(manifest.builtAt);
const dateLabel = builtAt.toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
const byPart = new Map();
for (const s of manifest.steps) {
  if (!byPart.has(s.part)) byPart.set(s.part, []);
  byPart.get(s.part).push(s);
}
const parts = [...byPart.entries()].sort((a, b) => a[0] - b[0]);

const scenesTable = Object.entries(manifest.scenes ?? {}).map(([k, v]) => {
  const d = v.data ?? {};
  const where = d.service
    ? `${d.service} ${d.version ?? ''}${d.sourceEnv ? ` (${d.sourceEnv} → ${d.targetEnv})` : d.environment ? ` → ${d.environment}` : ''}${d.toVersion ? ` back to ${d.toVersion}` : ''}`
    : (d.url ?? d.target ? `→ ${d.url ?? d.target}` : (d.environment ? `${manifest.hero} → ${d.environment}` : ''));
  return `<tr><td><b>${esc(k)}</b></td><td>${esc(v.title.replace(/^[A-Z]\.\s*/, ''))}</td><td class="mono">${esc(where)}</td></tr>`;
}).join('');

const CSS = `
  :root { --ink: #0f172a; --muted: #64748b; --line: #e2e8f0; --accent: #4f46e5; --admin: #b91c1c; --qa: #b45309; --user: #1d4ed8; --terminal: #0f172a; }
  * { box-sizing: border-box; }
  body { margin: 0; font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; color: var(--ink); background: #fff; }
  .page { width: 1123px; padding: 40px 48px 56px; margin: 0 auto; position: relative; background: #fff; }
  .cover { min-height: 760px; display: flex; flex-direction: column; justify-content: center; }
  .cover h1 { font-size: 40px; margin: 0 0 8px; letter-spacing: -0.5px; }
  .cover .sub { font-size: 20px; color: var(--muted); margin-bottom: 28px; }
  .meta { color: var(--muted); font-size: 14px; }
  .actors { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px; margin: 22px 0; }
  .actor { border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
  .actor h3 { margin: 0 0 4px; font-size: 16px; }
  .actor .cred { font-family: ui-monospace, Consolas, monospace; font-size: 13px; color: var(--muted); }
  .actor p { margin: 6px 0 0; font-size: 13.5px; color: #334155; }
  h2.part { font-size: 30px; margin: 0 0 6px; }
  .blurb { color: var(--muted); font-size: 17px; max-width: 900px; }
  .toc { columns: 2; column-gap: 28px; margin-top: 10px; }
  .toc a { display: block; color: var(--ink); text-decoration: none; padding: 3px 0; border-bottom: 1px dotted var(--line); font-size: 14px; break-inside: avoid; }
  .toc a span { color: var(--muted); margin-right: 6px; font-variant-numeric: tabular-nums; }
  .toc a:hover { color: var(--accent); }
  .step-head { display: flex; align-items: baseline; gap: 14px; margin-bottom: 10px; }
  .step-head h3 { font-size: 22px; margin: 0; }
  .num { color: var(--muted); font-size: 14px; font-variant-numeric: tabular-nums; }
  .badge { font-size: 12px; font-weight: 700; letter-spacing: 0.04em; text-transform: uppercase; padding: 3px 9px; border-radius: 999px; color: #fff; background: #64748b; margin-left: auto; white-space: nowrap; }
  .badge.admin { background: var(--admin); } .badge.qa { background: var(--qa); } .badge.user { background: var(--user); } .badge.terminal { background: var(--terminal); }
  .side { display: grid; grid-template-columns: 1fr 1fr; gap: 18px; margin-bottom: 14px; }
  .side h4 { margin: 0 0 4px; font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); }
  .side p { margin: 0; font-size: 14px; }
  .say { border-left: 4px solid var(--accent); background: #f5f3ff; padding: 10px 12px; border-radius: 0 8px 8px 0; font-style: italic; }
  .tip { background: #fffbeb; border: 1px solid #fde68a; padding: 8px 10px; border-radius: 8px; font-size: 13px; margin-top: 10px; }
  .shots figure { margin: 0 0 12px; }
  .shots img { width: 100%; display: block; border: 1px solid var(--line); border-radius: 8px; box-shadow: 0 1px 3px rgba(15,23,42,0.08); }
  .shots figcaption { font-size: 13px; color: var(--muted); margin-top: 5px; }
  .footer { display: flex; justify-content: space-between; font-size: 11.5px; color: var(--muted); margin-top: 10px; border-top: 1px solid var(--line); padding-top: 8px; }
  table.scenes { border-collapse: collapse; width: 100%; font-size: 13.5px; margin-top: 8px; }
  table.scenes td { border-bottom: 1px solid var(--line); padding: 5px 8px; vertical-align: top; }
  .mono { font-family: ui-monospace, Consolas, monospace; font-size: 12.5px; }
  .callout-legend { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; color: var(--muted); margin-top: 10px; }
  .callout-legend i { display: inline-block; width: 22px; height: 22px; border-radius: 50%; background: #e11d48; color: #fff; font: 700 13px/22px system-ui; text-align: center; font-style: normal; }
  @media screen { body { background: #f1f5f9; } .page { margin: 14px auto; box-shadow: 0 2px 12px rgba(15,23,42,0.08); border-radius: 6px; } }
`;

// ── Sections (each becomes one PDF page) ─────────────────────────────────────────────────────
const sections = [];
const footer = (left, no) => `<div class="footer"><span>${esc(left)}</span><span>${no}</span></div>`;

sections.push({ id: 'cover', html: `<section class="page cover" id="cover">
  <h1>InfraPortal tutorial — walkthrough</h1>
  <div class="sub">Every screen of the presentation, annotated, with what you see and what the presenter says.</div>
  <div class="actors">
    ${(manifest.accounts ?? []).map((a) => `<div class="actor"><h3>${esc(a.role)} — ${esc(a.name)}</h3><div class="cred">${esc(a.email)} / ${esc(a.password)}</div><p>${
      a.role === 'Admin' ? 'Release manager and platform owner: approves promotions and rollbacks, owns Settings and Webhooks.'
      : a.role === 'QA' ? 'Signs off work items, raises issues, may raise a rollback. Has "My tasks" and the work-items queue.'
      : 'Reads deployments, artifacts and analytics. No queue, no approvals, no Settings.'}</p></div>`).join('')}
  </div>
  <p>The environment is a copy of the live InfraPortal (${esc(manifest.source ?? 'the source instance')}) with a scripted storyline staged on the <b>${esc(manifest.hero)}</b> product. The scenes referenced throughout:</p>
  <table class="scenes">${scenesTable}</table>
  <div class="callout-legend"><i>1</i> Numbered red callouts mark what to point at on each screen; the numbers are referenced in the text.</div>
  <p class="meta" style="margin-top:22px">Generated ${esc(dateLabel)} by <span class="mono">scripts/tutorial/capture</span> against a database built by <span class="mono">scripts/tutorial/seed-tutorial.ps1</span>. Rebuild both to refresh. Contains names from the live data — internal use only.</p>
</section>` });

let toc = '';
for (const [p, steps] of parts) {
  toc += `<a href="#part-${p}"><span>Part ${p}</span><b>${esc(PARTS[p]?.title ?? '')}</b></a>`;
  for (const s of steps) toc += `<a href="#step-${p}-${s.n}"><span>${p}.${s.n}</span>${esc(s.title)}</a>`;
}
sections.push({ id: 'contents', html: `<section class="page" id="contents"><h2 class="part">Contents</h2><div class="toc">${toc}</div>${footer('InfraPortal tutorial — walkthrough', 'Contents')}</section>` });

let pageNo = 2;
for (const [p, steps] of parts) {
  pageNo += 1;
  sections.push({ id: `part-${p}`, html: `<section class="page" id="part-${p}" style="min-height:560px;display:flex;flex-direction:column;justify-content:center">
    <div class="num" style="font-size:18px">Part ${p}</div><h2 class="part">${esc(PARTS[p]?.title ?? '')}</h2><p class="blurb">${esc(PARTS[p]?.blurb ?? '')}</p>
    <div class="toc" style="columns:1;max-width:760px;margin-top:18px">${steps.map((s) => `<a href="#step-${p}-${s.n}"><span>${p}.${s.n}</span>${esc(s.title)} <span style="float:right">${esc(s.actor)}</span></a>`).join('')}</div>
  </section>` });
  for (const s of steps) {
    pageNo += 1;
    sections.push({ id: `step-${p}-${s.n}`, html: `<section class="page" id="step-${p}-${s.n}">
      <div class="step-head"><span class="num">${p}.${s.n}</span><h3>${esc(s.title)}</h3><span class="badge ${roleClass(s.role)}">${esc(s.actor)}</span></div>
      <div class="side">
        <div><h4>What you see</h4><p>${esc(s.what)}</p>${s.tip ? `<div class="tip">${esc(s.tip)}</div>` : ''}</div>
        <div><h4>Presenter says</h4><p class="say">${esc(s.say)}</p></div>
      </div>
      <div class="shots">${s.images.map((im) => `<figure><img src="${img64(im.file)}" alt="${esc(im.caption ?? s.title)}">${im.caption ? `<figcaption>${esc(im.caption)}</figcaption>` : ''}</figure>`).join('')}</div>
      ${footer(`Part ${p} — ${PARTS[p]?.title ?? ''}`, pageNo)}
    </section>` });
  }
}

const shell = (body) => `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>InfraPortal tutorial — walkthrough</title><style>${CSS}</style></head><body>${body}</body></html>`;

// ── HTML ─────────────────────────────────────────────────────────────────────────────────────
const htmlPath = join(outDir, 'InfraPortal-tutorial-walkthrough.html');
writeFileSync(htmlPath, shell(sections.map((s) => s.html).join('\n')), 'utf8');

// ── PDF: one page per section, sized to its content, then merged ─────────────────────────────
const PX_PER_MM = 96 / 25.4;
const PAGE_WIDTH_PX = 1123; // 297mm — the width every .page is laid out at
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: PAGE_WIDTH_PX, height: 800 } });
const merged = await PDFDocument.create();
const anchors = new Map(); // section id → merged page index, for the clickable contents
const buffers = [];
for (const s of sections) {
  await page.setContent(shell(s.html), { waitUntil: 'load' });
  const height = await page.evaluate(() => Math.ceil(document.querySelector('.page').getBoundingClientRect().height));
  const buf = await page.pdf({ width: `${PAGE_WIDTH_PX / PX_PER_MM}mm`, height: `${(height + 2) / PX_PER_MM}mm`, printBackground: true, margin: { top: '0', bottom: '0', left: '0', right: '0' }, pageRanges: '1' });
  anchors.set(s.id, buffers.length);
  buffers.push(buf);
}
await browser.close();

const pages = [];
for (const buf of buffers) {
  const doc = await PDFDocument.load(buf);
  const [copied] = await merged.copyPages(doc, [0]);
  pages.push(merged.addPage(copied));
}
// Chromium drops in-document anchors when pages are printed one at a time, so navigation in the PDF
// is the outline (bookmarks panel): every part, with its steps nested under it.
const outline = sections.map((s) => {
  const isStep = s.id.startsWith('step-');
  const title = s.id === 'cover' ? 'Cover' : s.id === 'contents' ? 'Contents'
    : s.id.startsWith('part-') ? `Part ${s.id.slice(5)} — ${PARTS[Number(s.id.slice(5))]?.title ?? ''}`
    : (() => { const [, p, n] = s.id.match(/^step-(\d+)-(\d+)$/); const st = manifest.steps.find((x) => x.part === Number(p) && x.n === Number(n)); return `${p}.${n} ${st?.title ?? ''}`; })();
  return { title, pageIndex: anchors.get(s.id), isChild: isStep };
});
addOutline(merged, outline);
const pdfPath = join(outDir, 'InfraPortal-tutorial-walkthrough.pdf');
writeFileSync(pdfPath, await merged.save());

console.log(`HTML: ${htmlPath}\nPDF:  ${pdfPath} (${pages.length} pages)\n${manifest.steps.length} steps, ${manifest.steps.reduce((n, s) => n + s.images.length, 0)} images`);

// ── PDF outline (bookmarks). pdf-lib has no high-level API for it, so the /Outlines tree is built by
// hand: top-level items for cover, contents and parts, the steps nested under their part.
function addOutline(doc, items) {
  if (items.length === 0) return;
  const { context } = doc;
  const outlinesRef = context.nextRef();
  const refs = items.map(() => context.nextRef());
  const top = [];
  items.forEach((it, i) => {
    if (it.isChild && top.length > 0) top[top.length - 1].children.push(i);
    else top.push({ index: i, children: [] });
  });
  const dictFor = (i, parentRef, prevRef, nextRef, firstRef, lastRef, count) => {
    const it = items[i];
    const pageRef = doc.getPage(it.pageIndex).ref;
    const d = context.obj({ Title: PDFString.of(it.title), Parent: parentRef, Dest: context.obj([pageRef, 'XYZ', null, null, null]) });
    if (prevRef) d.set(PDFName.of('Prev'), prevRef);
    if (nextRef) d.set(PDFName.of('Next'), nextRef);
    if (firstRef) d.set(PDFName.of('First'), firstRef);
    if (lastRef) d.set(PDFName.of('Last'), lastRef);
    if (count) d.set(PDFName.of('Count'), PDFNumber.of(count));
    return d;
  };
  top.forEach((t, ti) => {
    const prev = ti > 0 ? refs[top[ti - 1].index] : undefined;
    const next = ti < top.length - 1 ? refs[top[ti + 1].index] : undefined;
    const first = t.children.length ? refs[t.children[0]] : undefined;
    const last = t.children.length ? refs[t.children[t.children.length - 1]] : undefined;
    context.assign(refs[t.index], dictFor(t.index, outlinesRef, prev, next, first, last, t.children.length));
    t.children.forEach((ci, k) => {
      const cprev = k > 0 ? refs[t.children[k - 1]] : undefined;
      const cnext = k < t.children.length - 1 ? refs[t.children[k + 1]] : undefined;
      context.assign(refs[ci], dictFor(ci, refs[t.index], cprev, cnext));
    });
  });
  context.assign(outlinesRef, context.obj({ Type: 'Outlines', First: refs[top[0].index], Last: refs[top[top.length - 1].index], Count: items.length }));
  doc.catalog.set(PDFName.of('Outlines'), outlinesRef);
  doc.catalog.set(PDFName.of('PageMode'), PDFName.of('UseOutlines'));
}
