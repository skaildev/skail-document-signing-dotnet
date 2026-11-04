const KEY = 'document-signing.contractId';
const AREAS = ['legal', 'finance'];
const AREA_LABELS = { legal: 'Legal', finance: 'Finance' };

const $ = (id) => document.getElementById(id);
const els = {
  title: $('title'), start: $('start'), state: $('state'), error: $('error'), result: $('result'),
  timeline: $('timeline'), contractId: $('contractId'), monitor: $('monitor'), countdown: $('countdown'),
};
const cards = Object.fromEntries(AREAS.map((a) => {
  const root = document.querySelector(`[data-area="${a}"]`);
  const part = (role) => root.querySelector(`[data-role="${role}"]`);
  return [a, { root, status: part('status'), detail: part('detail'), by: part('by'), reason: part('reason'), sign: part('sign'), refuse: part('refuse') }];
}));

let contract = null;
let source = null;
let defaultTitle = '';

const isTerminal = (c) => c && ['Archived', 'Cancelled', 'Expired', 'Failed'].includes(c.phase);
const time = (iso) => new Date(iso).toLocaleTimeString();

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message ?? '';
}

function stateLabel(c) {
  if (!c) return 'Idle';
  if (c.phase === 'Starting') return 'Starting…';
  if (c.phase === 'Waiting') {
    const pending = AREAS.filter((a) => c[a].status === 'Pending');
    if (pending.length === 2) return 'Waiting for both';
    if (pending.length === 1) return `Waiting for ${AREA_LABELS[pending[0]]}`;
    return 'Archiving…';
  }
  return c.phase;
}

function resultText(r) {
  if (r.end === 'Archived') {
    const signed = r.signatures.map((s) => `${AREA_LABELS[s.area]} (${s.by})`).join(' and ');
    return `Archived · reference ${r.reference} · signed by ${signed}`;
  }
  if (r.end === 'Expired') {
    const revoked = r.signatures.map((s) => `${AREA_LABELS[s.area]} signature revoked`);
    return ['Expired · no answer before the deadline', ...revoked].join(' · ');
  }
  const other = r.otherArea === 'Revoked' ? 'signature revoked' : 'request withdrawn';
  return `Cancelled · refused by ${AREA_LABELS[r.refusedBy] ?? r.refusedBy}${r.reason ? ` (${r.reason})` : ''} · the other area: ${other}`;
}

// the countdown is only visual: the real deadline is skail's durable Delay
function tick() {
  const waiting = contract?.phase === 'Waiting' && contract.startedAt;
  els.countdown.hidden = !waiting;
  if (!waiting) return;
  const left = Math.max(0, contract.deadlineSeconds - (Date.now() - new Date(contract.startedAt).getTime()) / 1000);
  els.countdown.textContent = `deadline in ${Math.ceil(left)}s`;
}

function render() {
  const active = contract?.active === true;
  els.start.disabled = active;
  els.title.disabled = active;
  if (contract) els.title.value = contract.title;

  els.state.textContent = stateLabel(contract);
  els.state.className = 'badge ' + (['Failed', 'Cancelled', 'Expired'].includes(contract?.phase) ? 'failed'
    : contract?.phase === 'Archived' ? 'done' : contract ? 'active' : 'idle');
  els.contractId.textContent = contract?.contractId ?? '-';
  showError(contract?.error);

  for (const area of AREAS) {
    const card = cards[area];
    const view = contract?.[area];
    const status = view?.status ?? 'Pending';
    card.status.textContent = status;
    card.status.className = `pill ${status}`;
    card.detail.textContent = [
      view?.requested ? 'Signature requested' : 'Not requested yet',
      view?.by, view?.at ? time(view.at) : '', view?.reason,
    ].filter(Boolean).join(' · ');
    // only a pending area of an active contract can still decide
    const canDecide = active && status === 'Pending';
    for (const control of [card.by, card.reason, card.sign, card.refuse]) control.disabled = !canDecide;
  }

  const r = contract?.result;
  els.result.hidden = !r;
  els.result.className = 'result ' + (r?.end === 'Archived' ? 'ok' : 'bad');
  if (r) els.result.textContent = resultText(r);
  tick();

  els.timeline.replaceChildren(...(contract?.timeline ?? []).map((t) => {
    const item = document.createElement('li');
    item.textContent = `${time(t.at)}  ${t.text}`;
    return item;
  }));
}

function connect(contractId) {
  source?.close();
  source = new EventSource(`/api/contracts/${contractId}/stream`);
  source.onmessage = (event) => {
    contract = JSON.parse(event.data);
    setInterval(tick, 500);
render();
    if (isTerminal(contract)) {
      source.close();
      localStorage.removeItem(KEY);
    }
  };
  // EventSource reconnects by itself; the stream sends the current snapshot first
}

async function start() {
  els.start.disabled = true;
  els.title.disabled = true;
  showError(null);
  try {
    const res = await fetch('/api/contracts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: els.title.value }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
    contract = await res.json();
    localStorage.setItem(KEY, contract.contractId);
    render();
    connect(contract.contractId);
  } catch (e) {
    showError(`Could not start: ${e.message}`);
    render();
  }
}

async function decide(area, signed) {
  const card = cards[area];
  for (const control of [card.by, card.reason, card.sign, card.refuse]) control.disabled = true;
  showError(null);
  try {
    const res = await fetch(`/api/contracts/${contract.contractId}/signatures`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ area, signed, signedBy: card.by.value, reason: card.reason.value }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error ?? `HTTP ${res.status}`);
  } catch (e) {
    showError(`Could not send the decision: ${e.message}`); // the server released the area: the buttons come back
  }
  render();
}

els.start.addEventListener('click', start);
for (const area of AREAS) {
  cards[area].sign.addEventListener('click', () => decide(area, true));
  cards[area].refuse.addEventListener('click', () => decide(area, false));
}

fetch('/api/config').then((r) => r.json()).then((c) => {
  defaultTitle = c.defaultTitle;
  if (!els.title.value) els.title.placeholder = defaultTitle;
  if (c.monitorUrl) { els.monitor.href = c.monitorUrl; els.monitor.hidden = false; }
}).catch(() => {});

// reload keeps the cards and the locked controls right: the state always comes from the server
const saved = localStorage.getItem(KEY);
if (saved) {
  fetch(`/api/contracts/${saved}`).then((r) => (r.ok ? r.json() : null)).then((c) => {
    if (!c) return localStorage.removeItem(KEY);
    contract = c;
    render();
    if (!isTerminal(c)) connect(c.contractId);
  });
}
render();
