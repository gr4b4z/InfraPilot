// Captures the annotated screenshots for the tutorial walkthrough document, following
// docs/tutorial/presentation-guide.md part by part and role by role. Run against a freshly seeded
// environment (scripts/tutorial/seed-tutorial.ps1) with the web dev server up; the live steps in
// Part 2/3 change state (approve, deploy, roll back), so a second run needs a reseed first.
//
//   node capture.mjs            # everything
//   PARTS=1,2 node capture.mjs  # a subset, for iterating on annotations
//
// Output: .local/tutorial-walkthrough/images/*.png and manifest.json; build-doc.mjs assembles them.

import { spawn } from 'node:child_process';
import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  launch, actorContext, go, drawMarks, clearMarks, shot, terminalCard, api, tokenFor, curlFor,
  pretty, scenes, writeManifest, outDir, stateDir, repoRoot, VIEWPORT,
} from './lib.mjs';

const sc = scenes.scenes;
const hero = scenes.heroProduct;
const prodEnv = scenes.prodEnv ?? 'prod';
const heroEnvs = scenes.heroEnvs ?? ['dev', 'test', 'stable', 'staging', 'prod'];
// Scene ids come from the seed's tutorial-scenes.json; the pages they live on are the app's routes.
const promoUrl = (d) => `/promotions/${d.id}`;
const eventUrl = (d) => `/deployments/events/${d.id}`;
const noteUrl = (d) => `/release-notes/${hero}/${d.id}`;
const webhookUrl = (d) => `/webhooks/${d.id}`;
const only = (process.env.PARTS ?? '').split(',').map((s) => s.trim()).filter(Boolean).map(Number);
const wantPart = (n) => only.length === 0 || only.includes(n);

const manifest = { builtAt: new Date().toISOString(), hero, source: scenes.source ?? null, scenes: sc, accounts: scenes.accounts, steps: [] };
const record = (entry) => { manifest.steps.push(entry); console.log(`  ✓ ${entry.part}.${entry.n} ${entry.title}`); };

const browser = await launch();
const ctx = {};
for (const role of ['admin', 'qa', 'user']) ctx[role] = await actorContext(browser, role);
const pages = {};
for (const role of ['admin', 'qa', 'user']) pages[role] = await ctx[role].newPage();
const P = (role) => pages[role];
const actorName = (role) => ({ admin: 'Admin — Anna Admin', qa: 'QA — Karol QA', user: 'User — Ula User', terminal: 'Terminal', none: 'Signed out' })[role];

let stepNo = 0;
let currentPart = 0;
function part(n) { currentPart = n; stepNo = 0; }
/** One walkthrough step: screenshot(s) + the two texts. `images` = [{file, caption?}]. */
function step({ title, actor, images, what, say, tip }) {
  stepNo += 1;
  record({ part: currentPart, n: stepNo, title, actor: actorName(actor), role: actor, images, what, say, tip: tip ?? null });
}

/** Screenshot with marks, then clear them. `tall` grows the viewport for long pages. */
async function annotatedShot(page, name, marks, { tall, clip, anchor, clipAround } = {}) {
  if (tall) await page.setViewportSize({ width: VIEWPORT.width, height: tall });
  await page.waitForTimeout(200);
  const boxes = await drawMarks(page, marks, { anchor });
  // clipAround: crop to a band of the page around the first mark (height in px).
  if (clipAround && boxes[0]) {
    const b = boxes[0];
    const y = Math.max(0, b.y + b.height / 2 - clipAround / 2);
    clip = { x: 240, y, width: VIEWPORT.width - 240, height: Math.min(clipAround, (tall ?? VIEWPORT.height) - y) };
  }
  const file = await shot(page, name, { clip });
  await clearMarks(page);
  if (tall) await page.setViewportSize(VIEWPORT);
  return file;
}

const nav = (page, label) => page.getByRole('link', { name: label, exact: true }).first();
const heading = (page, text) => page.getByRole('heading', { name: text }).first();
const text = (page, t, exact = false) => page.getByText(t, { exact }).first();
const button = (page, name) => page.getByRole('button', { name }).first();

// ── Webhook listener (runs for the whole capture so live actions produce deliveries) ──────────
const listenerLog = join(outDir, 'webhook-listener.log');
writeFileSync(listenerLog, '', 'utf8');
const listener = spawn('pwsh', ['-NoProfile', '-File', join(repoRoot, 'scripts', 'tutorial', 'webhook-listener.ps1')], { stdio: ['ignore', 'pipe', 'pipe'] });
listener.stdout.on('data', (d) => writeFileSync(listenerLog, d, { flag: 'a' }));
listener.stderr.on('data', (d) => writeFileSync(listenerLog, d, { flag: 'a' }));
const listenerOutput = () => readFileSync(listenerLog, 'utf8').replace(/\x1b\[[0-9;]*m/g, '');
/** The listener's output without the JSON bodies: one line per delivery plus its event/header lines. */
const listenerSummary = () => {
  const lines = listenerOutput().split(/\r?\n/).filter((l) => /^Listening|^\d{2}:\d{2}:\d{2}\s|^\s+(event:|X-Webhook-|X-Hub-Signature)/.test(l));
  const capped = lines.slice(0, 60);
  if (lines.length > 60) capped.push(`… ${lines.length - 60} more lines`);
  return capped.join('\n');
};

try {
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 0 — Before you start
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(0)) {
    part(0);
    const anon = await browser.newContext({ viewport: VIEWPORT, colorScheme: 'light' });
    await anon.addInitScript(() => window.localStorage.setItem('theme-mode', 'light'));
    const page = await anon.newPage();
    await go(page, '/');
    const img = await annotatedShot(page, 'login', [
      { target: page.getByPlaceholder('admin@localhost'), label: 'E-mail of the demo account' },
      { target: text(page, 'Dev accounts'), label: 'The three accounts and their passwords', labelAt: 'left' },
    ]);
    step({
      title: 'Sign in as one of the three actors', actor: 'none',
      images: [{ file: img }],
      what: 'The local tutorial environment uses e-mail + password sign-in. The card under the form lists the demo accounts: admin@localhost (Admin), qa@localhost (QA) and user@localhost (plain User). Production uses Entra ID instead; everything else in the portal behaves the same.',
      say: 'Everything you will see today is a copy of our real InfraPortal data, rebuilt locally this morning. I will switch between three people: Anna, the release manager and platform admin; Karol from QA; and Ula, a developer who just reads. Watch how the same portal changes shape for each of them.',
      tip: 'Open three browser profiles (or two private windows) so switching roles is instant.',
    });
    await anon.close();
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 1 — What is deployed where (User)
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(1)) {
    part(1);
    const page = P('user');

    await go(page, '/');
    let img = await annotatedShot(page, 'user-landing', [
      { target: heading(page, 'Deployments'), label: 'A plain user lands here' },
      { target: page.locator('aside, nav').first().getByText('Deployments', { exact: true }), label: 'No "My Tasks", no Settings, no Webhooks in this menu' },
      { target: page.locator('button, a').filter({ hasText: /^mpt$/ }).first(), label: 'Product chips: what this user chooses to see', labelAt: 'above' },
    ]);
    step({
      title: 'The Deployments overview, as a plain user', actor: 'user',
      images: [{ file: img }],
      what: 'One row per product, one column per environment in pipeline order (Dev → Test → Stable → Staging → Prod). Each cell says how many services are deployed and how fresh the newest deploy is. A plain user lands here; QA and Admin land on "My tasks". The product chips are a personal preference the API applies everywhere, so a user hides the products they do not care about.',
      say: 'This is the front door. Eight products, every environment we deploy to, and how recently each one moved. Ula only cares about mpt, so she can switch the other chips off — that filter follows her onto every page.',
    });

    await go(page, `/deployments/${hero}`);
    const failed = text(page, sc.F.data.version);
    img = await annotatedShot(page, 'matrix', [
      { target: page.getByRole('button', { name: 'State', exact: true }), label: 'State / Activity / Compare views', labelAt: 'above' },
      { target: page.locator('th', { hasText: /^Prod/ }).first() },
      { target: page.locator('th', { hasText: /^Service/ }).first(), label: 'One row per service', labelAt: 'below' },
    ]);
    const imgRow = await annotatedShot(page, 'matrix-failed-row', [
      { target: failed, label: `Red cell: the failed deploy (scene F)`, labelAt: 'left' },
    ], { anchor: failed, clipAround: 320 });
    step({
      title: 'The service × environment matrix for mpt', actor: 'user',
      images: [{ file: img, caption: 'The top of the matrix' }, { file: imgRow, caption: `Further down: ${sc.F.data.service} — the failed deploy on ${sc.F.data.environment}` }],
      what: 'Every service of the product in rows (3), every environment in columns in pipeline order and in the colours set in Settings (2), the current version in each cell with its age. A version that differs between Staging and Prod is exactly what a promotion moves. A red cell is a failed deploy; a small pill under a version shows the version the environment is being promoted to. Arrow keys move across the matrix; "/" searches the services on this page.',
      say: `Here is mpt itself. Read a row left to right and you see a version travelling through the pipeline. This red one — ${sc.F.data.service} on Test — failed an hour ago. Let's open it.`,
    });

    await go(page, eventUrl(sc.F.data));
    img = await annotatedShot(page, 'event-detail', [
      { target: text(page, 'This deployment failed'), label: 'Why it failed, from the pipeline run', labelAt: 'right' },
      { target: text(page, 'PIPELINE OUTPUT'), label: 'The Helm output the pipeline captured', labelAt: 'right' },
      { target: page.locator('main').getByText('Work items', { exact: true }).or(page.locator('main').getByText('WORK ITEMS', { exact: true })), label: 'The change this deploy carried', labelAt: 'below' },
      { target: page.locator('main').getByText('Roll back', { exact: true }), label: 'Straight to a rollback', labelAt: 'below' },
    ]);
    step({
      title: 'Deployment detail: the failed deploy with its logs', actor: 'user',
      images: [{ file: img }],
      what: 'The failure banner quotes the pipeline\'s own reason and links to the run. "Pipeline output" is the Helm log the pipeline posted with the event, error lines highlighted — the migration hook hit a lock timeout. On the right: the promotions this version is part of, the work items it carries (TUT-101), and the neighbouring deploys of the same service. "Roll back" opens the rollback flow pre-filled.',
      say: 'Nobody has to go digging in the CI system. The pipeline sent its output along with the event, so the reason is right here: the database migration hook timed out on a lock. The ticket behind the change is linked, and if this were production the rollback button is one click away.',
    });

    await go(page, `/deployments/${hero}/${sc.F.data.service}`);
    img = await annotatedShot(page, 'service-page', [
      { target: page.locator('main').getByText(/^failed$/i).first(), label: 'Where it runs right now — Test is red', labelAt: 'below' },
      { target: text(page, 'Release timeline', true), label: 'Every deploy of the last month, per environment', labelAt: 'right' },
      { target: page.locator('main').getByText(/^approved$/i).first(), label: 'A promotion of this service, approved, waiting for its deploy', labelAt: 'right' },
      { target: button(page, 'Deploy an artifact'), label: 'Promote a registered build from here', labelAt: 'left' },
    ], { tall: 1300 });
    step({
      title: 'The service page', actor: 'user',
      images: [{ file: img }],
      what: 'One page per service: a card per environment with the current version and status, a release timeline (one dot per deploy, red for failures), the open promotions, and the recent distinct versions with the environments each one reached. "Full history" is the flat list of every deploy.',
      say: `"How is ${sc.F.data.service} doing?" is answered on one screen: Test is red, Staging and Stable carry the version that is about to go to Prod, and there is an approved promotion waiting for its deploy. We will come back to that one.`,
    });

    await go(page, `/artifacts?product=${hero}`);
    img = await annotatedShot(page, 'artifacts', [
      { target: page.getByPlaceholder(/Search product/), label: 'Free text across every column', labelAt: 'below' },
      { target: page.locator('th, [role=columnheader]').filter({ hasText: /^Deployed$/ }).first(), label: 'Where each build landed', labelAt: 'below' },
      { target: page.locator('th, [role=columnheader]').filter({ hasText: /^Branch$/ }).first(), label: 'Any branch, not only main', labelAt: 'below' },
    ]);
    step({
      title: 'Artifacts: the build registry', actor: 'user',
      images: [{ file: img }],
      what: 'Every build the pipelines registered, newest first, from any branch — with the commit, and the environments the same version was deployed to. A build is a fact about CI; a deployment is a fact about an environment; this page joins them. Filters offer the products, services and branches the registry actually holds.',
      say: 'Builds and deployments are two different things and we keep them apart on purpose. This is the registry: what CI produced, from which branch, and which environments already run it. A feature-branch build shows up here long before it is deployed anywhere.',
    });

    await go(page, '/analytics');
    const select = page.locator('select').first();
    await select.selectOption(hero).catch(() => {});
    await page.waitForLoadState('networkidle').catch(() => {});
    await page.waitForTimeout(800);
    img = await annotatedShot(page, 'analytics', [
      { target: select, label: 'Per product', labelAt: 'left' },
      { target: text(page, /Change failure rate/), label: 'Failures + rollbacks over deploys', labelAt: 'above' },
      { target: text(page, /Deployments per day/), label: 'Cadence per environment (hover for the split)', labelAt: 'right' },
      { target: text(page, /Stories × environments/), label: 'Which tickets reached which environment', labelAt: 'right' },
    ], { tall: 1300 });
    step({
      title: 'Analytics', actor: 'user',
      images: [{ file: img }],
      what: 'Deploy frequency, change-failure rate, approval and lead-time percentiles (p50/p75/p90, never averages), a "shipped this period" list and the story × environment matrix. Every tile states its definition, and warnings say how much of the data carries a story reference — a number without its coverage is not shown as a fact.',
      say: 'Managers ask "are we shipping, and is it hurting?". This page answers with percentiles and with the coverage printed next to them. When a third of the deploys carry no ticket reference, the page says so instead of pretending.',
    });
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 2 — Moving a version to production (QA, then Admin, then the terminal)
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(2)) {
    part(2);
    const qa = P('qa');
    const admin = P('admin');
    const A = sc.A.data, B = sc.B.data, C = sc.C.data, D = sc.D.data, E = sc.E.data;

    // QA: My tasks
    await go(qa, '/my-tasks');
    let img = await annotatedShot(qa, 'qa-my-tasks', [
      { target: nav(qa, /My Tasks/), label: 'Everything waiting on Karol', labelAt: 'right' },
      { target: qa.getByRole('button', { name: /Assigned to me/ }), label: 'Work items assigned to Karol', labelAt: 'below' },
      { target: qa.getByRole('button', { name: /^Promotions/ }), label: 'Promotions Karol may approve (the staging edges)', labelAt: 'below' },
    ]);
    step({
      title: 'QA lands on "My tasks"', actor: 'qa',
      images: [{ file: img }],
      what: 'The inbox: promotions the signed-in person may approve, work items assigned to them, and work items nobody is assigned to. Read-only by design — each row links to the page where the decision is made, next to the diff and the discussion. The bell in the top bar carries the same total.',
      say: 'Karol starts the day here. Seven promotions to staging are waiting for a QA approval, and thirty-two tickets are assigned to him for sign-off before they can go to production.',
    });

    // QA: work items queue
    await go(qa, '/me/work-items');
    img = await annotatedShot(qa, 'qa-queue', [
      { target: qa.getByRole('button', { name: /Assigned to me/ }), label: 'Karol\'s tickets', labelAt: 'below' },
      { target: qa.getByRole('button', { name: /Not assigned/ }), label: 'Tickets whose policy wants a QA and has none', labelAt: 'below' },
      { target: text(qa, 'QA: Karol QA'), label: 'The role the policy requires', labelAt: 'below' },
      { target: qa.getByRole('link', { name: /Details/ }), label: 'Open the ticket page', labelAt: 'left' },
    ]);
    step({
      title: 'The work items queue', actor: 'qa',
      images: [{ file: img }],
      what: 'Every ticket carried by an open promotion whose policy requires a QA sign-off, with its state (Pending / Approved / Issue / Blocked), the environments it is testable in, the people on it and the service it belongs to. The same Jira ticket carried by three services is three rows, because each is a different change to a different deployable.',
      say: 'Each row is one ticket in one service. MPT-23398 appears twice because two services carry it and each gets its own sign-off. The orange "assigned by" chips show the routing came from Jira; Karol is the QA on all of them because the seed put him there.',
    });

    // QA: promotion A
    await go(qa, promoUrl(A));
    img = await annotatedShot(qa, 'qa-promotion-a', [
      { target: qa.getByText(/^Pending$/).first(), label: 'Lifecycle state', labelAt: 'left' },
      { target: qa.locator('main h2').filter({ hasText: /work items/i }), label: 'The tickets this promotion carries', labelAt: 'right' },
      { target: qa.getByRole('link', { name: /Sign off & discuss/ }), label: 'Karol\'s job: sign each one off', labelAt: 'right' },
      { target: text(qa, 'Release manager'), label: 'The human gate — Anna\'s job, not Karol\'s', labelAt: 'right' },
      { target: text(qa, 'All work items resolved'), label: 'The work-item gate', labelAt: 'right' },
    ], { tall: 1150 });
    step({
      title: 'Scene A — a promotion waiting for QA sign-off', actor: 'qa',
      images: [{ file: img }],
      what: `${A.service} ${A.version}, ${A.sourceEnv} → ${A.targetEnv}. The header shows what Prod runs now and what it will run; the compare link goes to the provider's diff. The change set (work items, commits, PRs) was computed by the release pipeline and posted with the promotion. The policy for this edge has two gates: a Release manager approval, and every work item signed off. Karol can satisfy the second, not the first.`,
      say: 'A promotion is one sentence: this service, this version, from staging to prod. The pipeline that built the release computed the net change and attached it, so the tickets you see are exactly what would ship. Karol\'s part is the sign-off on each ticket. Anna\'s part comes after.',
    });

    // QA: the work item page
    const aRef = await api('GET', `/api/promotions/${A.id}`, { token: await tokenFor('qa') });
    const aItem = (aRef.body?.candidate?.sourceEventReferences ?? []).find((r) => r.type === 'work-item' && r.key);
    if (aItem) {
      await go(qa, `/work-items/${encodeURIComponent(A.service)}/${encodeURIComponent(aItem.key)}?product=${hero}&targetEnv=${A.targetEnv}`);
      img = await annotatedShot(qa, 'qa-work-item', [
        { target: qa.locator('main').getByRole('button', { name: 'Approve' }), label: 'Approve, raise an issue, or block', labelAt: 'below' },
        { target: qa.locator('main h2').filter({ hasText: /^\s*change\s*$/i }), label: 'What carried the ticket: PRs and commits', labelAt: 'below' },
        { target: qa.locator('main h2').filter({ hasText: /^\s*people\s*$/i }), label: 'Who is on it', labelAt: 'below' },
      ], { tall: 1300 });
      step({
        title: 'The work-item page', actor: 'qa',
        images: [{ file: img }],
        what: 'One ticket in one service for one target environment: its title and description from the tracker, the commits and pull requests that carried it, the people (author, reviewer, the assigned QA), the sign-off box and the comment thread. Three decisions: Approve releases the gate; Issue flags a problem without terminating anything; Block says "not going out". Only Approve counts; the other two are reversible holds.',
        say: 'This is where the actual QA work lands. Karol reads the ticket, checks it on staging, and signs it off here with a note — or raises an issue, which stalls the release without killing it. Keyboard "A" approves, "I" raises an issue, "o" opens Jira.',
      });
    }

    // QA: scene C — issue raised
    await go(qa, promoUrl(C));
    img = await annotatedShot(qa, 'qa-promotion-c', [
      { target: qa.getByText(/^Issue$/).first(), label: 'One ticket has an issue', labelAt: 'above' },
      { target: qa.getByText(/^Approved$/).first(), label: 'The other is signed off', labelAt: 'above' },
      { target: qa.getByText(/Repro still happens/).first(), label: 'Why — on the ticket, for everyone to read', labelAt: 'below' },
    ], { tall: 1000 });
    step({
      title: 'Scene C — an issue raised on a work item', actor: 'qa',
      images: [{ file: img }],
      what: `${C.service} ${C.version}: one ticket approved, one with an issue. The gate reads "1 of 2 approved · 1 with issues" and the promotion stays Pending. An issue is "something is wrong", a block is "not going out"; both are reversible and both stall the gate. Rejecting is done to the whole promotion, never to one ticket, and only by the release manager.`,
      say: 'Karol found a problem on the second ticket. He did not kill the release — he raised an issue, wrote why, and the promotion waits. When the fix arrives as a new version, the pipeline supersedes this candidate and asks again.',
    });

    // Admin: My tasks
    await go(admin, '/my-tasks');
    img = await annotatedShot(admin, 'admin-my-tasks', [
      { target: text(admin, /Promotions awaiting your approval/), label: 'Scene B: signed off by QA, waiting for Anna', labelAt: 'below' },
      { target: admin.getByRole('link', { name: /Review/ }), label: 'Open it', labelAt: 'left' },
      { target: text(admin, /Work items with nobody assigned/), label: 'Tickets the policy wants a QA on', labelAt: 'right' },
    ]);
    step({
      title: 'Admin lands on "My tasks"', actor: 'admin',
      images: [{ file: img }],
      what: 'Anna\'s inbox shows one promotion awaiting her approval: every work item on it is already signed off, so the only thing left is the release manager\'s decision. Below: tickets the policy requires a QA on but nobody has been assigned to yet — she can assign from here.',
      say: 'Now I am Anna. One thing needs her: a release to production that QA has finished with. Everything else in her list is routing — tickets that arrived without a QA, waiting to be handed to someone.',
    });

    // Admin: scene B before approval
    await go(admin, promoUrl(B));
    img = await annotatedShot(admin, 'admin-promotion-b', [
      { target: admin.getByText(/^Approved$/).first(), label: 'QA signed it off, with a note', labelAt: 'right' },
      { target: text(admin, /0 of 1 approvals/), label: 'What is missing: the release manager', labelAt: 'left' },
      { target: admin.getByText(/Approving here deploys this/).first() },
      { target: admin.getByRole('button', { name: /^Approve$/ }).or(admin.getByRole('button', { name: /^Approve/ })).first(), label: 'The decision', labelAt: 'left' },
    ], { tall: 1500 });
    step({
      title: 'Scene B — ready for the release manager', actor: 'admin',
      images: [{ file: img }],
      what: `${B.service} ${B.version}, ${B.sourceEnv} → ${B.targetEnv}. The work-item gate is green (1); the human gate shows "0 of 1 approvals" (2). The yellow notice (3) is the policy's "deploys on approval" flag: on this edge InfraPortal's gate is the only gate, so approving makes the release automation deploy the version. (Marketplace's edges say the opposite — their pipeline has an Azure DevOps approval of its own after ours.)`,
      say: 'Everything Anna needs to decide is on this page: the diff, the tickets with QA\'s notes, who else approved, and — this yellow box — what happens when she presses the button. Here it deploys. On the marketplace product it would only unblock a pipeline that has its own gate. Nobody should be surprised by a click.',
    });

    // Admin: approve B (live)
    const approveBtn = admin.getByRole('button', { name: /^Approve/ }).first();
    await approveBtn.scrollIntoViewIfNeeded();
    await approveBtn.click();
    await admin.waitForTimeout(600);
    let dialogImg = null;
    const dialog = admin.getByRole('dialog').first();
    if (await dialog.isVisible().catch(() => false)) {
      const ta = dialog.locator('textarea').first();
      if (await ta.isVisible().catch(() => false)) await ta.fill('Approved — change window open, rollback plan documented.');
      dialogImg = await annotatedShot(admin, 'admin-approve-dialog', [
        { target: dialog.getByRole('button', { name: /^Approve|Confirm/ }).first(), label: 'Confirm', labelAt: 'below' },
      ]);
      await dialog.getByRole('button', { name: /^Approve|Confirm/ }).first().click();
    } else {
      const ta = admin.locator('textarea').first();
      if (await ta.isVisible().catch(() => false)) {
        await ta.fill('Approved — change window open, rollback plan documented.');
        await admin.getByRole('button', { name: /^Approve/ }).first().click();
      }
    }
    await admin.waitForTimeout(1500);
    await admin.waitForLoadState('networkidle').catch(() => {});
    await admin.reload({ waitUntil: 'domcontentloaded' });
    await admin.waitForLoadState('networkidle').catch(() => {});
    await admin.waitForTimeout(800);
    img = await annotatedShot(admin, 'admin-promotion-b-approved', [
      { target: admin.getByText(/^Approved$/).first(), label: 'Approved — waiting for the pipeline to deploy', labelAt: 'left' },
      { target: text(admin, /All approvals met/), label: 'Gate satisfied', labelAt: 'left' },
    ], { tall: 1500 });
    const images = [{ file: img, caption: 'After approval' }];
    if (dialogImg) images.unshift({ file: dialogImg, caption: 'The approval asks for a comment' });
    // promotion.approved is held for 10 seconds (cancel window) before delivery.
    await new Promise((r) => setTimeout(r, 16000));
    const hookOut = listenerSummary();
    const hookCard = await terminalCard(browser, 'listener-promotion-approved', {
      title: 'scripts/tutorial/webhook-listener.ps1', command: '.\\scripts\\tutorial\\webhook-listener.ps1',
      output: hookOut.trim() || '(no delivery received yet — the delivery worker retries; see the webhook page)',
    });
    images.push({ file: hookCard, caption: 'The webhook listener in the presenter\'s terminal' });
    step({
      title: 'Scene B — approved (live)', actor: 'admin',
      images,
      what: 'Approving records the decision against the "Release manager" requirement, writes a system entry on the thread, sets the status to Approved and fires the promotion.approved webhook — the listener terminal prints it a second later. The release automation now deploys the version; when the deploy event for it arrives, the promotion closes as Deployed on its own.',
      say: 'Anna approves with a comment. The status flips, the thread records who and when, and — look at the terminal — the notification is already out. From here the pipeline takes over; InfraPortal will hear about the deploy the same way it hears about every deploy.',
    });

    // Terminal: land scene E via curl (live)
    const deployBody = { product: hero, service: E.service, environment: E.targetEnv, version: E.version, source: 'helm-deploy', status: 'succeeded', deployedAt: new Date().toISOString() };
    const deployCmd = curlFor('/api/deployments/events', { ...deployBody, deployedAt: '$(date -u +%Y-%m-%dT%H:%M:%SZ)' }).replace('"$(date -u +%Y-%m-%dT%H:%M:%SZ)"', "'\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"'");
    const deployRes = await api('POST', '/api/deployments/events', { apiKey: scenes.apiKey, body: deployBody });
    const deployCard = await terminalCard(browser, 'curl-deploy-e', { title: 'bash — what the pipeline posts when Helm finishes', command: deployCmd, output: pretty(deployRes.body) });
    await new Promise((r) => setTimeout(r, 2000));
    await go(admin, promoUrl(E));
    img = await annotatedShot(admin, 'admin-promotion-e-deployed', [
      { target: admin.getByText(/^Deployed$/).first(), label: 'Closed by the deploy event, not by a click', labelAt: 'left' },
      { target: text(admin, /read-only/), label: 'Terminal state — the record stays', labelAt: 'below' },
    ], { tall: 1300 });
    step({
      title: 'Scene E — the pipeline deploys, the promotion closes (live)', actor: 'terminal',
      images: [{ file: deployCard, caption: 'The deploy event, as the pipeline sends it' }, { file: img, caption: 'The approved promotion a moment later' }],
      what: `Scene E (${E.service} ${E.version}) was approved earlier and waiting for its deploy. The terminal shows exactly what the pipeline posts when Helm finishes: one JSON event with product, service, environment, version and time. The response derives the previous version. Because a succeeded deploy of that version landed on the target, the promotion is closed as Deployed, the matrix cell for Prod shows the new version, and deployment.created plus promotion.deployed webhooks go out.`,
      say: 'This is the same thing a pipeline does, typed by hand. One HTTP call: "this version is now on prod". InfraPortal matches it to the open promotion and closes it. No one marks promotions done manually — reality does.',
    });

    // Admin: scene D rejected
    await go(admin, promoUrl(D));
    img = await annotatedShot(admin, 'admin-promotion-d', [
      { target: admin.getByText(/^Rejected$/).first(), label: 'Terminal state', labelAt: 'left' },
      { target: text(admin, /read-only/), label: 'Nothing more happens here', labelAt: 'below' },
      { target: text(admin, 'APPROVAL TRAIL'), label: 'Who said no, and why', labelAt: 'right' },
    ], { tall: 1300 });
    step({
      title: 'Scene D — rejected by the release manager', actor: 'admin',
      images: [{ file: img }],
      what: `${D.service} ${D.version} was rejected with a reason. Rejection is terminal: the page is read-only, the reason sits on the approval trail and in the audit feed. The next version of the service on the same edge opens a fresh candidate — and a newer candidate automatically supersedes an older Pending one, so nothing stale lingers in the queue.`,
      say: 'Saying no is a first-class action. It stays on record, it does not block the next version, and it is visible to whoever asks "why did this not ship" a month from now.',
    });

    // Admin: promotions list
    await go(admin, '/promotions');
    img = await annotatedShot(admin, 'admin-promotions-list', [
      { target: admin.getByRole('button', { name: /Awaiting my approval/ }) },
      { target: admin.getByRole('button', { name: /Needs attention/ }) },
      { target: admin.getByRole('button', { name: /Approved · awaiting deploy/ }) },
      { target: text(admin, /work items need attention/), label: 'Tickets without a QA assigned', labelAt: 'below' },
    ]);
    step({
      title: 'The Promotions list', actor: 'admin',
      images: [{ file: img }],
      what: 'All open promotions across products, with tabs that answer the operational questions: what can I approve now (1), what is stalled by issues or missing QAs (2), what is approved and waiting for a deploy (3), what was resolved or rejected. Each card shows the edge, the versions, and the tickets — with a warning when the policy requires a QA that nobody has been assigned. Several ready candidates can be approved in bulk.',
      say: 'Twenty-eight open promotions right now. The tabs are the questions people ask: "what is waiting for me", "what is stuck". The orange warnings on the mpt-extensions card say twenty-two tickets have no QA assigned yet — a routing problem, not a quality problem, and Anna can fix it from the queue.',
    });

    // Admin: audit
    await go(admin, '/promotions/audit');
    img = await annotatedShot(admin, 'admin-audit', [
      { target: admin.getByRole('button', { name: /Approvals/ }).first(), label: 'Only approvals', labelAt: 'below' },
      { target: admin.locator('main').getByText(/^approved$/i).first() },
      { target: admin.locator('select').nth(2).or(text(admin, /Anyone/)), label: 'By person', labelAt: 'below' },
    ]);
    step({
      title: 'The promotions audit feed', actor: 'admin',
      images: [{ file: img }],
      what: 'Every action on every promotion, newest first: created, approved, rejected, deployed, ticket sign-offs, comments, people assigned. Tabs per category, filters per product, environment, person and action, and time windows. The approver names on a gate-opening row come from the recorded trail, so history stays true even if an approval is later cancelled.',
      say: '"Who approved what for prod last week?" is one filter here. This is the page you show an auditor — and the page you open yourself when something shipped and you want to know who signed it.',
    });
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 2b — the pipeline side, from the terminal (register build → deploy → promotion)
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(2)) {
    const admin = P('admin');
    const service = scenes.heroService ?? sc.E.data.service;
    const base = sc.E.data.version.match(/^(\d+)\.(\d+)\.(\d+)/);
    const version = base ? `${base[1]}.${base[2]}.${Number(base[3]) + 1}-gfeedface` : '9.9.9-gfeedface';
    const firstEnv = heroEnvs[0];
    const secondEnv = heroEnvs[1];

    const buildBody = { product: hero, service, version, branch: 'refs/heads/master', commitSha: 'feedfacefeedfacefeedfacefeedfacefeedface', buildId: '8800100', buildUrl: 'https://example.visualstudio.com/_build/results?buildId=8800100' };
    const buildRes = await api('POST', '/api/builds', { apiKey: scenes.apiKey, body: buildBody });
    const buildCard = await terminalCard(browser, 'curl-register-build', { title: 'bash — CI registers a build', command: curlFor('/api/builds', buildBody), output: pretty(buildRes.body) });

    const evBody = { product: hero, service, environment: firstEnv, version, source: 'helm-deploy', deployedAt: new Date().toISOString(), references: [{ type: 'work-item', provider: 'jira', key: 'TUT-202', title: 'Tutorial: live deploy from the terminal' }] };
    const evRes = await api('POST', '/api/deployments/events', { apiKey: scenes.apiKey, body: evBody });
    const evCard = await terminalCard(browser, 'curl-deploy-dev', { title: `bash — the pipeline deploys it to ${firstEnv}`, command: curlFor('/api/deployments/events', evBody), output: pretty(evRes.body) });

    const prBody = { product: hero, service, sourceEnv: firstEnv, targetEnv: secondEnv, version, references: [{ type: 'work-item', provider: 'jira', key: 'TUT-202', title: 'Tutorial: live deploy from the terminal' }] };
    const prRes = await api('POST', '/api/promotions', { apiKey: scenes.apiKey, body: prBody });
    const prCard = await terminalCard(browser, 'curl-open-promotion', { title: `bash — the release pipeline opens a promotion ${firstEnv} → ${secondEnv}`, command: curlFor('/api/promotions', prBody), output: pretty(prRes.body) });

    await new Promise((r) => setTimeout(r, 1500));
    await go(admin, `/artifacts?product=${hero}`);
    const artImg = await annotatedShot(admin, 'artifacts-new-build', [
      { target: text(admin, version), label: 'The build registered a second ago', labelAt: 'right' },
    ]);
    await go(admin, `/deployments/${hero}/${service}`);
    const svcImg = await annotatedShot(admin, 'service-after-live', [
      { target: text(admin, version), label: `Now on ${firstEnv}`, labelAt: 'right' },
    ]);
    step({
      title: 'What the pipelines send, end to end (live)', actor: 'terminal',
      images: [
        { file: buildCard, caption: '1. CI registers the build (idempotent on product + service + version)' },
        { file: evCard, caption: `2. The deploy to ${firstEnv}, carrying its ticket` },
        { file: prCard, caption: `3. The release pipeline opens the promotion to ${secondEnv}${prRes.status === 201 ? ' — auto-approved: that edge has no human gate' : ''}` },
        { file: artImg, caption: 'The registry, a second later' },
        { file: svcImg, caption: 'The service page, a second later' },
      ],
      what: 'Three calls, all with the pipeline API key. Registering a build puts it in the Artifacts registry. The deploy event puts the version on the matrix and records the ticket it carries. The promotion call opens a candidate for the next environment; on the dev → test edge the policy has no human gate, so it is approved immediately and only waits for the deploy. Every screen updated without a reload — the API pushes changes to open pages.',
      say: 'Nothing in InfraPortal is typed in by people except decisions. Pipelines post three kinds of facts: a build exists, a version is running somewhere, a version should move to the next environment. Everything you saw today is derived from those three.',
    });
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 3 — Rollbacks (QA raises, Admin approves)
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(3)) {
    part(3);
    const qa = P('qa');
    const admin = P('admin');
    const G = sc.G.data;

    await go(qa, '/rollbacks');
    let img = await annotatedShot(qa, 'qa-rollbacks', [
      { target: qa.locator('main span').filter({ hasText: /^\s*pending\s*$/i }).first(), label: 'Waiting for the release manager', labelAt: 'above' },
      { target: text(qa, G.toVersion), label: 'Back to the version that ran before', labelAt: 'below' },
      { target: button(qa, /New rollback/), label: 'Raise one', labelAt: 'left' },
    ]);
    step({
      title: 'Scene G — a rollback request awaiting approval', actor: 'qa',
      images: [{ file: img }],
      what: `Karol raised a rollback of ${G.service} in ${G.environment}: from ${G.fromVersion} back to ${G.toVersion}, with the reason. A rollback is in-place within one environment and only to a version that ran there before. The policy for mpt says QA may raise one and the release manager must approve; the request waits as Pending until then.`,
      say: 'Error rates doubled after the last helpdesk release. Karol does not need Anna to start the paperwork — the policy lets QA raise the rollback, and it lands in Anna\'s inbox with the reason attached.',
    });

    await button(qa, /New rollback/).click();
    await qa.waitForTimeout(700);
    img = await annotatedShot(qa, 'qa-new-rollback', [
      { target: qa.getByRole('button', { name: 'Manual', exact: true }), label: 'Pick services and versions yourself', labelAt: 'left' },
      { target: qa.getByRole('button', { name: 'Align', exact: true }), label: 'Make this env match a reference env', labelAt: 'right' },
      { target: qa.getByRole('button', { name: 'Preview', exact: true }), label: 'Dry run: what would move, what would be skipped', labelAt: 'left' },
    ]);
    step({
      title: 'Creating a rollback: manual or align', actor: 'qa',
      images: [{ file: img }],
      what: 'Two ways to describe a rollback. Manual: choose the services and the versions to return to (only versions that ran in the environment are offered). Align: make the environment look like a reference environment (for example "make prod look like staging"), optionally excluding services. Both show a dry-run preview — what would move, what would be skipped and why — before anything is submitted.',
      say: 'Manual is for one service. Align is for the bad night when several things went out together and you want prod back to what staging looks like — the preview tells you exactly what that means before you commit.',
    });
    await qa.keyboard.press('Escape').catch(() => {});

    await go(admin, '/rollbacks');
    const approve = admin.getByRole('button', { name: /^Approve/ }).first();
    img = await annotatedShot(admin, 'admin-rollback', [
      { target: approve, label: 'The release manager decides', labelAt: 'left' },
    ]);
    const before = { file: img, caption: 'Before' };
    let after = null;
    if (await approve.isVisible().catch(() => false)) {
      await approve.click();
      await admin.waitForTimeout(600);
      const dlg = admin.getByRole('dialog').first();
      if (await dlg.isVisible().catch(() => false)) {
        const ta = dlg.locator('textarea').first();
        if (await ta.isVisible().catch(() => false)) await ta.fill('Approved — roll back now, fix forward tomorrow.');
        await dlg.getByRole('button', { name: /^Approve|Confirm/ }).first().click();
      }
      await admin.waitForTimeout(1500);
      await admin.reload({ waitUntil: 'domcontentloaded' });
      await admin.waitForLoadState('networkidle').catch(() => {});
      await admin.waitForTimeout(800);
      const afterImg = await annotatedShot(admin, 'admin-rollback-approved', [
        { target: admin.locator('main span').filter({ hasText: /^\s*(approved|executing|deploying|done)\s*$/i }).first(), label: 'Approved — the pipeline performs it', labelAt: 'right' },
      ]);
      after = { file: afterImg, caption: 'After Anna approves' };
    }
    step({
      title: 'The release manager approves the rollback (live)', actor: 'admin',
      images: after ? [before, after] : [before],
      what: 'Anna sees the same request with an Approve button. Approval fires the rollback.approved webhook, which is what the release automation listens for to perform the rollback; the deploy events it then posts (flagged as rollbacks) update the matrix and the analytics. Admins can also override a gate, but that is a separate, reason-carrying action so the audit trail can tell a bypass from an approval.',
      say: 'Same pattern as promotions: a human decision, then automation. The rollback shows up in analytics as a rollback, not as a normal deploy, so the change-failure rate stays honest.',
    });

    await go(admin, '/settings/rollbacks');
    img = await annotatedShot(admin, 'settings-rollbacks', [
      { target: text(admin, /qa@localhost/).first(), label: 'Who may raise a rollback', labelAt: 'below' },
      { target: text(admin, /admin@localhost/).first(), label: 'Who approves it', labelAt: 'above' },
    ]);
    step({
      title: 'Settings → Rollbacks: who may raise, who approves', actor: 'admin',
      images: [{ file: img }],
      what: 'Per product, optionally per environment: the people or groups allowed to create a rollback and the approval steps it needs. A product with no policy at all is not enrolled — that is deliberately different from a policy with no approval step, which means "anyone listed may roll back without a gate".',
      say: 'Enrolment is explicit. No policy means nobody but an admin can even ask; an empty approval step means the people listed can roll back on their own authority. Both are choices, and the settings page makes you make them.',
    });
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 4 — Release notes and webhooks (Admin)
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(4)) {
    part(4);
    const admin = P('admin');
    const H = sc.H.data, I = sc.I.data;

    await go(admin, `/release-notes/${hero}`);
    let img = await annotatedShot(admin, 'release-notes-index', [
      { target: admin.locator('main').locator('select').first(), label: 'Pick the environment and window…', labelAt: 'below' },
      { target: button(admin, /Preview/), label: '…and preview a new note', labelAt: 'left' },
    ]);
    step({
      title: 'Release notes for a product', actor: 'admin',
      images: [{ file: img }],
      what: 'Published notes per product and environment, each covering a time window. The default window for a new note starts where the previous one ended, so a pipeline calling the generate endpoint after every release produces exactly the delta.',
      say: 'Release notes write themselves from the deploy events: which services moved, from which version to which, and the tickets, PRs and people behind each. This is the list of everything published for mpt.',
    });

    await go(admin, noteUrl(H));
    img = await annotatedShot(admin, 'release-note-detail', [
      { target: admin.getByRole('button', { name: /^rendered$/i }) },
      { target: admin.getByRole('button', { name: /^services$/i }) },
      { target: button(admin, /Copy markdown/), label: 'Paste into Teams, Confluence, an e-mail', labelAt: 'below' },
    ], { tall: 1100 });
    step({
      title: 'Scene H — a generated release note', actor: 'admin',
      images: [{ file: img }],
      what: `The last seven days to ${H.environment ?? prodEnv} for ${hero}: one section per service with the version transition, then each ticket with its PR, build and people. The toggles switch between the rendered markdown (1) and the structured per-service data it was built from (2); Copy markdown (3) takes it to Teams, Confluence or an e-mail. Rendered from a Handlebars template that admins edit (Settings → Release Notes Template), resolved product-and-environment first, then product, then the global default.`,
      say: 'Nobody wrote this. It is the week\'s deploy events rendered through a template. Copy the markdown into the release announcement, or let the webhook post it to a channel the moment it is generated.',
    });

    const draftFrom = new Date(Date.now() - 7 * 86400000).toISOString();
    const draftTo = new Date().toISOString();
    await go(admin, `/release-notes/${hero}/new?environment=${prodEnv}&from=${encodeURIComponent(draftFrom)}&to=${encodeURIComponent(draftTo)}`);
    img = await annotatedShot(admin, 'release-note-draft', [
      { target: admin.locator('textarea').first(), label: 'Edit before publishing', labelAt: 'above' },
      { target: admin.getByRole('button', { name: /Publish|Generate/ }).first(), label: 'Publish + webhook', labelAt: 'left' },
    ], { tall: 1300 });
    step({
      title: 'Drafting a release note: preview, edit, publish', actor: 'admin',
      images: [{ file: img }],
      what: 'Pick the environment and the window, preview the rendered markdown, edit it if a line needs a human touch, publish. Publishing stores the note with the structured data it was built from and fires the release_note.generated webhook.',
      say: 'The pipeline path is fully automatic; this is the human path for the same thing — when you want to add a sentence, or publish a note for a window the pipeline did not cover.',
    });

    await go(admin, '/settings/release-notes-template');
    img = await annotatedShot(admin, 'settings-release-notes-template', [
      { target: admin.locator('textarea').first(), label: 'Handlebars; fields listed in the docs', labelAt: 'above' },
    ], { tall: 1200 });
    step({
      title: 'Settings → Release Notes Template', actor: 'admin',
      images: [{ file: img }],
      what: 'The template at three scopes: global, per product, per product and environment. The editor loads exactly the row saved at the selected scope, pre-filled with the inherited template when nothing is saved there yet.',
      say: 'If your product wants a different shape — say, grouped by ticket type — you change it here, for that product only.',
    });

    await go(admin, '/webhooks');
    img = await annotatedShot(admin, 'webhooks-list', [
      { target: admin.getByRole('link', { name: /Tutorial listener/ }).or(text(admin, /Tutorial listener/)), label: 'Scene I: the seeded subscription', labelAt: 'below' },
      { target: admin.getByRole('button', { name: /New|Add|Create/ }).first(), label: 'Teams, Discord, GitHub, Azure DevOps, generic', labelAt: 'left' },
    ]);
    step({
      title: 'Webhooks', actor: 'admin',
      images: [{ file: img }],
      what: 'Subscriptions that push events out: to a Microsoft Teams channel (Adaptive Card or HTML via Power Automate), Discord, a GitHub repository_dispatch, an Azure DevOps incoming hook, or any HTTPS endpoint with an HMAC-signed JSON envelope. Each picks its events and can be narrowed to products, services and environments. Messaging targets have an editable message template with live preview.',
      say: 'This is how the rest of the company hears about what happens here. Release approved → the release channel knows. Rollback approved → the on-call channel knows. Note generated → the announcement channel gets the text.',
    });

    await go(admin, webhookUrl(I));
    // Live: press Test so a delivery shows up, then read the listener.
    const testBtn = button(admin, /Test/);
    if (await testBtn.isVisible().catch(() => false)) {
      await testBtn.click();
      await admin.waitForTimeout(2500);
      await admin.reload({ waitUntil: 'domcontentloaded' });
      await admin.waitForLoadState('networkidle').catch(() => {});
      await admin.waitForTimeout(800);
    }
    img = await annotatedShot(admin, 'webhook-detail', [
      { target: text(admin, 'Events'), label: 'What it listens to', labelAt: 'right' },
      { target: text(admin, /Product: mpt/), label: 'Only this product', labelAt: 'right' },
      { target: testBtn, label: 'Send a test delivery', labelAt: 'below' },
      { target: text(admin, /Recent Deliveries/), label: 'Every attempt, with status and retry', labelAt: 'right' },
    ], { tall: 1200 });
    const hookOut = listenerSummary();
    const hookCard = await terminalCard(browser, 'listener-all', { title: 'scripts/tutorial/webhook-listener.ps1 — everything received during this session', command: '.\\scripts\\tutorial\\webhook-listener.ps1', output: hookOut.trim() || '(nothing received)' });
    step({
      title: 'Scene I — the subscription and its deliveries (live test)', actor: 'admin',
      images: [{ file: img, caption: 'The subscription after pressing Test' }, { file: hookCard, caption: 'What arrived in the listener during this walkthrough' }],
      what: 'The subscription\'s target, events and filters; a Test button that sends a synthetic delivery; and the delivery history with response codes and a retry for failed attempts. The terminal shows the raw deliveries the listener received during this session — the approval, the deploy, the test — each with its event type and signature header.',
      say: 'Press Test and the channel gets a message — no waiting for a real event to check the wiring. And when a delivery fails, it is listed here with the response we got, and you retry it from this page.',
    });
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════
  // Part 5 — Running the platform (Admin): the Settings tour
  // ═══════════════════════════════════════════════════════════════════════════════════════════
  if (wantPart(5)) {
    part(5);
    const admin = P('admin');
    const user = P('user');

    await go(admin, '/settings/environments');
    let img = await annotatedShot(admin, 'settings-environments', [
      { target: admin.getByPlaceholder(/prod, production/).first().or(text(admin, /production, prd, live/)), label: 'Aliases: other names pipelines use', labelAt: 'below' },
      { target: admin.locator('input[type=checkbox]:checked').first(), label: 'The production flag analytics keys off', labelAt: 'above' },
      { target: admin.locator('main').getByText('Merge environments', { exact: true }).last(), label: 'Fold history recorded under old names', labelAt: 'above' },
    ], { tall: 1300 });
    step({
      title: 'Settings → Environments', actor: 'admin',
      images: [{ file: img }],
      what: 'The canonical environments, their order (drag), colours, and which count as production. Aliases map the names pipelines actually send ("production", "prd", "live") onto one environment so three spellings stop producing three columns. Aliases affect what arrives next; "Merge environments" moves the history already recorded under the old names, with a preview of what moves and what cannot.',
      say: 'Every column and every colour you saw today came from this list. The aliases are what keeps a fleet of pipelines honest — one team says "prod", another says "production", the portal shows one column.',
    });

    await go(admin, '/settings/promotions');
    img = await annotatedShot(admin, 'settings-promotions', [
      { target: text(admin, /external approval/) },
      { target: text(admin, /auto-approve/).first() },
      { target: admin.getByText(/^QA$/).first() },
      { target: button(admin, /Add Policy/), label: 'One policy per edge', labelAt: 'right' },
    ]);
    step({
      title: 'Settings → Promotions: the policies', actor: 'admin',
      images: [{ file: img }],
      what: 'One policy per edge (product, optional service, source environment, target environment). Marketplace is marked "external approval" (1): its pipeline has an Azure DevOps gate after ours. Dev and test edges auto-approve (2). Production edges require a QA on every ticket (3). Each policy has approval steps made of requirements — a requirement is satisfied by a member of any listed group or any listed user, with a minimum number of distinct approvers; steps are combined with AND, and one person counts once. Plus the work-item flags: whether tickets are tracked, which roles each must have someone in, whether every ticket must be signed off before approval, and whether the approval deploys or a downstream gate follows.',
      say: 'This is where "who may ship what to where" lives. Dev and test are automatic. Staging wants a QA lead. Production wants the release manager and a signed-off ticket list. Marketplace is marked "external approval" because its pipeline has one more gate after ours — that is the yellow box from earlier.',
    });

    await go(admin, '/settings/roles');
    img = await annotatedShot(admin, 'settings-roles', [
      { target: admin.locator('input[value="qa"]').first() },
      { target: button(admin, /Add Role/), label: 'Adopt a role a pipeline has started sending', labelAt: 'right' },
    ]);
    step({
      title: 'Settings → Participant Roles', actor: 'admin',
      images: [{ file: img }],
      what: 'The vocabulary of roles: triggered-by, author, reviewer, qa, qa-owner, assignee, reporter. Only configured roles can be assigned by hand; pipelines may send any role and the unknown ones are flagged in the queue so an admin can decide whether to adopt them.',
      say: 'Roles are how work gets routed. "This ticket needs a QA" is a policy rule; "Karol is the QA" is an assignment; both use this list.',
    });

    await go(admin, '/settings/feature-flags');
    img = await annotatedShot(admin, 'settings-feature-flags', [
      { target: text(admin, /features\.(promotions|rollbacks|releaseNotes)/i).first(), label: 'Whole modules on or off', labelAt: 'right' },
    ]);
    step({
      title: 'Settings → Feature Flags', actor: 'admin',
      images: [{ file: img }],
      what: 'Promotions, Rollbacks, Release Notes, Analytics, the Service Catalog and Approvals can each be switched off. A disabled module disappears from the navigation and its API answers with a clear refusal, so an installation can start with deployments only and grow.',
      say: 'Not every team wants every part on day one. Start with the matrix, turn promotions on when the policies are agreed.',
    });

    await go(admin, '/settings/service-products');
    img = await annotatedShot(admin, 'settings-service-products', [
      { target: admin.getByPlaceholder('mpt-extensions').first(), label: 'Re-file a service under the right product', labelAt: 'below' },
      { target: button(admin, /Save override/), label: 'New events land there; "Move history" moves the old ones', labelAt: 'right' },
    ]);
    step({
      title: 'Settings → Service Products', actor: 'admin',
      images: [{ file: img }],
      what: 'Overrides for the product a service files under. A pipeline mid-migration often sends the old product name; an override makes new events land in the right place, and "remap" moves the history that already arrived under the wrong one.',
      say: 'When marketplace became mpt, the pipelines did not all change on the same day. This page is how the portal kept one history for each service anyway.',
    });

    await go(admin, '/settings/maintenance');
    img = await annotatedShot(admin, 'settings-maintenance', [
      { target: admin.getByRole('button', { name: /Scan for duplicates/ }).first(), label: 'Retried webhooks leave duplicate deploy events', labelAt: 'right' },
      { target: button(admin, /Check size/), label: 'Pipeline output is the biggest thing stored', labelAt: 'right' },
      { target: button(admin, /Preview changes/), label: 'Settle promotions stuck in "awaiting deploy"', labelAt: 'right' },
    ], { tall: 1400 });
    step({
      title: 'Settings → Maintenance', actor: 'admin',
      images: [{ file: img }],
      what: 'The one-off fixes an operator needs: removed (retired) services and how to restore them, scanning and removing duplicate deploy events left by pipeline retries, trimming stored pipeline logs older than a cutoff, settling promotions stranded in "awaiting deploy" against the real deploy history, orphaned work items, and re-sending approved webhooks.',
      say: 'Every long-running system needs a broom closet. Everything in here previews before it changes anything, and everything it changes is recorded in the audit log.',
    });

    // The plain user's restricted view of a promotion.
    await go(user, promoUrl(sc.C.data));
    img = await annotatedShot(user, 'user-promotion-readonly', [
      { target: text(user, 'PROMOTION APPROVAL'), label: 'Visible — but no Approve button for Ula', labelAt: 'right' },
      { target: user.locator('aside, nav').first().getByText('Promotions', { exact: true }), label: 'Ula can read, not decide', labelAt: 'right' },
    ], { tall: 1300 });
    step({
      title: 'What a plain user can and cannot do', actor: 'user',
      images: [{ file: img }],
      what: 'Ula sees every promotion, every deploy and every analytics page, and can comment — but the approve, reject, sign-off and assign controls are absent, and there is no "My tasks", Settings or Webhooks in her menu. Authorization is decided by the API from the roles in the token, so the same is true for the API itself.',
      say: 'Transparency without authority: everyone can see what is going to prod and why; only the people the policy names can make it happen.',
    });
  }
} finally {
  listener.kill();
  writeManifest(manifest);
  await browser.close();
}

console.log(`\n${manifest.steps.length} steps captured → ${outDir}`);
