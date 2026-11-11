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
    runs.set(run.id, run);
    updateTitle();
    onRunsChanged?.(run);
  });
}

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
  view.replaceChildren(h('div', { class: 'page-head' }, h('h1', {}, 'Runs'), sub), strip, list);

  const render = () => {
    const all = [...runs.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    const active = all.filter((r) => isActive(r.status)).length;
    sub.textContent = all.length ? `${active} active · ${all.length} total` : '';
    if (!all.length) {
      list.replaceChildren(h('div', { class: 'empty' },
        h('strong', {}, 'No runs yet'),
        'Start one with "New run" and watch the agents work live.'));
      return;
    }
    list.replaceChildren(...all.map(runRow));
  };

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
  return h('a', { class: `run-row${run.status === 'WaitingForHuman' ? ' attn' : ''}`, href: `#/run/${encodeURIComponent(run.id)}` },
    h('div', {}, pill(run.status)),
    h('div', {}, h('div', { class: 'title' }, run.title), h('div', { class: 'meta' }, `${run.project} · ${run.id}`)),
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
    if (!isFeed) loadArtifact(name, r?.updatedAt);
  };

  const loadArtifact = async (name, stamp) => {
    const key = `${name}@${stamp}`;
    if (artifactFor === key) return;
    artifactFor = key;
    try {
      const res = await fetch(`/api/runs/${encodeURIComponent(id)}/files/${name.split('/').map(encodeURIComponent).join('/')}`);
      artifactView.textContent = res.ok ? await res.text() : `Could not load ${name} (${res.status}).`;
    } catch (err) {
      artifactView.textContent = `Could not load ${name}: ${err.message}`;
    }
  };

  const renderTabs = (r) => {
    const names = ['feed', ...(r.artifacts ?? [])];
    const key = names.join('|');
    if (key !== tabKey) {
      tabKey = key;
      tabs.replaceChildren(...names.map((n) => h('button', {
        class: 'tab', type: 'button', role: 'tab', 'data-tab': n,
        onclick: () => showTab(n, runs.get(id)),
      }, n === 'feed' ? 'Live feed' : n.replace(/^evidence\//, ''))));
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
    const agent = h('select', {}, agentNames.map((a) => h('option', { value: a, selected: a === r.agent }, a)));
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
      live && h('button', { class: 'btn danger small', type: 'button', onclick: () => cancelRun(r.id) }, 'Cancel run'));
    guidanceBox.hidden = !live;
    noteList.replaceChildren(...(r.guidance ?? []).map((g) =>
      h('div', { class: 'note' }, h('span', { class: 'note-t' }, `${fmtClock(g.time)}${g.stage ? ' · ' + g.stage : ''}`), g.text)));
    renderDecision(r);
    renderTabs(r);
    put(facts,
      h('span', {}, 'Project ', h('b', {}, r.project)),
      h('span', {}, 'Run ', h('b', {}, r.id)),
      r.agent && h('span', {}, 'Agent ', h('b', {}, r.agent)),
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

  const addRow = (kind, ev, bodyNodes, extraClass = '') => {
    const body = h('div', { class: 'body' }, bodyNodes);
    const row = h('div', { class: `row k-${kind} ty-${ev.type} ${extraClass}` },
      h('span', { class: 't' }, fmtClock(ev.time)), h('span', { class: 'stg' }, ev.stage ?? ''), body);
    feed.append(row);
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
      default: {
        closeDelta();
        const bad = ev.type === 'RunFinished' && ['Failed', 'Cancelled', 'Interrupted'].includes(ev.data?.status);
        addRow('system', ev, ev.text ?? ev.type, bad ? 'bad' : '');
      }
    }
    scrollSoon();
  };

  const es = new EventSource(`/api/runs/${encodeURIComponent(id)}/stream`);
  es.addEventListener('ev', (e) => appendEvent(JSON.parse(e.data)));

  onRunsChanged = (changed) => { if (changed.id === id) renderRun(changed); };
  currentRunView = { close: () => es.close() };
}

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

function renderStageAgents(project) {
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
      body: JSON.stringify({ brief, project: projectSelect.value, stages: collectStageChoices() }),
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
