// MoonWalk web UI. Vanilla ES module, no build step.
// Two live channels: /api/stream (run snapshots for the board) and /api/runs/{id}/stream (events for one run).

const view = document.getElementById('view');
const runs = new Map();
let currentRunView = null;
let onRunsChanged = null;

/* ---------------------------------------------------------------- helpers */

function h(tag, props = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) {
    if (kid == null || kid === false) continue;
    el.append(kid.nodeType ? kid : document.createTextNode(kid));
  }
  return el;
}

/** replaceChildren that skips null, undefined and false instead of printing them as text. */
function put(el, ...children) {
  el.replaceChildren(...children.flat().filter((c) => c != null && c !== false));
}

function fmtTokens(n) {
  if (n >= 1e6) return `${(n / 1e6).toFixed(1)}M`;
  if (n >= 1e3) return `${(n / 1e3).toFixed(n >= 1e4 ? 0 : 1)}k`;
  return String(n);
}

const fmtUsd = (n) => (n >= 1 ? `$${n.toFixed(2)}` : `$${n.toFixed(3)}`);

const AGENT_STAGES = ['Spec', 'Implement', 'Integrate'];
const STAGE_SHORT = { Spec: 'plan', Implement: 'implement', Integrate: 'unity' };

/**
 * The agent a stage uses or used: what it actually ran with, else the run's choice for that stage, else the run's current default.
 * (run.agent alone is only the project default, which is misleading once a stage picks another agent.)
 */
function stageAgent(run, stageName) {
  const stage = run.stages.find((s) => s.name === stageName);
  const choices = run.stageAgents ?? {};
  const key = Object.keys(choices).find((k) => k.toLowerCase() === stageName.toLowerCase());
  return stage?.agent || (key ? choices[key]?.agent : null) || run.agent || '';
}

/** "agy" when every agent stage uses one agent, otherwise "plan agy · implement muse". Empty for runs without agents (the demo). */
function agentsLine(run) {
  const parts = run.stages.filter((s) => AGENT_STAGES.includes(s.name)).map((s) => ({ stage: s.name, agent: stageAgent(run, s.name) })).filter((p) => p.agent);
  if (!parts.length) return '';
  const distinct = [...new Set(parts.map((p) => p.agent))];
  return distinct.length === 1 ? distinct[0] : parts.map((p) => `${STAGE_SHORT[p.stage]} ${p.agent}`).join(' · ');
}

/** Sums a run's usage ledger, optionally for one stage. */
function totals(run, stage) {
  const entries = (run.usage ?? []).filter((u) => !stage || u.stage === stage);
  return {
    tokens: entries.reduce((a, u) => a + (u.tokens ?? 0), 0),
    cost: entries.reduce((a, u) => a + (u.costUsd ?? 0), 0),
    hasCost: entries.some((u) => u.costUsd != null),
    models: [...new Set(entries.map((u) => (u.model ?? u.agent).replace(/^claude-/, '')))],
    attempts: entries.length,
  };
}

/** One muted line under a stage: tokens, dollars (only where the agent reports them) and the model(s) used. */
function usageLine(run, stage) {
  const t = totals(run, stage);
  if (!t.attempts) return null;
  const parts = [];
  if (t.tokens) parts.push(`${fmtTokens(t.tokens)} tokens`);
  if (t.hasCost) parts.push(fmtUsd(t.cost));
  parts.push(t.models.join(', '));
  if (t.attempts > 1) parts.push(`${t.attempts} attempts`);
  return h('div', { class: 'usage' }, parts.join(' · '));
}

const STATUS_LABEL = {
  Queued: 'Queued', Running: 'Running', WaitingForHuman: 'Needs you', Paused: 'Paused',
  Done: 'Done', Failed: 'Failed', Cancelled: 'Cancelled', Interrupted: 'Interrupted',
  Pending: 'Pending', Passed: 'Passed', Blocked: 'Blocked', NeedsHuman: 'Needs you',
};

const pill = (status) => h('span', { class: `pill s-${status.toLowerCase()}` }, STATUS_LABEL[status] ?? status);
const isActive = (s) => ['Queued', 'Running', 'Paused', 'WaitingForHuman'].includes(s);

function fmtDuration(ms) {
  if (ms < 0) ms = 0;
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, '0')}s`;
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m`;
}

const fmtClock = (t) => new Date(t).toLocaleTimeString([], { hour12: false });

function ago(t) {
  const ms = Date.now() - new Date(t).getTime();
  return ms < 5000 ? 'just now' : `${fmtDuration(ms)} ago`;
}

/** An element whose text is a live duration: data-since (+ optional data-until to freeze it). */
function duration(since, until) {
  return h('span', { 'data-since': since, 'data-until': until ?? null }, '');
}

setInterval(() => {
  for (const el of document.querySelectorAll('[data-since]')) {
    const from = new Date(el.dataset.since).getTime();
    const to = el.dataset.until ? new Date(el.dataset.until).getTime() : Date.now();
    el.textContent = fmtDuration(to - from);
  }
  for (const el of document.querySelectorAll('[data-ago]')) el.textContent = ago(el.dataset.ago);
}, 1000);

async function api(path, options) {
  const res = await fetch(path, options);
  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try { detail = (await res.json()).error ?? detail; } catch { /* no JSON body */ }
    throw new Error(detail);
  }
  return res.status === 202 || res.status === 204 ? null : res.json();
}

const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body ?? {}),
});

/** Runs an async action and shows the server's reason when it is refused. */
async function attempt(action) {
  try { await action(); } catch (err) { alert(err.message); }
}

/* ------------------------------------------------------------ global stream */

const conn = document.getElementById('conn');
const connText = document.getElementById('connText');

function setConn(state, text) {
  conn.className = `conn ${state}`;
  connText.textContent = text;
}

function startGlobalStream() {
  const es = new EventSource('/api/stream');
  es.onopen = () => setConn('live', 'live');
  es.onerror = () => setConn('down', 'reconnecting');
  es.addEventListener('run', (e) => {
    const run = JSON.parse(e.data);
    if (run.removed) {
      runs.delete(run.id);
      updateTitle();
      onRunsChanged?.(run);
      return;
    }
    const before = runs.get(run.id);
    runs.set(run.id, run);
    updateTitle();
    notifyChange(before, run);
    onRunsChanged?.(run);
  });
}

/* ------------------------------------------------------- browser notifications */

const NOTIFY_KEY = 'mw.notify';
const bell = document.getElementById('bell');

function notifySupported() { return typeof window.Notification === 'function'; }

function notifyOn() {
  try { return notifySupported() && localStorage.getItem(NOTIFY_KEY) === '1' && Notification.permission === 'granted'; } catch { return false; }
}

function renderBell() {
  const state = !notifySupported() ? 'unsupported' : Notification.permission === 'denied' ? 'blocked' : notifyOn() ? 'on' : 'off';
  bell.dataset.state = state;
  bell.title = {
    on: 'Notifications are on: you are told when a run needs you or finishes while this tab is in the background. Click to turn off.',
    off: 'Click to get a notification when a run needs you or finishes.',
    blocked: 'Notifications are blocked for this site in the browser settings.',
    unsupported: 'This browser has no notifications.',
  }[state];
  bell.setAttribute('aria-pressed', String(state === 'on'));
}

bell.addEventListener('click', async () => {
  if (!notifySupported()) { alert('This browser does not support notifications.'); return; }
  if (Notification.permission === 'denied') { alert('Notifications are blocked for this site. Allow them in the browser settings, then click the bell again.'); return; }
  try {
    if (notifyOn()) {
      localStorage.setItem(NOTIFY_KEY, '0');
    } else {
      const permission = Notification.permission === 'granted' ? 'granted' : await Notification.requestPermission();
      if (permission === 'granted') {
        localStorage.setItem(NOTIFY_KEY, '1');
        new Notification('MoonWalk notifications are on', { body: 'You will be told when a run needs you or finishes.', silent: true });
      }
    }
  } catch { /* storage or permission unavailable */ }
  renderBell();
});

/** Notifies about the moments that matter, when you are not looking at the page: a run needs you, finished, failed or was interrupted. */
function notifyChange(before, run) {
  if (!before || before.status === run.status || !notifyOn()) return;
  if (document.hasFocus() && !document.hidden) return; // you are looking at it

  let title;
  let body;
  if (run.status === 'WaitingForHuman') {
    title = run.waiting?.reason === 'Budget' ? 'Budget reached' : run.waiting?.reason === 'Review' ? 'Ready for you to play' : 'Needs you';
    body = `${run.title}\n${run.waiting?.message ?? ''}`;
  } else if (run.status === 'Done') {
    title = 'Run finished';
    body = run.title;
  } else if (['Failed', 'Interrupted'].includes(run.status)) {
    title = run.status === 'Failed' ? 'Run failed' : 'Run interrupted';
    body = `${run.title}\n${run.error ?? ''}`;
  } else {
    return;
  }

  try {
    const note = new Notification(title, { body: body.slice(0, 240), tag: run.id });
    note.onclick = () => { window.focus(); location.hash = `#/run/${encodeURIComponent(run.id)}`; note.close(); };
  } catch { /* the browser refused; nothing to do */ }
}

renderBell();

/** "(2) MoonWalk" in the browser tab while runs are waiting for you. */
function updateTitle() {
  const waiting = [...runs.values()].filter((r) => r.status === 'WaitingForHuman').length;
  document.title = waiting ? `(${waiting}) MoonWalk` : 'MoonWalk';
}

/* ------------------------------------------------------------------ router */

function route() {
  teardownRunView();
  onRunsChanged = null;
  const match = location.hash.match(/^#\/run\/(.+)$/);
  const settings = location.hash === '#/settings';
  document.getElementById('navRuns').classList.toggle('active', !match && !settings);
  document.getElementById('navSettings').classList.toggle('active', settings);
  if (match) showRun(decodeURIComponent(match[1]));
  else if (settings) showSettings();
  else showBoard();
}

/* ---------------------------------------------------------------- settings */

const badge = (cls, text) => h('span', { class: `pill ${cls}` }, text);

async function showSettings() {
  const agentsBox = h('div', { class: 'cards' }, h('div', { class: 'hint' }, 'Checking agents…'));
  const projectsBox = h('div', { class: 'cards' });
  const notes = h('div', { class: 'hint' });

  const load = async () => {
    try {
      const health = await api('/api/agents/health');
      put(agentsBox, health.agents.map((a) => h('div', { class: 'card' },
        h('div', { class: 'card-head' }, h('b', {}, a.name), h('span', { class: 'muted' }, a.kind),
          a.available ? badge('s-done', 'Available') : badge('s-failed', 'Not available')),
        h('div', { class: 'card-line' }, a.available ? (a.version ?? a.command) : a.detail),
        h('div', { class: 'card-line faint' }, `command: ${a.command}`))));
      put(notes, health.errors.map((e) => h('div', { class: 'error-box' }, `agents.json: ${e}`)));
    } catch (err) {
      put(agentsBox, h('div', { class: 'error-box' }, `Could not check agents: ${err.message}`));
    }

    try {
      const projects = await api('/api/projects');
      put(projectsBox, projects.map((p) => h('div', { class: 'card' },
        h('div', { class: 'card-head' }, h('b', {}, p.name),
          p.valid ? badge('s-done', 'Ready') : badge('s-failed', 'Problems')),
        p.name === 'demo'
          ? h('div', { class: 'card-line' }, 'Built in. Scripted stages, no repository or agent needed.')
          : h('div', { class: 'card-line' }, `${p.agent} · ${p.workspace} workspace · base ${p.baseBranch}`),
        h('div', { class: 'card-line faint' }, `stages: ${p.stages.join(' → ')}`),
        p.repoPath && h('div', { class: 'card-line faint' }, p.repoPath),
        ...(p.errors ?? []).map((e) => h('div', { class: 'card-error' }, e)))));
    } catch (err) {
      put(projectsBox, h('div', { class: 'error-box' }, `Could not load projects: ${err.message}`));
    }
  };

  const reload = h('button', {
    class: 'btn small', type: 'button',
    onclick: () => attempt(async () => {
      await post('/api/agents/reload');
      await post('/api/projects/reload');
      put(agentsBox, h('div', { class: 'hint' }, 'Checking agents…'));
      await load();
    }),
  }, 'Reload config and re-check');

  view.replaceChildren(
    h('div', { class: 'page-head' }, h('h1', {}, 'Settings'), reload),
    h('h2', { class: 'section-title' }, 'Agents'), notes, agentsBox,
    h('h2', { class: 'section-title' }, 'Projects'), projectsBox);
  load();
}

window.addEventListener('hashchange', route);

/* ------------------------------------------------------------------- board */

async function showBoard() {
  const list = h('div', { class: 'runs' });
  const sub = h('span', { class: 'sub' });
  const strip = h('div', { class: 'usage-strip' });

  /* filters: kept in boardFilter so they survive live updates and navigating away and back */
  const search = h('input', { type: 'search', class: 'filter-q', placeholder: 'Search title, id, project or brief', value: boardFilter.q, 'aria-label': 'Search runs' });
  const statusSel = h('select', { 'aria-label': 'Status' },
    [['', 'Any status'], ['active', 'Active'], ['WaitingForHuman', 'Needs you'], ['Done', 'Done'], ['Failed', 'Failed or interrupted'], ['Cancelled', 'Cancelled']]
      .map(([v, l]) => h('option', { value: v, selected: v === boardFilter.status }, l)));
  const projectSel = h('select', { 'aria-label': 'Project' });
  const archivedBox = h('input', { type: 'checkbox', checked: boardFilter.archived });
  let projectKey = '';
  const bar = h('div', { class: 'filter-bar' }, search, statusSel, projectSel, h('label', { class: 'inline' }, archivedBox, 'Show archived'));
  view.replaceChildren(h('div', { class: 'page-head' }, h('h1', {}, 'Runs'), sub), strip, bar, list);

  const matches = (r) => {
    if (!boardFilter.archived && r.archived) return false;
    if (boardFilter.project && r.project !== boardFilter.project) return false;
    const s = boardFilter.status;
    if (s === 'active' && !isActive(r.status)) return false;
    if (s === 'Failed' && !['Failed', 'Interrupted'].includes(r.status)) return false;
    if (s && s !== 'active' && s !== 'Failed' && r.status !== s) return false;
    const q = boardFilter.q.trim().toLowerCase();
    return !q || `${r.title} ${r.id} ${r.project} ${r.brief}`.toLowerCase().includes(q);
  };

  const render = () => {
    const all = [...runs.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    const names = [...new Set(all.map((r) => r.project))].sort();
    if (names.join('|') !== projectKey) {
      projectKey = names.join('|');
      put(projectSel, h('option', { value: '' }, 'Any project'),
        names.map((n) => h('option', { value: n, selected: n === boardFilter.project }, n)));
    }
    const shown = all.filter(matches);
    const active = all.filter((r) => isActive(r.status)).length;
    sub.textContent = all.length ? `${active} active · ${shown.length} of ${all.length} shown` : '';
    if (!all.length) {
      list.replaceChildren(h('div', { class: 'empty' },
        h('strong', {}, 'No runs yet'),
        'Start one with "New run" and watch the agents work live.'));
    } else if (!shown.length) {
      list.replaceChildren(h('div', { class: 'empty' }, h('strong', {}, 'No runs match'), 'Change the search or the filters above.'));
    } else {
      list.replaceChildren(...shown.map(runRow));
    }
  };

  search.addEventListener('input', () => { boardFilter.q = search.value; render(); });
  statusSel.addEventListener('change', () => { boardFilter.status = statusSel.value; render(); });
  projectSel.addEventListener('change', () => { boardFilter.project = projectSel.value; render(); });
  archivedBox.addEventListener('change', () => { boardFilter.archived = archivedBox.checked; render(); });

  try {
    for (const r of await api('/api/runs')) runs.set(r.id, r);
    updateTitle();
  } catch (err) {
    list.replaceChildren(h('div', { class: 'empty' }, h('strong', {}, 'Could not load runs'), String(err)));
    return;
  }
  render();
  loadUsage(strip, true);
  onRunsChanged = () => { render(); loadUsage(strip); };
}

const boardFilter = { q: '', status: '', project: '', archived: false };

let usageStamp = 0;

/** Tokens and dollars used today and over the last 7 days, and by agent and model. Re-read at most every few seconds. */
async function loadUsage(strip, force = false) {
  if (!force && Date.now() - usageStamp < 4000) return;
  usageStamp = Date.now();
  try {
    const u = await api('/api/usage?days=7');
    const chip = (label, tokens, cost) => h('span', { class: 'usage-chip' }, h('b', {}, label), ' ',
      [tokens ? `${fmtTokens(tokens)} tokens` : null, cost ? fmtUsd(cost) : null].filter(Boolean).join(' · ') || 'nothing yet');
    if (!u.totalTokens && !u.totalCostUsd) { strip.replaceChildren(); return; }
    put(strip,
      chip('Today', u.today.tokens, u.today.costUsd),
      chip('7 days', u.totalTokens, u.totalCostUsd),
      u.byAgent.slice(0, 5).map((a) => chip((a.model || a.agent).replace(/^claude-/, ''), a.tokens, a.costUsd)));
  } catch { /* the strip is optional */ }
}

function runRow(run) {
  const stage = run.currentStage ?? (run.status === 'Done' ? 'finished' : '');
  const t = totals(run);
  const spend = [t.tokens ? `${fmtTokens(t.tokens)} tokens` : null, t.hasCost ? fmtUsd(t.cost) : null].filter(Boolean).join(' · ');
  return h('a', { class: `run-row${run.status === 'WaitingForHuman' ? ' attn' : ''}${run.archived ? ' archived' : ''}`, href: `#/run/${encodeURIComponent(run.id)}` },
    h('div', {}, pill(run.status)),
    h('div', {}, h('div', { class: 'title' }, run.title),
      h('div', { class: 'meta' }, `${run.project} · ${run.id}${spend ? ' · ' + spend : ''}${run.archived ? ' · archived' : ''}`)),
    h('div', { class: 'meta hide-sm' }, stage),
    h('div', { class: 'num hide-sm' }, duration(run.createdAt, isActive(run.status) ? null : run.finishedAt ?? run.updatedAt)),
    h('div', { class: 'num hide-sm', 'data-ago': run.updatedAt }, ago(run.updatedAt)));
}

/* --------------------------------------------------------------- run detail */

function teardownRunView() {
  currentRunView?.close();
  currentRunView = null;
}

async function showRun(id) {
  let run = runs.get(id);
  try {
    run = await api(`/api/runs/${encodeURIComponent(id)}`);
    runs.set(id, run);
  } catch {
    view.replaceChildren(h('div', { class: 'empty' }, h('strong', {}, 'Run not found'), h('a', { href: '#/', class: 'back' }, '← Back to runs')));
    return;
  }

  let agentNames = ['claude'];
  try { agentNames = (await api('/api/agents')).agents; } catch { /* keep the default */ }

  const head = h('div', { class: 'run-head' });
  const facts = h('div', { class: 'facts' });
  const banner = h('div');
  const decisionBox = h('div');
  const stagesBox = h('div', { class: 'stages' });
  const briefBox = h('div', { class: 'brief-box' }, h('b', {}, 'Brief'), run.brief);
  const errorSlot = h('div');
  const feed = h('div', { class: 'feed', role: 'log', 'aria-live': 'off' });
  const jump = h('button', { class: 'jump', type: 'button', hidden: true }, 'Jump to latest ↓');
  const count = h('span', { class: 'count' }, '0 events');
  const tabs = h('div', { class: 'tabs', role: 'tablist' });
  const artifactView = h('pre', { class: 'artifact', hidden: true });

  // Guidance notes: the inputs persist across re-renders so typing is never lost.
  const noteInput = h('textarea', { rows: 2, placeholder: 'Add a note for the agents (applies to the next agent stage or retry)' });
  const noteList = h('div', { class: 'notes' });
  const guidanceBox = h('div', { class: 'guidance' },
    h('b', {}, 'Guidance'), noteList, noteInput,
    h('button', {
      class: 'btn small', type: 'button',
      onclick: () => attempt(async () => {
        const text = noteInput.value.trim();
        if (!text) return;
        await post(`/api/runs/${encodeURIComponent(id)}/guidance`, { text });
        noteInput.value = '';
      }),
    }, 'Add note'));

  const filters = { text: true, tool: true, system: true };
  const chips = Object.keys(filters).map((key) => {
    const chip = h('button', { class: 'chip', type: 'button', 'aria-pressed': 'true' }, { text: 'Agent text', tool: 'Tools', system: 'System' }[key]);
    chip.addEventListener('click', () => {
      filters[key] = !filters[key];
      chip.setAttribute('aria-pressed', String(filters[key]));
      feed.classList.toggle(`hide-${key}`, !filters[key]);
    });
    return chip;
  });

  const chipsBox = h('div', { class: 'chips' }, chips);

  view.replaceChildren(
    h('div', { class: 'run-head' }, h('a', { class: 'back', href: '#/' }, '← Runs')),
    head, facts, banner, decisionBox,
    h('div', { class: 'run-grid' },
      h('section', { class: 'panel side' }, h('h2', {}, 'Stages'), stagesBox, errorSlot, guidanceBox, briefBox),
      h('section', { class: 'panel' },
        h('div', { class: 'feed-bar' }, tabs, chipsBox, count),
        feed, artifactView, jump)));

  /* tabs: the live feed plus any artifact the run produced (spec, verify log, handover) */
  let activeTab = 'feed';
  let tabKey = '';
  let artifactFor = '';

  const showTab = (name, r) => {
    activeTab = name;
    const isFeed = name === 'feed';
    feed.hidden = !isFeed;
    chipsBox.hidden = !isFeed;
    count.hidden = !isFeed;
    artifactView.hidden = isFeed;
    if (!isFeed) jump.hidden = true;
    for (const t of tabs.children) t.setAttribute('aria-selected', String(t.dataset.tab === name));
    if (!isFeed) (name === 'changes' ? loadChanges : loadArtifact)(name, r?.updatedAt);
  };

  const loadArtifact = async (name, stamp) => {
    const key = `${name}@${stamp}`;
    if (artifactFor === key) return;
    artifactFor = key;
    const url = `/api/runs/${encodeURIComponent(id)}/files/${name.split('/').map(encodeURIComponent).join('/')}`;
    artifactView.classList.remove('diff');
    if (/\.(png|jpe?g|gif|webp)$/i.test(name)) {
      artifactView.replaceChildren(h('img', { class: 'shot', src: url, alt: name }), h('div', { class: 'muted shot-name' }, name));
      return;
    }
    try {
      const res = await fetch(url);
      artifactView.textContent = res.ok ? await res.text() : `Could not load ${name} (${res.status}).`;
    } catch (err) {
      artifactView.textContent = `Could not load ${name}: ${err.message}`;
    }
  };

  /** What the run changed in git, against its base branch: a file list and the colored patch. */
  const loadChanges = async (name, stamp) => {
    const key = `changes@${stamp}`;
    if (artifactFor === key) return;
    artifactFor = key;
    artifactView.classList.add('diff');
    try {
      const d = await api(`/api/runs/${encodeURIComponent(id)}/diff`);
      if (!d.available) { artifactView.textContent = d.reason ?? 'No changes to show.'; return; }
      const added = d.files.reduce((a, f) => a + f.added, 0);
      const removed = d.files.reduce((a, f) => a + f.removed, 0);
      const line = (text) => {
        const cls = text.startsWith('diff ') ? 'file'
          : /^(\+\+\+|---|index |new file|deleted file|similarity|rename|old mode|new mode|Binary files)/.test(text) ? 'meta'
          : text.startsWith('@@') ? 'hunk' : text.startsWith('+') ? 'add' : text.startsWith('-') ? 'del' : '';
        return h('div', { class: `dl ${cls}` }, text || ' ');
      };
      put(artifactView,
        h('div', { class: 'diff-head' },
          h('b', {}, `${d.files.length} file${d.files.length === 1 ? '' : 's'}`),
          h('span', { class: 'add' }, ` +${added}`), h('span', { class: 'del' }, ` -${removed}`),
          h('span', { class: 'muted' }, `  ${d.base}..${d.branch}`)),
        h('div', { class: 'diff-files' }, d.files.map((f) => h('div', { class: 'df' },
          h('span', { class: 'df-path' }, f.path),
          f.binary ? h('span', { class: 'muted' }, 'binary') : [h('span', { class: 'add' }, `+${f.added}`), h('span', { class: 'del' }, `-${f.removed}`)]))),
        d.patch ? h('div', { class: 'patch' }, d.patch.split('\n').map(line)) : h('div', { class: 'muted' }, 'No differences.'),
        d.truncated && h('div', { class: 'muted' }, '… the patch is cut off here; see the branch in git for the rest.'));
    } catch (err) {
      artifactView.textContent = `Could not load the changes: ${err.message}`;
    }
  };

  const renderTabs = (r) => {
    const names = ['feed', ...(r.branch ? ['changes'] : []), ...(r.artifacts ?? [])];
    const key = names.join('|');
    if (key !== tabKey) {
      tabKey = key;
      tabs.replaceChildren(...names.map((n) => h('button', {
        class: 'tab', type: 'button', role: 'tab', 'data-tab': n,
        onclick: () => showTab(n, runs.get(id)),
      }, n === 'feed' ? 'Live feed' : n === 'changes' ? 'Changes' : n.replace(/^(evidence|screenshots)\//, ''))));
      if (!names.includes(activeTab)) activeTab = 'feed';
    }
    showTab(activeTab, r);
  };

  /* controls and the decision card */
  let waitKey = '';

  const renderDecision = (r) => {
    const w = r.status === 'WaitingForHuman' ? r.waiting : null;
    const key = w ? `${w.stage}|${w.reason}|${r.stages.map((s) => s.status).join(',')}` : '';
    if (key === waitKey) return;
    waitKey = key;
    if (!w) { decisionBox.replaceChildren(); return; }

    const note = h('textarea', { rows: 2, placeholder: 'Optional note. It is added to the guidance and sent to the agent on retry.' });
    // After a playtest, feedback usually means changing the code again: default to Implement. Otherwise redo the stage that is waiting.
    const retryDefault = w.reason === 'Review' && r.stages.some((s) => s.name === 'Implement') ? 'Implement' : w.stage;
    const retryFrom = h('select', {}, r.stages.map((s) => h('option', { value: s.name, selected: s.name === retryDefault }, s.name)));
    const agent = h('select', {}, agentNames.map((a) => h('option', { value: a, selected: a === stageAgent(r, w.stage) }, a)));
    const decide = (action, extra = {}) => attempt(() => post(`/api/runs/${encodeURIComponent(id)}/decision`, { action, note: note.value.trim() || null, ...extra }));
    const attention = { Gate: 'Review needed', Review: 'Your turn', Failed: 'Stage failed', Blocked: 'Stage blocked', Budget: 'Budget reached' }[w.reason] ?? 'Needs you';

    decisionBox.replaceChildren(h('div', { class: `decision r-${w.reason.toLowerCase()}` },
      h('div', { class: 'decision-head' }, h('b', {}, attention), h('span', { class: 'muted' }, ` · ${w.stage}`)),
      h('div', { class: 'decision-msg' }, w.message),
      note,
      h('div', { class: 'decision-row' },
        w.canApprove && h('button', { class: 'btn primary', type: 'button', onclick: () => decide('approve') },
          r.stages[r.stages.length - 1].name === w.stage ? 'Approve and finish' : 'Approve and continue'),
        h('label', { class: 'inline' }, 'Retry from', retryFrom),
        h('label', { class: 'inline' }, 'with', agent),
        h('button', { class: 'btn', type: 'button', onclick: () => decide('retry', { retryFrom: retryFrom.value, agent: agent.value }) }, 'Retry'),
        h('button', { class: 'btn danger', type: 'button', onclick: () => decide('stop') }, 'Stop run'))));
  };

  const renderRun = (r) => {
    const live = ['Queued', 'Running', 'Paused', 'WaitingForHuman'].includes(r.status);
    const canPause = ['Queued', 'Running'].includes(r.status) && !r.pauseRequested;
    put(head,
      h('h1', {}, r.title), pill(r.status),
      r.pauseRequested && r.status === 'Running' && h('span', { class: 'muted' }, 'pausing before next stage…'),
      canPause && h('button', { class: 'btn small', type: 'button', onclick: () => attempt(() => post(`/api/runs/${encodeURIComponent(r.id)}/pause`)) }, 'Pause'),
      (r.status === 'Paused' || (r.pauseRequested && r.status === 'Running')) &&
        h('button', { class: 'btn primary small', type: 'button', onclick: () => attempt(() => post(`/api/runs/${encodeURIComponent(r.id)}/resume`)) }, r.status === 'Paused' ? 'Resume' : 'Cancel pause'),
      r.status === 'Interrupted' && h('button', { class: 'btn primary small', type: 'button', onclick: () => attempt(() => post(`/api/runs/${encodeURIComponent(r.id)}/recover`)) }, 'Recover run'),
      live && h('button', { class: 'btn danger small', type: 'button', onclick: () => cancelRun(r.id) }, 'Cancel run'),
      !live && h('button', { class: 'btn small', type: 'button', onclick: () => attempt(() => post(`/api/runs/${encodeURIComponent(r.id)}/${r.archived ? 'unarchive' : 'archive'}`)) }, r.archived ? 'Unarchive' : 'Archive'),
      !live && !r.workspaceHeld && h('button', { class: 'btn danger small', type: 'button', onclick: () => deleteRun(r) }, 'Delete'));
    guidanceBox.hidden = !live;
    noteList.replaceChildren(...(r.guidance ?? []).map((g) =>
      h('div', { class: 'note' }, h('span', { class: 'note-t' }, `${fmtClock(g.time)}${g.stage ? ' · ' + g.stage : ''}`), g.text)));
    renderDecision(r);
    renderTabs(r);
    put(facts,
      h('span', {}, 'Project ', h('b', {}, r.project)),
      h('span', {}, 'Run ', h('b', {}, r.id)),
      agentsLine(r) && h('span', {}, 'Agents ', h('b', {}, agentsLine(r))),
      (r.auto?.plan || r.auto?.implementation) && h('span', { class: 'auto-tag', title: 'This run can approve itself' },
        r.auto.plan && r.auto.implementation ? 'full auto' : r.auto.plan ? 'auto plan' : 'auto implementation'),
      r.branch && h('span', {}, 'Branch ', h('b', {}, r.branch)),
      r.commit && h('span', {}, 'Commit ', h('b', {}, r.commit)),
      totals(r).tokens > 0 && h('span', {}, 'Tokens ', h('b', {}, fmtTokens(totals(r).tokens))),
      r.costUsd != null && h('span', {}, 'Cost ', h('b', {}, `$${r.costUsd.toFixed(3)}`)),
      h('span', {}, 'Elapsed ', h('b', {}, duration(r.createdAt, isActive(r.status) ? null : r.finishedAt ?? r.updatedAt))));
    const releasable = r.workspaceHeld && !['Queued', 'Running', 'Paused', 'WaitingForHuman'].includes(r.status);
    const worktree = r.workspaceMode === 'Worktree';
    const heldText = worktree
      ? `The change lives in ${r.workDir} (branch ${r.branch}); your own working folder is untouched. ` +
        (releasable ? 'Release removes that folder; the branch stays for you to merge.' : 'When this run is finished you can release it to remove that folder.')
      : `The repository is on ${r.branch}. Play or review it there` +
        (releasable ? `, then release to switch back to ${r.returnTo}. The next run for this project waits until you do.`
          : `. When this run is finished you can release it to switch back to ${r.returnTo}.`);
    banner.replaceChildren(...(r.workspaceHeld ? [h('div', { class: 'notice' },
      h('div', {}, h('b', {}, worktree ? 'Worktree held. ' : 'Workspace held. '), heldText),
      releasable && h('button', { class: 'btn primary small', type: 'button', onclick: () => releaseRun(r.id) }, 'Release workspace'))] : []));
    stagesBox.replaceChildren(...r.stages.map((s) =>
      h('div', { class: `stage s-${s.status.toLowerCase()}` },
        h('span', { class: 'node' }),
        h('div', { class: 'name' }, h('span', {}, s.name, s.agent && h('span', { class: 'agent-tag' }, s.agent)),
          s.startedAt && h('span', { class: 'dur' }, duration(s.startedAt, s.finishedAt))),
        s.summary && h('div', { class: 'sum' }, s.summary),
        usageLine(r, s.name))));
    errorSlot.replaceChildren(...(r.error && r.status !== 'Done' ? [h('div', { class: 'error-box' }, r.error)] : []));
  };
  renderRun(run);

  /* feed */
  let follow = true;
  let lastSeq = 0;
  let total = 0;
  let openDelta = null; // the text row currently receiving deltas

  const atBottom = () => feed.scrollHeight - feed.scrollTop - feed.clientHeight < 48;
  feed.addEventListener('scroll', () => {
    follow = atBottom();
    jump.hidden = follow || activeTab !== 'feed';
  });
  jump.addEventListener('click', () => { follow = true; jump.hidden = true; feed.scrollTop = feed.scrollHeight; });

  let scrollQueued = false;
  const scrollSoon = () => {
    if (!follow || scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => { scrollQueued = false; feed.scrollTop = feed.scrollHeight; });
  };

  const closeDelta = () => { openDelta?.body.classList.remove('live-caret'); openDelta = null; };

  /* Every stage attempt gets its own collapsible group, so a retry does not bury the first attempt. */
  let group = null;
  const groups = [];

  const startGroup = (ev) => {
    const attemptNo = Number(/attempt (\d+)/.exec(ev.text ?? '')?.[1] ?? 1);
    for (const g of groups) if (g.stage === ev.stage) g.el.open = false; // fold earlier attempts of this stage
    const result = h('span', { class: 'attempt-result' }, 'running');
    const body = h('div', { class: 'attempt-body' });
    const el = h('details', { class: 'attempt', open: true },
      h('summary', {}, h('b', {}, ev.stage),
        attemptNo > 1 && h('span', { class: 'attempt-n' }, `attempt ${attemptNo}`),
        h('span', { class: 't' }, fmtClock(ev.time)), result),
      body);
    feed.append(el);
    group = { el, body, stage: ev.stage, result };
    groups.push(group);
  };

  const addRow = (kind, ev, bodyNodes, extraClass = '') => {
    const body = h('div', { class: 'body' }, bodyNodes);
    const row = h('div', { class: `row k-${kind} ty-${ev.type} ${extraClass}` },
      h('span', { class: 't' }, fmtClock(ev.time)), h('span', { class: 'stg' }, ev.stage ?? ''), body);
    (group?.body ?? feed).append(row);
    return { row, body };
  };

  const appendEvent = (ev) => {
    if (ev.seq <= lastSeq) return;
    lastSeq = ev.seq;
    total++;
    count.textContent = `${total} event${total === 1 ? '' : 's'}`;

    switch (ev.type) {
      case 'AgentTextDelta':
        if (!openDelta || openDelta.stage !== ev.stage) {
          closeDelta();
          const { body } = addRow('text', ev, '');
          body.classList.add('live-caret');
          openDelta = { body, stage: ev.stage };
          openDelta.node = document.createTextNode('');
          body.prepend(openDelta.node);
        }
        openDelta.node.data += ev.text ?? '';
        break;
      case 'AgentText':
        closeDelta();
        if (ev.text) addRow('text', ev, ev.text);
        break;
      case 'AgentToolUse':
      case 'AgentToolResult': {
        closeDelta();
        const isUse = ev.type === 'AgentToolUse';
        const nodes = isUse
          ? [h('span', { class: 'tool-name' }, `▸ ${ev.text ?? ev.data?.tool ?? 'tool'}`)]
          : [h('span', { class: 'res' }, `  ↳ ${ev.text ?? 'result'}`)];
        if (ev.data) nodes.push(h('details', {}, h('summary', {}, 'data'), h('pre', {}, JSON.stringify(ev.data, null, 2))));
        addRow('tool', ev, nodes);
        break;
      }
      case 'StageStarted':
        closeDelta();
        startGroup(ev); // the group header replaces the "Stage X started" row
        break;
      default: {
        closeDelta();
        if (ev.type === 'RunFinished') group = null; // the closing lines belong to the run, not to a stage
        if (ev.type === 'StageFinished' && group && group.stage === ev.stage) {
          const outcome = ev.data?.outcome ?? '';
          group.result.textContent = outcome;
          group.result.className = `attempt-result o-${outcome.toLowerCase()}`;
        }
        const bad = ev.type === 'RunFinished' && ['Failed', 'Cancelled', 'Interrupted'].includes(ev.data?.status);
        addRow('system', ev, ev.text ?? ev.type, bad ? 'bad' : '');
      }
    }
    scrollSoon();
  };

  const es = new EventSource(`/api/runs/${encodeURIComponent(id)}/stream`);
  es.addEventListener('ev', (e) => appendEvent(JSON.parse(e.data)));

  onRunsChanged = (changed) => {
    if (changed.id !== id) return;
    if (changed.removed) { location.hash = '#/'; return; }
    renderRun(changed);
  };
  currentRunView = { close: () => es.close() };
}

const deleteRun = (r) => attempt(async () => {
  if (!confirm(`Permanently delete this run and its files?\n\n${r.title}\n\nThe git branch ${r.branch ?? ''} is not touched.`)) return;
  await api(`/api/runs/${encodeURIComponent(r.id)}`, { method: 'DELETE' });
  location.hash = '#/';
});

const cancelRun = (id) => attempt(() => post(`/api/runs/${encodeURIComponent(id)}/cancel`));

const releaseRun = (id) => attempt(() => post(`/api/runs/${encodeURIComponent(id)}/release`));

/* --------------------------------------------------------------- new run */

const dialog = document.getElementById('newRun');
const briefInput = document.getElementById('brief');
const projectSelect = document.getElementById('project');
const projectNote = document.getElementById('projectNote');

async function loadProjects() {
  const projects = await api('/api/projects');
  projectSelect.replaceChildren(...projects.map((p) =>
    h('option', { value: p.name, disabled: !p.valid, 'data-errors': (p.errors ?? []).join(' ') },
      p.valid ? p.name : `${p.name} (invalid)`)));
  const first = projects.find((p) => p.valid && p.name !== 'demo') ?? projects.find((p) => p.valid);
  if (first) projectSelect.value = first.name;
  try {
    catalog = await api('/api/models');
    agentList = (await api('/api/agents')).agents;
  } catch { catalog = []; agentList = []; }
  showProjectNote(projects);
  renderStageAgents(projects.find((x) => x.name === projectSelect.value));
  projectSelect.onchange = () => {
    showProjectNote(projects);
    renderStageAgents(projects.find((x) => x.name === projectSelect.value));
  };
}

/* Per-stage agent / model / effort pickers. The catalog (/api/models) says which models and efforts each agent accepts. */
const STAGE_LABELS = { Spec: 'Plan (Spec)', Implement: 'Implement', Integrate: 'Unity integrate (unity-mcp)' };
const stageAgentsBox = document.getElementById('stageAgents');
const stageAgentRows = document.getElementById('stageAgentRows');
let catalog = [];
let agentList = [];
let stageSelects = {}; // stage name -> { agent, model, effort }

const fill = (select, items, blankLabel) => {
  const keep = select.value;
  select.replaceChildren(h('option', { value: '' }, blankLabel), ...items.map(([v, label]) => h('option', { value: v }, label)));
  select.value = items.some(([v]) => v === keep) ? keep : '';
};

function refreshStageRow(refs, defaultAgent) {
  const agent = refs.agent.value || defaultAgent;
  const entry = catalog.find((c) => c.agent === agent);
  fill(refs.model, (entry?.models ?? []).map((m) => [m.id, m.label]), entry ? 'default model' : 'n/a');
  const model = entry?.models.find((m) => m.id === refs.model.value);
  const efforts = model ? model.efforts : (entry?.efforts ?? []);
  fill(refs.effort, efforts.map((e) => [e, e]), efforts.length ? 'default effort' : (entry?.effortNote ? 'in model name' : 'n/a'));
  refs.model.disabled = !entry;
  refs.effort.disabled = efforts.length === 0;
}

const autoPlan = document.getElementById('autoPlan');
const autoImpl = document.getElementById('autoImpl');

function renderStageAgents(project) {
  // The project's own default for automation, which the checkboxes then let you change for this run.
  autoPlan.checked = !!project?.auto?.plan;
  autoImpl.checked = !!project?.auto?.implementation;
  stageSelects = {};
  const stages = (project?.stages ?? []).filter((s) => STAGE_LABELS[s]);
  stageAgentsBox.hidden = !project || project.name === 'demo' || stages.length === 0;
  stageAgentRows.replaceChildren();
  if (stageAgentsBox.hidden) return;
  for (const stage of stages) {
    const refs = { agent: h('select', {}), model: h('select', {}), effort: h('select', {}) };
    fill(refs.agent, agentList.map((a) => [a, a]), `default (${project.agent})`);
    refs.agent.onchange = () => { refs.model.value = ''; refreshStageRow(refs, project.agent); };
    refs.model.onchange = () => refreshStageRow(refs, project.agent);
    refreshStageRow(refs, project.agent);
    stageSelects[stage] = refs;
    stageAgentRows.append(h('div', { class: 'stage-row' }, h('span', { class: 'stage-name' }, STAGE_LABELS[stage]), refs.agent, refs.model, refs.effort));
  }
}

/* Presets: built-in and saved combinations of the per-stage choices above. */
const presetSelect = document.getElementById('presetSelect');
const presetDelete = document.getElementById('presetDelete');
let presets = { builtin: [], custom: [] };

async function loadPresets() {
  try { presets = await api('/api/presets'); } catch { presets = { builtin: [], custom: [] }; }
  const opt = (p) => h('option', { value: p.name }, p.name);
  put(presetSelect,
    h('option', { value: '' }, 'Preset…'),
    presets.builtin.length && h('optgroup', { label: 'Built in' }, presets.builtin.map(opt)),
    presets.custom.length && h('optgroup', { label: 'Saved' }, presets.custom.map(opt)));
  presetDelete.hidden = true;
}

function applyPreset(preset) {
  for (const [stage, choice] of Object.entries(preset.stages ?? {})) {
    const refs = stageSelects[stage];
    if (!refs) continue; // the project has no such stage
    refs.agent.value = choice.agent ?? '';
    if (refs.agent.value !== (choice.agent ?? '')) continue; // that agent is not installed here
    refs.model.value = '';
    refreshStageRow(refs, refs.agent.value);
    refs.model.value = choice.model ?? '';
    refreshStageRow(refs, refs.agent.value);
    refs.effort.value = choice.effort ?? '';
  }
}

presetSelect.addEventListener('change', () => {
  const all = [...presets.builtin, ...presets.custom];
  const preset = all.find((p) => p.name === presetSelect.value);
  presetDelete.hidden = !presets.custom.some((p) => p.name === presetSelect.value);
  if (preset) applyPreset(preset);
});

document.getElementById('presetSave').addEventListener('click', () => attempt(async () => {
  const stages = collectStageChoices();
  if (!Object.keys(stages).length) { alert('Choose an agent, model or effort for at least one stage first.'); return; }
  const name = prompt('Name for this preset');
  if (!name?.trim()) return;
  await post('/api/presets', { name: name.trim(), stages });
  await loadPresets();
  presetSelect.value = name.trim();
  presetDelete.hidden = false;
}));

presetDelete.addEventListener('click', () => attempt(async () => {
  const name = presetSelect.value;
  if (!name || !confirm(`Delete the saved preset "${name}"?`)) return;
  await api(`/api/presets/${encodeURIComponent(name)}`, { method: 'DELETE' });
  await loadPresets();
}));

function collectStageChoices() {
  const out = {};
  for (const [stage, refs] of Object.entries(stageSelects)) {
    const choice = { agent: refs.agent.value || null, model: refs.model.value || null, effort: refs.effort.value || null };
    if (choice.agent || choice.model || choice.effort) out[stage] = choice;
  }
  return out;
}

function showProjectNote(projects) {
  const p = projects.find((x) => x.name === projectSelect.value);
  if (!p) { projectNote.textContent = ''; return; }
  projectNote.textContent = p.name === 'demo'
    ? 'Scripted stages, no repository or agent.'
    : `${p.agent} · ${p.workspace} workspace on ${p.baseBranch} · ${p.repoPath}`;
  const bad = projects.filter((x) => !x.valid).map((x) => `${x.name}: ${x.errors.join(' ')}`);
  if (bad.length) projectNote.textContent += `\nNot usable: ${bad.join(' | ')}`;
}

document.getElementById('newRunBtn').addEventListener('click', async () => {
  try { await loadProjects(); } catch { /* keep whatever is listed */ }
  await loadPresets();
  dialog.showModal();
  briefInput.focus();
});
document.getElementById('newRunCancel').addEventListener('click', () => dialog.close());
document.getElementById('newRunForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const brief = briefInput.value.trim();
  if (!brief) return;
  try {
    const run = await api('/api/runs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        brief, project: projectSelect.value, stages: collectStageChoices(),
        auto: { plan: autoPlan.checked, implementation: autoImpl.checked },
      }),
    });
    briefInput.value = '';
    dialog.close();
    location.hash = `#/run/${encodeURIComponent(run.id)}`;
  } catch (err) {
    alert(`Could not start run: ${err.message}`);
  }
});

/* ----------------------------------------------------------------- start */

startGlobalStream();
route();
