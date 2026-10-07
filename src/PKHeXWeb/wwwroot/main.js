import { dotnet } from './_framework/dotnet.js';
const $ = (id) => document.getElementById(id), J = (s) => JSON.parse(s);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const nice = (s) => s.replace(/_/g, ' ').replace(/([a-z])([A-Z0-9])/g, '$1 $2');
const toast = (t) => { const e = document.createElement('div'); e.className = 'toast'; e.textContent = t; document.body.appendChild(e); setTimeout(() => e.remove(), 3500); };
addEventListener('unhandledrejection', (e) => toast('Error: ' + (e.reason?.message ?? e.reason)));
addEventListener('error', (e) => toast('Error: ' + e.message));

const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const E = (await getAssemblyExports(getConfig().mainAssemblyName)).PkhexWeb.Engine;
await runMain();
const N = J(E.GetNames());
const call = (fn) => { try { return fn(); } catch (e) { return { ok: false, error: e?.message ?? String(e) }; } };

const SPR = 'https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/';
const img = (id, shiny) => id ? `<img src="${SPR}${shiny ? 'shiny/' : ''}${id}.png" alt="" loading="lazy" onerror="this.remove()">` : '';
const optCache = {};
const listOpts = (k) => (optCache[k] ??= N[k].map((n, i) => `<option value="${i}">${esc(n || '—')}</option>`).join(''));
const LISTS = [[/^Species$/, 'species'], [/^(Move[1-4]|RelearnMove[1-4]|AlphaMove)$/, 'moves'], [/^(HeldItem|Item)$/, 'items'], [/^Ability$/, 'abilities'], [/^(Nature|StatNature)$/, 'natures']];
const NUM = /^(Byte|SByte|U?Int(16|32|64))$/;
const TAB = {
  Main: /^(Species|Nickname|IsNicknamed|IsShiny|IsAlpha|AlphaMove|CurrentLevel|EXP|Nature|StatNature|Ability|AbilityNumber|HeldItem|Gender|Form|FormArgument|IsEgg|CurrentFriendship|HeightScalar|WeightScalar|Scale|Tera\w*|PID|EncryptionConstant)$/,
  Moves: /^(Move[1-4]|RelearnMove[1-4])(_PP|_PPUps)?$/,
  Cosmetic: /^(Contest|Marking|Ribbon|AffixedRibbon|HasBattle|HasContest)/,
  Met: /^(Ball|Version|Fateful|Met|Egg)/,
  'OT / Misc': /^(OriginalTrainer|OT_|HandlingTrainer|HT_|TID|SID|Language|Geo)/,
};
// Friendly names for the Origin game dropdown. Only games listed here are offered (plus the current value).
const GAME = { RD: 'Red', GN: 'Green', BU: 'Blue', YW: 'Yellow', GD: 'Gold', SI: 'Silver', C: 'Crystal', R: 'Ruby', S: 'Sapphire', E: 'Emerald', FR: 'FireRed', LG: 'LeafGreen', CXD: 'Colosseum / XD', D: 'Diamond', P: 'Pearl', Pt: 'Platinum', HG: 'HeartGold', SS: 'SoulSilver', B: 'Black', W: 'White', B2: 'Black 2', W2: 'White 2', X: 'X', Y: 'Y', OR: 'Omega Ruby', AS: 'Alpha Sapphire', SN: 'Sun', MN: 'Moon', US: 'Ultra Sun', UM: 'Ultra Moon', GO: 'Pokémon GO', GP: 'Let’s Go, Pikachu!', GE: 'Let’s Go, Eevee!', SW: 'Sword', SH: 'Shield', BD: 'Brilliant Diamond', SP: 'Shining Pearl', PLA: 'Legends: Arceus', SL: 'Scarlet', VL: 'Violet', ZA: 'Legends: Z-A' };
const S = { info: null, box: 0, slot: -1, slots: [], props: [], opts: {}, legal: {}, tab: 'Main', note: '' };
let legalDirty = true;
const TR = { props: [], inv: null, dex: null, tab: 'Trainer', open: new Set() };   // Trainer window state
const LEGAL_DEP = /^(Species|Form|CurrentLevel|EXP|Version|Met|Egg|IsEgg|Ability|Move[1-4]$)/;
const ONLY_LEGAL = /^(Move[1-4]|RelearnMove[1-4]|Ability)$/;
const loadOpts = () => {
  const b = call(() => J(E.GetOptions(S.box, S.slot))).opts ?? {};
  if (legalDirty) { const r = call(() => J(E.GetLegalChoices(S.box, S.slot))); S.legal = r.opts ?? {}; if (!r.ok) toast('Legal lists unavailable: ' + r.error); legalDirty = false; }
  S.opts = { ...b, ...S.legal };
};
const by = (n) => S.props.find((p) => p.name === n);

function ctl(p) {
  const v = p.value ?? '', lbl = nice(p.name), k = LISTS.find(([r]) => r.test(p.name))?.[1], o = S.opts[p.name];
  if (o) return `<label class="f">${lbl}<select data-p="${p.name}" data-v="${esc(v)}">${o.some((x) => String(x.v) === v) ? '' : `<option value="${esc(v)}">${esc(v)} (unknown)</option>`}${o.map((x) => `<option value="${x.v}">${esc(x.t)}</option>`).join('')}</select></label>`;
  if (!o && ONLY_LEGAL.test(p.name)) return `<label class="f">${lbl}<select data-p="${p.name}" data-v="${esc(v)}" disabled title="Legal list unavailable"><option value="${esc(v)}">${esc((k && N[k][+v]) || v)}</option></select></label>`;
  if (p.type === 'Boolean') return `<label class="f chk"><input type="checkbox" data-p="${p.name}"${v === 'True' ? ' checked' : ''}>${lbl}</label>`;
  if (p.options) return `<label class="f">${lbl}<select data-p="${p.name}" data-v="${esc(v)}">${p.options.map((o) => `<option>${esc(o)}</option>`).join('')}</select></label>`;
  if (k) return `<label class="f">${lbl}<select data-p="${p.name}" data-v="${esc(v)}">${listOpts(k)}</select></label>`;
  return `<label class="f">${lbl}<input data-p="${p.name}" type="${NUM.test(p.type) ? 'number' : 'text'}"${NUM.test(p.type) && p.min != null ? ` min="${p.min}" max="${p.max}" step="1"` : ''} value="${esc(v)}"></label>`;
}

function updateHist() {
  const u = $('undobtn'), r = $('redobtn'); if (!u || !r) return;
  const h = call(() => J(E.HistoryState()));
  u.disabled = !(h.undo > 0); r.disabled = !(h.redo > 0);
}

function loadGrid() {
  updateHist();
  const r = call(() => J(S.box < 0 ? E.GetParty() : E.GetBox(S.box)));
  S.slots = r.slots ?? [];
  $('grid').innerHTML = S.slots.map((s) => s.empty
    ? `<button class="slot${s.slot === S.slot ? ' sel' : ''}" data-s="${s.slot}" aria-label="Empty slot"></button>`
    : `<button class="slot f${s.slot === S.slot ? ' sel' : ''}" data-s="${s.slot}" aria-label="${esc(s.nick)} level ${s.level}">${img(s.id, s.shiny)}<i class="lg ${s.legal ? 'ok' : 'bad'}" title="${s.legal ? 'Legal' : 'Illegal'}"></i>${s.shiny ? '<i class="sh" title="Shiny">★</i>' : ''}${s.alpha ? '<i class="al" title="Alpha">α</i>' : ''}<span>${s.level}</span></button>`).join('');
  $('boxsel').value = S.box;
  syncTools();
}

// Copy / Delete only make sense with a Pokémon selected (the toolbar is built at the end of this file).
function syncTools() {
  const c = $('tbcopy'), d = $('tbdel'); if (!c || !d) return;
  const has = S.slot >= 0 && !!S.slots[S.slot] && !S.slots[S.slot].empty;
  c.disabled = !has; d.disabled = !has || S.box < 0;
  d.title = has && S.box < 0 ? 'Deleting from the party isn’t supported yet' : 'Delete the selected Pokémon';
}

function setup(info) {
  if (!info.ok) return toast(info.error);
  S.info = info; S.slot = -1; TR.props = []; TR.inv = TR.dex = null;
  $('boxsel').innerHTML = `<option value="-1">Party</option>` + Array.from({ length: info.boxes }, (_, i) => `<option value="${i}">Box ${i + 1}</option>`).join('');
  S.box = info.party > 0 ? -1 : 0;
  $('hint').textContent = `${info.game} · generation ${info.generation} · trainer ${info.ot}`;
  $('newp').hidden = true; loadGrid(); empty();
}

function empty() { $('ed').classList.remove('open'); $('ed').innerHTML = '<p class="hint" style="margin:0">Select a Pokémon to edit it, or an empty slot to add one.</p>'; }

function pick(i) {
  S.slot = i; loadGrid(); const s = S.slots[i];
  if (s.empty) return showEmpty();
  S.props = call(() => J(E.GetProps(S.box, i))).props ?? []; legalDirty = true; loadOpts(); S.tab = 'Main'; S.note = ''; view();
}

function showEmpty() {
  const ed = $('ed');
  ed.innerHTML = S.box < 0
    ? '<div class="top"><div><h2>Empty party slot</h2><small>Adding to the party isn’t supported yet. Use a box.</small></div><button class="btn cl" id="cl">Close</button></div>'
    : `<div class="top"><div><h2>Empty slot</h2><small>Box ${S.box + 1}, slot ${S.slot + 1}</small></div><button class="btn cl" id="cl">Close</button></div>
<div class="fg"><label class="f">Species<select id="nsp">${listOpts('species')}</select></label><label class="f">Level<input id="nlv" type="number" min="1" max="100" value="50"></label><label class="f">Encounter<select id="nenc"><option value="-1">Automatic (first legal)</option></select></label><label class="f chk"><input type="checkbox" id="nsh">Shiny</label>${E.AlphaSupported() ? '<label class="f chk"><input type="checkbox" id="nal">Alpha</label>' : ''}</div>
<p><button class="btn pri" id="create">Create Pokémon</button> <button class="btn" id="sdbtn">Import Showdown set…</button></p>
<label class="f">Or import a Pokémon file<input type="file" id="impf"></label><p class="hint">Files from older generations are converted forward and legalised automatically.</p>`;
  if ($('nsp')) { $('nsp').value = 25; loadEncounters(); }
  enhance(ed);
  ed.classList.add('open');
}

function loadEncounters() {
  const sel = $('nenc'); if (!sel) return;
  const r = call(() => J(E.ListEncounters(+$('nsp').value)));
  sel.innerHTML = '<option value="-1">Automatic (first legal)</option>' + (r.encounters ?? []).map((e) => {
    const lv = e.min ? (e.min === e.max ? ` Lv ${e.min}` : ` Lv ${e.min}-${e.max}`) : '';
    return `<option value="${e.i}">${esc(nice(e.kind))}: ${esc(e.name)}${lv}</option>`;
  }).join('');
  sel.value = '-1'; sel._cbSync?.();
  if (!r.ok) toast(r.error);
}

const ST = ['HP', 'ATK', 'DEF', 'SPA', 'SPD', 'SPE'];
const HT_FLAG = /^HT_(HP|ATK|DEF|SPA|SPD|SPE)$/;
document.head.appendChild(Object.assign(document.createElement('style'), { textContent:
  '.ht-on input{background:#fde047!important;color:#1a1a1a!important;border-color:#eab308!important}' +
  '.sc{display:flex;gap:6px;align-items:center}.sc label{flex:1;min-width:0}.sc .mx{flex:none;padding:4px 8px;font-size:12px}' +
  '#rbm{width:min(620px,94vw);max-height:86vh;padding:0;border:1px solid #8886;border-radius:12px}#rbm::backdrop{background:#0009}' +
  '#rbm .rbw{display:flex;flex-direction:column;max-height:86vh}#rbm .rbh{padding:12px 16px;border-bottom:1px solid #8884}#rbm .rbh h3{margin:0 0 8px}' +
  '#rbm .rbl{overflow:auto;padding:8px 16px;display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:0 14px}' +
  '#rbm .rbi{display:flex;gap:8px;align-items:center;padding:6px 0;cursor:pointer}#rbm .rbi.bad{color:#dc2626}' +
  '#rbm .rbf{display:flex;gap:8px;flex-wrap:wrap;align-items:center;padding:10px 16px;border-top:1px solid #8884}#rbm .rbf .sp{flex:1}' }));
// Largest value the Max button may set: IV cap, EV cap, or what is left of the 510 EV total.
const maxFor = (n) => {
  const p = by(n); if (!p || p.max == null) return null;
  const cap = +p.max;
  if (!/^EV_/.test(n) || p.max !== '252') return cap;
  const others = ST.reduce((a, x) => (`EV_${x}` === n ? a : a + +(by(`EV_${x}`)?.value ?? 0)), 0);
  return Math.max(0, Math.min(cap, 510 - others));
};

const isRibbon = (p) => p.type === 'Boolean' && /^Ribbon/.test(p.name);

function setRibbon(n, on) {
  const r = call(() => J(E.SetProp(S.box, S.slot, n, String(on))));
  if (!r.ok) { toast(r.error); return false; }
  const p = by(n); if (p) p.value = on ? 'True' : 'False';
  return true;
}

function openRibbons() {
  let dlg = $('rbm');
  if (!dlg) {
    dlg = document.createElement('dialog'); dlg.id = 'rbm'; document.body.appendChild(dlg);
    dlg.addEventListener('close', () => { if (S.slot >= 0 && S.props.length) refresh(); });
    dlg.addEventListener('click', (e) => {
      if (e.target === dlg) return dlg.close();
      const b = e.target.closest('button'); if (!b) return;
      if (b.dataset.rbclose) return dlg.close();
      if (b.dataset.rbset) {
        const on = b.dataset.rbset === '1';
        dlg.querySelectorAll('.rbi:not([hidden])').forEach((l) => {
          const c = l.querySelector('input');
          if ((on && l.dataset.bad) || c.checked === on) return;
          if (setRibbon(c.dataset.rb, on)) c.checked = on;
        });
        count();
      }
    });
    dlg.addEventListener('change', (e) => {
      const c = e.target; if (!c.dataset.rb) return;
      if (!setRibbon(c.dataset.rb, c.checked)) c.checked = !c.checked;
      count();
    });
    dlg.addEventListener('input', (e) => {
      if (e.target.id !== 'rbq') return; const q = e.target.value.toLowerCase();
      dlg.querySelectorAll('.rbi').forEach((l) => { l.hidden = !l.textContent.toLowerCase().includes(q); });
    });
  }
  const count = () => { const n = dlg.querySelector('#rbn'); if (n) n.textContent = `${dlg.querySelectorAll('.rbi input:checked').length} set`; };
  const cs = getComputedStyle(document.body);
  dlg.style.background = cs.backgroundColor === 'rgba(0, 0, 0, 0)' ? 'Canvas' : cs.backgroundColor; dlg.style.color = cs.color;
  dlg.innerHTML = '<div class="rbw"><div class="rbh"><h3>Ribbons and marks</h3><p class="hint" style="margin:0">Checking which ribbons are legal…</p></div></div>';
  dlg.showModal();
  setTimeout(() => {
    const r = call(() => J(E.GetLegalRibbons(S.box, S.slot)));
    const legal = new Set(r.legal ?? []), rb = S.props.filter(isRibbon);
    const shown = r.ok ? rb.filter((p) => legal.has(p.name) || p.value === 'True') : rb;
    const row = (p) => {
      const bad = r.ok && !legal.has(p.name);
      return `<label class="rbi${bad ? ' bad' : ''}"${bad ? ' data-bad="1"' : ''}><input type="checkbox" data-rb="${p.name}"${p.value === 'True' ? ' checked' : ''}>${esc(nice(p.name.replace(/^Ribbon/, '')))}${bad ? ' (not legal)' : ''}</label>`;
    };
    dlg.innerHTML = `<div class="rbw"><div class="rbh"><h3>Ribbons and marks <small id="rbn" class="hint"></small></h3>`
      + (r.ok ? '' : `<p class="bad" style="margin:0 0 8px">Legal filter unavailable (${esc(r.error ?? 'unknown error')}); showing every ribbon.</p>`)
      + `<input id="rbq" type="search" placeholder="Filter ribbons" style="width:100%"></div>`
      + `<div class="rbl">${shown.map(row).join('') || '<p class="hint">No ribbons can be legally set on this Pokémon.</p>'}</div>`
      + `<div class="rbf"><button class="btn" data-rbset="1">Select all legal</button><button class="btn" data-rbset="0">Clear all</button><span class="sp"></span><button class="btn pri" data-rbclose="1">Done</button></div></div>`;
    count();
  }, 30);
}

// ---------- searchable dropdowns ----------
// Turns the plain <select> for long lists (species, moves, items, natures, locations, encounters) into a search box
// with a filtered drop-down. The real <select> stays in the DOM (hidden), so all existing change handlers keep working.
const COMBO = /^(Species|Move[1-4]|RelearnMove[1-4]|AlphaMove|HeldItem|Item|Nature|StatNature|Met_?Location|Egg_?Location)$/;
const COMBO_IDS = new Set(['nsp', 'nenc']);
const CB_MAX = 400;
document.head.appendChild(Object.assign(document.createElement('style'), { textContent:
  '.cb{position:relative;display:block}.cb .cbi{width:100%;box-sizing:border-box}' +
  '.cbl{position:fixed;z-index:1000;margin:0;padding:4px;list-style:none;overflow:auto;border:1px solid #8886;border-radius:8px;box-shadow:0 8px 24px #0005}' +
  '.cbl li{padding:6px 8px;border-radius:6px;cursor:pointer}.cbl li.act{background:#6366f133}.cbl li.cur{font-weight:600}' +
  '.cbl li.msg{cursor:default;opacity:.65;font-size:12px}' }));

function combo(sel) {
  if (sel.dataset.cb) return; sel.dataset.cb = '1';
  const wrap = document.createElement('div'); wrap.className = 'cb';
  const inp = document.createElement('input'); inp.type = 'text'; inp.className = 'cbi'; inp.autocomplete = 'off'; inp.spellcheck = false;
  inp.placeholder = 'Search…'; inp.setAttribute('role', 'combobox'); inp.setAttribute('aria-autocomplete', 'list'); inp.disabled = sel.disabled; inp.title = sel.title;
  const ul = document.createElement('ul'); ul.className = 'cbl'; ul.hidden = true; ul.setAttribute('role', 'listbox');
  sel.hidden = true; sel.after(wrap); wrap.append(inp, ul);

  const cur = () => sel.selectedOptions[0];
  const sync = () => { inp.value = cur()?.textContent ?? ''; };
  sel._cbSync = sync; sync();

  let shown = [], act = -1;
  const paint = () => {
    [...ul.children].forEach((li, i) => li.classList.toggle('act', i === act));
    ul.children[act]?.scrollIntoView({ block: 'nearest' });
  };
  const place = () => {
    const r = inp.getBoundingClientRect(), below = innerHeight - r.bottom - 12, above = r.top - 12, up = below < 180 && above > below;
    const h = Math.max(120, Math.min(300, up ? above : below));
    Object.assign(ul.style, { left: r.left + 'px', width: r.width + 'px', maxHeight: h + 'px', top: up ? '' : r.bottom + 2 + 'px', bottom: up ? innerHeight - r.top + 2 + 'px' : '' });
    const cs = getComputedStyle(document.body);
    ul.style.background = cs.backgroundColor === 'rgba(0, 0, 0, 0)' ? 'Canvas' : cs.backgroundColor; ul.style.color = cs.color;
  };
  const render = (q, jump) => {
    q = q.trim().toLowerCase();
    const all = [...sel.options];
    let m = !q ? all : all.filter((o) => o.textContent.toLowerCase().includes(q) || o.value === q);
    if (q) m = [...m.filter((o) => o.textContent.toLowerCase().startsWith(q)), ...m.filter((o) => !o.textContent.toLowerCase().startsWith(q))];
    const more = m.length - CB_MAX; shown = m.slice(0, CB_MAX);
    ul.innerHTML = shown.map((o, i) => `<li role="option" data-i="${i}" class="${o === cur() ? 'cur' : ''}">${esc(o.textContent)}</li>`).join('')
      + (more > 0 ? `<li class="msg">${more} more — keep typing to narrow</li>` : '') + (m.length ? '' : '<li class="msg">No matches</li>');
    act = jump ? Math.max(0, shown.indexOf(cur())) : 0;
    paint();
  };
  const open = () => { if (inp.disabled) return; render('', true); place(); ul.hidden = false; paint(); };
  const close = () => { ul.hidden = true; sync(); };
  const choose = (o) => {
    if (!o) return;
    const changed = sel.value !== o.value; sel.value = o.value; close(); inp.blur();
    if (changed) sel.dispatchEvent(new Event('change', { bubbles: true }));
  };

  inp.addEventListener('focus', () => { open(); inp.select(); });
  inp.addEventListener('click', () => { if (ul.hidden) { open(); inp.select(); } });
  inp.addEventListener('input', () => { if (ul.hidden) { place(); ul.hidden = false; } render(inp.value, false); });
  inp.addEventListener('blur', () => { setTimeout(() => { if (!ul.hidden) close(); }, 0); });
  inp.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault(); if (ul.hidden) return open();
      act = Math.max(0, Math.min(shown.length - 1, act + (e.key === 'ArrowDown' ? 1 : -1))); paint();
    } else if (e.key === 'Enter') { e.preventDefault(); if (!ul.hidden) choose(shown[act]); }
    else if (e.key === 'Escape') { if (!ul.hidden) { e.stopPropagation(); close(); inp.blur(); } }
    else if (e.key === 'Tab') close();
  });
  // mousedown (not click) so the input doesn't lose focus before the choice registers
  ul.addEventListener('mousedown', (e) => {
    e.preventDefault(); const li = e.target.closest('li[data-i]'); if (li) choose(shown[+li.dataset.i]);
  });
  addEventListener('scroll', (e) => { if (!ul.hidden && !ul.contains(e.target)) place(); }, true);
  addEventListener('resize', () => { if (!ul.hidden) place(); });
}
const enhance = (root) => root.querySelectorAll('select').forEach((s) => { if (COMBO_IDS.has(s.id) || COMBO.test(s.dataset.p ?? '')) combo(s); });

// Evolutions that keep a counter in the form argument; the minimum PKHeX requires once evolved (same table as EvoArg in Engine.cs).
const FARG_MIN = { 865: 3, 867: 49, 979: 20, 899: 20, 902: 294, 983: 3 };

// <species number>-<species name>-<ball>-<origin game>
const exportName = () => {
  const id = +(by('Species')?.value ?? 0);
  const bv = by('Ball')?.value, ball = S.opts.Ball?.find((o) => String(o.v) === bv)?.t ?? bv;
  const gv = by('Version')?.value, game = gv ? (GAME[gv] ?? gv) : '';
  const clean = (x) => String(x ?? '').replace(/[\\/:*?"<>|]/g, '').trim().replace(/\s+/g, '_');
  return [String(id).padStart(4, '0'), clean(N.species[id]) || 'Unknown', clean(ball), clean(game)].filter(Boolean).join('-');
};

// ---------- Showdown set import / export (one Pokémon) ----------
document.head.appendChild(Object.assign(document.createElement('style'), { textContent:
  '.top{flex-wrap:wrap}.top>div:nth-child(2){min-width:140px}' +
  '#sdm{width:min(560px,96vw);max-height:90vh;padding:0;border:1px solid var(--bd);border-radius:14px;background:var(--pn);color:var(--tx)}#sdm::backdrop{background:#0009}' +
  '#sdm .sdw{display:flex;flex-direction:column;max-height:90vh}#sdm .sdh{padding:12px 16px 0}#sdm .sdh h3{margin:0 0 4px;font-size:16px}' +
  '#sdm .sdb{padding:10px 16px;display:flex;flex-direction:column;gap:8px;overflow:auto}' +
  '#sdm textarea{width:100%;min-height:240px;resize:vertical;background:var(--in);border:1px solid var(--bd);border-radius:10px;padding:10px;color:var(--tx);font:13px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}' +
  '#sdm .sdf{display:flex;gap:8px;align-items:center;padding:10px 16px calc(10px + env(safe-area-inset-bottom,0px));border-top:1px solid var(--bd)}#sdm .sp{flex:1}' }));

function openShowdown() {
  let dlg = $('sdm');
  if (!dlg) {
    dlg = document.createElement('dialog'); dlg.id = 'sdm'; document.body.appendChild(dlg);
    dlg.innerHTML = `<div class="sdw"><div class="sdh"><h3>Showdown set</h3><p class="hint" id="sdhint" style="margin:0"></p></div>
<div class="sdb"><textarea id="sdtxt" spellcheck="false" autocapitalize="off" aria-label="Showdown set"></textarea><p id="sdres" class="hint" style="margin:0" aria-live="polite"></p></div>
<div class="sdf"><button class="btn" data-sd="close">Close</button><span class="sp"></span><button class="btn" data-sd="copy">Copy</button><button class="btn pri" data-sd="import">Import</button></div></div>`;
    dlg.addEventListener('click', async (e) => {
      if (e.target === dlg) return dlg.close();
      const a = e.target.closest('button')?.dataset.sd; if (!a) return;
      const txt = $('sdtxt');
      if (a === 'close') return dlg.close();
      if (a === 'copy') {
        try { await navigator.clipboard.writeText(txt.value); toast('Copied.'); }
        catch { txt.select(); toast(document.execCommand('copy') ? 'Copied.' : 'Select the text and copy it manually.'); }
        return;
      }
      const text = txt.value.trim(); if (!text) return toast('Paste a Showdown set first.');
      const wasEmpty = !!S.slots[S.slot]?.empty;
      const r = call(() => J(E.ImportShowdown(text, S.box, S.slot)));
      if (!r.ok) { $('sdres').className = 'bad'; $('sdres').textContent = r.error; return; }
      dlg.close(); legalDirty = true; S.note = ''; if (wasEmpty) S.tab = 'Main';
      refresh();
      toast(r.skipped?.length ? `Imported. Couldn’t read: ${r.skipped.join('; ')}` : 'Imported.');
    });
  }
  const empty = !!S.slots[S.slot]?.empty;
  const r = empty ? { ok: true, text: '' } : call(() => J(E.ExportShowdown(S.box, S.slot)));
  $('sdtxt').value = r.text ?? '';
  $('sdtxt').placeholder = 'Species @ Item\nAbility: …\nLevel: 50\nEVs: 252 Atk / 4 Def / 252 Spe\nAdamant Nature\n- Move 1\n- Move 2';
  $('sdhint').textContent = empty ? 'Paste a set to create a Pokémon here. A legal encounter is picked automatically.' : 'Edit the text and press Import to apply it to this Pokémon (undo with ↶).';
  $('sdres').className = 'hint'; $('sdres').textContent = r.ok ? '' : r.error;
  dlg.showModal();
}

function view() {
  const P = S.props, m = by('Species'), lvl = by('CurrentLevel')?.value ?? '?';
  const id = +(m?.value ?? 0), name = N.species[id] ?? '';
  const nick = by('Nickname')?.value || name, shiny = S.slots[S.slot]?.shiny;
  const list = (re) => P.filter((p) => re.test(p.name));
  let h = '';
  if (S.tab === 'Main') {
    const ORDER = ['Species', 'Form', 'FormArgument', 'Nickname', 'IsNicknamed', 'Gender', 'IsShiny', 'IsAlpha', 'AlphaMove', 'IsEgg', 'CurrentLevel', 'EXP', 'Nature', 'StatNature', 'Ability', 'AbilityNumber', 'HeldItem', 'CurrentFriendship', 'HeightScalar', 'WeightScalar', 'Scale', 'PID', 'EncryptionConstant'];
    const rank = (n) => { const i = ORDER.indexOf(n); return i < 0 ? ORDER.length : i; };
    const fa = by('FormArgument'), need = FARG_MIN[id];
    h = `<div class="fg">${list(TAB.Main).sort((a, b) => rank(a.name) - rank(b.name)).map(ctl).join('')}</div>`
      + (fa && need && +fa.value < need ? `<p class="hint">${esc(name)} needs a form argument of at least ${need} to be legal.</p>` : '');
  }
  if (S.tab === 'Stats') {
    const ht = (x) => by('HT_' + x), hasHT = ST.some((x) => ht(x)), evCap = by('EV_HP')?.max === '252';
    const evTotal = ST.reduce((a, x) => a + (+(by('EV_' + x)?.value ?? 0)), 0);
    const c = (n, hot) => { const p = by(n); return !p ? '<td></td>' : `<td class="${hot ? 'ht-on' : ''}"><div class="sc">${ctl(p).replace(/<label[^>]*>[^<]*(?=<input)/, '<label>')}<button class="btn mx" type="button" data-max="${n}" aria-label="Max ${esc(nice(n))}">Max</button></div></td>`; };
    const box = (x) => ht(x) ? `<td><input type="checkbox" data-p="HT_${x}" aria-label="Hyper trained ${x}"${ht(x).value === 'True' ? ' checked' : ''}></td>` : '<td></td>';
    h = `<table><tr><th></th><th>IV</th><th>EV</th>${hasHT ? '<th>Hyper</th>' : ''}</tr>${ST.map((x) => `<tr><td>${x}</td>${c('IV_' + x, ht(x)?.value === 'True')}${c('EV_' + x)}${hasHT ? box(x) : ''}</tr>`).join('')}<tr><td>Total</td><td></td><td>${evTotal}${evCap ? ' / 510' : ''}</td>${hasHT ? '<td></td>' : ''}</tr></table>`;
  }
  if (S.tab === 'Moves') {
    h = [1, 2, 3, 4].map((i) => { const mv = by('Move' + i); if (!mv) return ''; const u = +(by(`Move${i}_PPUps`)?.value ?? 0);
      return `<div class="mv">${ctl(mv)}<div class="r">${by(`Move${i}_PP`) ? ctl(by(`Move${i}_PP`)) : ''}<div class="seg" role="group" aria-label="PP Ups">${[0, 1, 2, 3].map((x) => `<button data-up="Move${i}_PPUps" data-x="${x}" class="${u === x ? 'on' : ''}">${x}</button>`).join('')}</div></div></div>`; }).join('')
      + (E.PlusSupported(S.box, S.slot) ? `<h3>Plus / mastery flags</h3><p><button class="btn" type="button" data-plus="0">Set for current moves</button> <button class="btn" type="button" data-plus="1">Set all possible</button></p>` : '')
      + `<p><button class="btn" type="button" id="mvdbg">Debug move check</button></p>` + (S.dbg ? `<pre>${esc(S.dbg)}</pre>` : '')
      + (list(/^RelearnMove/).length ? `<label class="f chk" style="margin-top:14px"><input type="checkbox" id="arl" ${AUTO_RL ? 'checked' : ''}> Relearn all suggested moves</label>` : '')
      + `<h3>Relearn moves</h3><div class="fg">${list(/^RelearnMove/).map(ctl).join('')}</div>`;
  }
  if (S.tab === 'Cosmetic') {
    const rb = P.filter(isRibbon), on = rb.filter((p) => p.value === 'True').length;
    const rest = P.filter((p) => TAB.Cosmetic.test(p.name) && !rb.includes(p));
    h = `<div class="fg">${rest.map(ctl).join('')}</div>` + (rb.length
      ? `<h3>Ribbons and marks</h3><p class="hint">${on} set</p><p><button class="btn" id="rbopen">Edit ribbons and marks…</button></p>`
      : '<p class="hint">This Pokémon has no ribbon fields.</p>');
  }
  if (S.tab === 'Met') {
    const ps = list(TAB.Met);
    const origin = (p) => {
      const v = p.value ?? '', ks = p.options.filter((o) => GAME[o]); if (!ks.includes(v)) ks.unshift(v);
      return `<label class="f">Origin game<select data-p="${p.name}" data-v="${esc(v)}">${ks.map((o) => `<option value="${esc(o)}">${esc(GAME[o] ?? o)}</option>`).join('')}</select></label>`;
    };
    const one = (p) => (p.name === 'Version' && p.options ? origin(p) : ctl(p));
    const rank = (n, ks) => { const i = ks.findIndex((k) => n.includes(k)); return i < 0 ? ks.length : i; };
    const sec = (t, re, ks) => {
      const a = ps.filter((p) => re.test(p.name)).sort((x, y) => rank(x.name, ks) - rank(y.name, ks));
      return a.length ? `${t ? `<h3>${t}</h3>` : ''}<div class="fg">${a.map(one).join('')}</div>` : '';
    };
    const WHEN = ['Location', 'Level', 'Year', 'Month', 'Day'];
    h = sec('', /^(Ball|Version|Fateful)/, ['Ball', 'Version', 'Fateful']) + sec('Met information', /^Met/, WHEN) + sec('Egg information', /^Egg/, WHEN)
      || '<p class="hint">This Pokémon has no origin fields.</p>';
  }
  if (S.tab === 'OT / Misc') h = `<div class="fg">${list(TAB['OT / Misc']).filter((p) => !HT_FLAG.test(p.name)).map(ctl).join('')}</div>`;
  if (S.tab === 'All fields') h = `<label class="f">Search<input id="q" type="search" placeholder="Filter fields"></label><div class="fg all" style="margin-top:10px">${P.map(ctl).join('')}</div>`;
  if (S.tab === 'Legality') { const r = call(() => J(E.Legality(S.box, S.slot))); h = r.ok ? `<p class="${r.valid ? 'ok' : 'bad'}">${r.valid ? 'Legal' : 'Not legal'}</p><div style="margin:8px 0"><button class="btn pri" id="autofix"${r.valid ? ' disabled title="Already legal"' : ''}>Auto-legalise</button></div>${S.note ? `<p class="hint">${esc(S.note)}</p>` : ''}<pre>${esc(r.report)}</pre>` : `<p class="bad">${esc(r.error)}</p>`; }
  const tabs = ['Main', 'Stats', 'Moves', 'Cosmetic', 'Met', 'OT / Misc', 'All fields', 'Legality'];
  $('ed').innerHTML = `<div class="top"><div class="av">${img(id, shiny)}</div><div><h2>${esc(nick)}${shiny ? ' ✦' : ''}</h2><small>${esc(name)} · Lv ${esc(lvl)}</small></div><button class="btn" id="sdbtn">Showdown</button><button class="btn" id="exp">Export</button><button class="btn cl" id="cl">Close</button></div>
<div class="tabs" role="tablist">${tabs.map((t) => `<button role="tab" class="${t === S.tab ? 'on' : ''}" data-t="${t}">${t}</button>`).join('')}</div>${h}`;
  $('ed').querySelectorAll('select[data-v]').forEach((s) => { s.value = s.dataset.v; });
  enhance($('ed'));
  $('ed').classList.add('open');
}

let AUTO_RL = false; try { AUTO_RL = localStorage.getItem('autoRelearn') === '1'; } catch {}
E.SetAutoRelearn(AUTO_RL);
function refresh() { S.props = call(() => J(E.GetProps(S.box, S.slot))).props ?? S.props; loadOpts(); loadGrid(); view(); }
function edit(n, v) {
  if (LEGAL_DEP.test(n)) legalDirty = true;
  const r = call(() => J(E.SetProp(S.box, S.slot, n, String(v))));
  if (!r.ok) toast(r.error);
  if (r.ok && /_PPUps$/.test(n)) E.HealPP(S.box, S.slot);
  refresh();
}
function autoLegalise() {
  const b = $('autofix'); if (b) { b.disabled = true; b.textContent = 'Legalising…'; }
  setTimeout(() => { // let the button repaint first; the checks run on the main thread
    const r = call(() => J(E.AutoLegalise(S.box, S.slot)));
    if (!r.ok) { S.note = ''; toast(r.error); return view(); }
    legalDirty = true;
    if (!r.changed) S.note = r.message;
    else S.note = [r.steps?.length ? 'Fixed: ' + r.steps.join(', ') + '.' : '', r.dropped?.length ? "Couldn't keep: " + r.dropped.join(', ') + '.' : '',
      r.valid ? 'Now legal.' : `${r.remaining} issue(s) still need manual attention.`].filter(Boolean).join(' ');
    r.changed ? refresh() : view();
  }, 30);
}
function afterHistory(r) {
  if (!r.ok) return toast(r.error);
  legalDirty = true; S.note = '';
  if (r.kind === 'save') {
    TR.inv = TR.dex = null; trReload();
    if (S.slot >= 0 && !S.slots[S.slot]?.empty) refresh(); else loadGrid();
    return;
  }
  S.box = r.box; S.slot = r.slot;
  loadGrid();
  if (S.slots[r.slot]?.empty) return showEmpty();
  refresh();
}
function download(bytes, name) { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([bytes])); a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 2000); }

$('grid').onclick = (e) => { const b = e.target.closest('[data-s]'); if (b) pick(+b.dataset.s); };
const go = (b) => { if (!S.info) return; S.box = b; S.slot = -1; loadGrid(); empty(); };
$('boxsel').onchange = (e) => go(+e.target.value);
$('pv').onclick = () => S.info && go(S.box <= -1 ? S.info.boxes - 1 : S.box - 1);
$('nx').onclick = () => S.info && go(S.box >= S.info.boxes - 1 ? -1 : S.box + 1);
$('ed').addEventListener('input', (e) => {
  if (e.target.id !== 'q') return; const q = e.target.value.toLowerCase();
  $('ed').querySelectorAll('.all label').forEach((l) => { l.hidden = !l.textContent.toLowerCase().includes(q); });
});
$('ed').addEventListener('change', async (e) => {
  const t = e.target;
  if (t.id === 'arl') {
    AUTO_RL = t.checked; E.SetAutoRelearn(AUTO_RL); try { localStorage.setItem('autoRelearn', AUTO_RL ? '1' : '0'); } catch {}
    if (AUTO_RL) { const r = call(() => J(E.RelearnSuggested(S.box, S.slot))); r.ok ? (legalDirty = true, refresh()) : toast(r.error); }
    return;
  }
  if (t.id === 'nsp') return loadEncounters();
  if (t.dataset.p) return edit(t.dataset.p, t.type === 'checkbox' ? t.checked : t.value);
  if (t.id === 'impf' && t.files[0]) {
    const b = new Uint8Array(await t.files[0].arrayBuffer());
    const r = call(() => J(E.ImportPokemonAny(b, S.box, S.slot)));
    if (!r.ok) return toast(r.error);
    legalDirty = true; S.note = ''; pick(S.slot);
    if (r.converted) toast(`Converted ${r.converted}. ${r.legal ? 'Legal.' : 'Still not legal: see the Legality tab.'}${r.note ? ' ' + r.note : ''}`);
  }
});
$('ed').addEventListener('click', (e) => {
  const t = e.target.closest('button'); if (!t) return;
  if (t.id === 'cl') $('ed').classList.remove('open');
  else if (t.id === 'create') { const r = call(() => J(E.CreatePokemon(S.box, S.slot, +$('nsp').value, +$('nlv').value, +$('nenc').value, $('nsh').checked, !!$('nal')?.checked))); r.ok ? pick(S.slot) : toast(r.error); }
  else if (t.id === 'exp') { const b = E.ExportPokemon(S.box, S.slot); b.length ? download(b, exportName() + '.' + E.PokemonExtension(S.box, S.slot)) : toast('Export failed.'); }
  else if (t.dataset.t) { S.tab = t.dataset.t; view(); }
  else if (t.id === 'mvdbg') { const r = call(() => J(E.MoveDebug(S.box, S.slot, 84))); S.dbg = r.ok ? r.text : r.error; view(); }
  else if (t.dataset.plus) { const r = call(() => J(E.ApplyPlus(S.box, S.slot, t.dataset.plus === '1'))); r.ok ? (legalDirty = true, refresh()) : toast(r.error); }
  else if (t.dataset.up) edit(t.dataset.up, t.dataset.x);
  else if (t.dataset.max) { const v = maxFor(t.dataset.max); if (v != null) edit(t.dataset.max, v); }
  else if (t.id === 'rbopen') openRibbons();
  else if (t.id === 'sdbtn') openShowdown();
  else if (t.id === 'autofix') autoLegalise();
});
{
  const lab = document.createElement('label'); lab.className = 'btn'; lab.title = 'Automatically set Plus / mastery flags when species, level or moves change';
  const cb = document.createElement('input'); cb.type = 'checkbox'; cb.id = 'autolegal';
  let on = true; try { on = localStorage.getItem('autoLegal') !== '0'; } catch {}
  cb.checked = on; E.SetAutoLegal(on);
  cb.onchange = () => { E.SetAutoLegal(cb.checked); try { localStorage.setItem('autoLegal', cb.checked ? '1' : '0'); } catch {} };
  lab.append(cb, ' Auto legality'); $('theme').before(lab);
}
$('load').onclick = () => $('file').click();
$('file').onchange = async (e) => { const f = e.target.files[0]; if (!f) return; const b = new Uint8Array(await f.arrayBuffer()); setup(call(() => J(E.LoadSave(b, f.name)))); e.target.value = ''; };
$('newbtn').onclick = () => { $('newp').hidden = !$('newp').hidden; };
for (const g of J(E.ListGames())) $('game').add(new Option(GAME[g] ?? g, g));
$('trainer').value ||= 'Rob'; $('trainer').placeholder = 'Rob';
if ([...$('game').options].some((o) => o.value === 'ZA')) $('game').value = 'ZA';
$('mk').onclick = () => setup(call(() => J(E.NewSave($('game').value, $('trainer').value))));
$('dlsave').onclick = () => S.info ? download(E.ExportSave(), 'edited.sav') : toast('Open or create a save first.');
$('theme').onclick = () => { const r = document.documentElement, d = (r.dataset.theme || (matchMedia('(prefers-color-scheme:dark)').matches ? 'dark' : 'light')) === 'dark'; r.dataset.theme = d ? 'light' : 'dark'; };
// ---- Trainer window: trainer, items, Pokédex, adventure, dates ----
const TR_TABS = ['Trainer', 'Items', 'Pokédex', 'Adventure', 'Dates', 'All fields'];
const EPOCH2000 = Date.UTC(2000, 0, 1);
const TR_RE = {
  dates: /(Date|Year|Month|Day|SecondsTo|Epoch|Clock|Calendar|Birthday)/i,
  money: /(Money|Coin|^BP$|Points|Miles|Currency|Cash|Dollar|Dust|Candy)/i,
  adventure: /(Played|Badge|Starter|Hall|Story|Progress|Step|Wins|Gym|Rank|Champion|Elite|Battle|Trade|Rating)/i,
  trainer: /^(OT|Trainer|TID|SID|Gender|Language|Country|Region|Console|Rival|Avatar|Game|Name)/i,
};
// Which tab a field is shown on (every field is also in "All fields").
const trClass = (p) => {
  const n = p.name;
  if (p.type === 'DateTime' || p.type === 'DateOnly' || (TR_RE.dates.test(n) && !/Played/i.test(n))) return 'Dates';
  if (TR_RE.money.test(n)) return 'Items';
  if (TR_RE.adventure.test(n)) return 'Adventure';
  if (TR_RE.trainer.test(n)) return 'Trainer';
  return null;
};

function trCtl(p, showPath) {
  const v = p.value ?? '', path = esc(p.path), tt = `title="${path}"`;
  const lbl = esc(nice(p.name)) + (showPath && p.path !== p.name ? ` <span class="pth">${esc(p.path.slice(0, -p.name.length - 1))}</span>` : '');
  if (/^SecondsTo(Start|Fame)$/.test(p.name))   // seconds since 2000-01-01, shown as a date
    return `<label class="f" ${tt}>${lbl} (date)<input type="datetime-local" step="1" data-sp="${path}" data-secs="1" value="${new Date(EPOCH2000 + (+v || 0) * 1000).toISOString().slice(0, 19)}"></label>`;
  if (p.type === 'Boolean') return `<label class="f chk" ${tt}><input type="checkbox" data-sp="${path}"${v === 'True' ? ' checked' : ''}>${lbl}</label>`;
  if (p.options) return `<label class="f" ${tt}>${lbl}<select data-sp="${path}">${p.options.map((o) => `<option${o === v ? ' selected' : ''}>${esc(o)}</option>`).join('')}</select></label>`;
  if (p.type === 'DateTime') return `<label class="f" ${tt}>${lbl}<input type="datetime-local" step="1" data-sp="${path}" value="${esc(v)}"></label>`;
  if (p.type === 'DateOnly') return `<label class="f" ${tt}>${lbl}<input type="date" data-sp="${path}" value="${esc(v)}"></label>`;
  if (NUM.test(p.type)) {
    const lim = Number.isSafeInteger(+p.min) && Number.isSafeInteger(+p.max) ? ` min="${p.min}" max="${p.max}"` : '';
    return `<label class="f" ${tt}>${lbl}<input type="number" step="1"${lim} data-sp="${path}" value="${esc(v)}"></label>`;
  }
  if (/^(Single|Double)$/.test(p.type)) return `<label class="f" ${tt}>${lbl}<input type="number" step="any" data-sp="${path}" value="${esc(v)}"></label>`;
  return `<label class="f" ${tt}>${lbl}<input data-sp="${path}" value="${esc(v)}"></label>`;
}

function trFields(tab) {
  const ps = TR.props.filter((p) => trClass(p) === tab);
  return ps.length ? `<div class="fg">${ps.map((p) => trCtl(p)).join('')}</div>`
    : `<p class="hint">No ${tab.toLowerCase()} fields were found for this game. Try “All fields”.</p>`;
}

function trItems() {
  const money = TR.props.filter((p) => trClass(p) === 'Items');
  let h = money.length ? `<h3 style="margin-top:0">Money and currencies</h3><div class="fg">${money.map((p) => trCtl(p)).join('')}</div>` : '';
  const inv = TR.inv;
  if (!inv?.ok) return h + `<p class="bad">${esc(inv?.error ?? 'Items unavailable.')}</p>`;
  if (!inv.supported) return h + '<p class="hint">Item editing isn’t available for this game yet.</p>';
  const nm = (i) => N.items[i] || '#' + i;
  const real = (i) => i > 0 && N.items[i] && !/^(\?\?\?|\()/.test(N.items[i]);
  h += '<h3>Pouches</h3>' + inv.pouches.map((p) => {
    const ids = (p.legal ?? N.items.map((_, i) => i)).filter(real).sort((a, b) => N.items[a].localeCompare(N.items[b]));
    const rows = p.items.map((it) => `<tr><td>${esc(nm(it.item))}</td><td><input type="number" min="0" max="${p.max}" step="1" value="${it.count}" data-ic="${p.index}:${it.slot}" aria-label="Count"></td><td><button class="btn" data-irm="${p.index}:${it.slot}" aria-label="Remove ${esc(nm(it.item))}">✕</button></td></tr>`).join('');
    const body = p.editable
      ? `${rows ? `<table>${rows}</table>` : '<p class="hint">Empty.</p>'}`
        + `<div class="tradd"><select id="ia${p.index}" aria-label="Item to add">${ids.map((i) => `<option value="${i}">${esc(N.items[i])}</option>`).join('')}</select><input type="number" id="ic${p.index}" min="1" max="${p.max}" value="${Math.min(p.max, 99)}" aria-label="Amount"><button class="btn pri" data-iadd="${p.index}">Add</button></div>`
        + `<div class="tradd"><button class="btn" data-imax="${p.index}">Max counts</button><button class="btn" data-iall="${p.index}">Add all legal</button><button class="btn" data-iclr="${p.index}">Clear pouch</button></div>`
      : '<p class="hint">This pouch can’t be edited.</p>';
    return `<details data-pouch="${p.index}"${TR.open.has(p.index) ? ' open' : ''}><summary>${esc(p.name)} <small class="hint">${p.items.length} of ${p.size} slots</small></summary>${body}</details>`;
  }).join('');
  return h;
}

function trDex() {
  const d = TR.dex;
  if (!d?.ok) return `<p class="bad">${esc(d?.error ?? 'Pokédex unavailable.')}</p>`;
  if (!d.supported) return '<p class="hint">Pokédex editing isn’t available for this game yet.</p>';
  const seen = new Set(d.seen), caught = new Set(d.caught), rows = [];
  for (let i = 1; i <= d.max; i++) {
    const n = N.species[i]; if (!n) continue;
    rows.push(`<div class="dxr" data-q="${esc(`${i} ${n}`.toLowerCase())}"><span>#${i} ${esc(n)}</span><span class="dxc"><label><input type="checkbox" data-dx="${i}" data-k="s"${seen.has(i) ? ' checked' : ''}> Seen</label><label><input type="checkbox" data-dx="${i}" data-k="c"${caught.has(i) ? ' checked' : ''}> Caught</label></span></div>`);
  }
  return `<div class="trdxh"><b id="dxn">${seen.size} seen · ${caught.size} caught</b><span class="sp"></span><button class="btn" data-dxall="s">Mark all seen</button><button class="btn" data-dxall="c">Mark all caught</button><button class="btn" data-dxall="n">Clear all</button></div>`
    + `<input id="trq" type="search" placeholder="Filter by name or number" style="width:100%;margin:10px 0"><div>${rows.join('')}</div>`;
}

function trBody() {
  if (TR.tab === 'Items') { TR.inv ??= call(() => J(E.GetInventory())); return trItems(); }
  if (TR.tab === 'Pokédex') { TR.dex ??= call(() => J(E.GetDex())); return trDex(); }
  if (TR.tab === 'All fields') return `<input id="trq" type="search" placeholder="Filter fields (name or path)" style="width:100%;margin-bottom:10px"><div class="fg all">${TR.props.map((p) => trCtl(p, true)).join('')}</div>`;
  return trFields(TR.tab);
}

function trFilter() {
  const inp = $('trq'); if (!inp) return; const q = inp.value.toLowerCase();
  $('trm').querySelectorAll('.fg.all > label, .dxr').forEach((l) => { l.hidden = !!q && !(l.dataset.q ?? `${l.textContent} ${l.title}`).toLowerCase().includes(q); });
}

function trRender(reset) {
  const dlg = $('trm'); if (!dlg) return;
  const keep = reset ? 0 : dlg.querySelector('#trb')?.scrollTop ?? 0;
  const tabs = TR_TABS.map((t) => `<button role="tab" class="${t === TR.tab ? 'on' : ''}" data-trt="${t}">${t}</button>`).join('');
  dlg.innerHTML = `<div class="trw"><div class="trh"><h3>Trainer and save data</h3><div class="tabs" role="tablist">${tabs}</div></div><div class="trb" id="trb">${trBody()}</div>`
    + `<div class="trf"><span class="hint" style="margin:0">Changes apply immediately. Undo them with ↶.</span><span class="sp"></span><button class="btn pri" data-trclose="1">Done</button></div></div>`;
  dlg.querySelector('#trb').scrollTop = keep;
  trFilter();
}

function trReload() {
  const dlg = $('trm'); if (!dlg?.open) return;
  const r = call(() => J(E.GetSaveProps())); if (r.ok) TR.props = r.props;
  trRender();
}

const trOp = (fn) => { const r = call(() => J(fn())); if (!r.ok) toast(r.error); TR.inv = null; trRender(); updateHist(); };

function trSet(el) {
  const path = el.dataset.sp;
  let v = el.type === 'checkbox' ? String(el.checked) : el.value;
  if (el.dataset.secs) { const s = Math.round((Date.parse(el.value + 'Z') - EPOCH2000) / 1000); if (!Number.isFinite(s)) return trRender(); v = String(s); }
  const r = call(() => J(E.SetSaveProp(path, v)));
  if (!r.ok) { toast(r.error); const p = TR.props.find((x) => x.path === path); if (p) el.value = p.value ?? ''; return trRender(); }
  const p = TR.props.find((x) => x.path === path); if (p) p.value = r.value;
  if (el.type === 'number' && r.value != null) el.value = r.value;   // the save may have clamped it
  updateHist();
}

function trOpen() {
  if (!S.info) return toast('Open or create a save first.');
  let dlg = $('trm');
  if (!dlg) {
    dlg = document.createElement('dialog'); dlg.id = 'trm'; document.body.appendChild(dlg);
    dlg.addEventListener('close', () => { if (S.slot >= 0 && S.props.length) refresh(); });
    dlg.addEventListener('toggle', (e) => {
      const d = e.target.closest?.('details[data-pouch]'); if (!d) return;
      d.open ? TR.open.add(+d.dataset.pouch) : TR.open.delete(+d.dataset.pouch);
    }, true);
    dlg.addEventListener('input', (e) => { if (e.target.id === 'trq') trFilter(); });
    dlg.addEventListener('change', (e) => {
      const t = e.target;
      if (t.dataset.sp) return trSet(t);
      if (t.dataset.ic) { const [pi, sl] = t.dataset.ic.split(':').map(Number); return trOp(() => E.InvSetCount(pi, sl, Math.trunc(+t.value) || 0)); }
      if (t.dataset.dx) {
        const row = t.closest('.dxr'), s = row.querySelector('[data-k=s]'), c = row.querySelector('[data-k=c]');
        if (t.dataset.k === 'c' && c.checked) s.checked = true;
        if (t.dataset.k === 's' && !s.checked) c.checked = false;
        const r = call(() => J(E.SetDexEntry(+t.dataset.dx, s.checked, c.checked)));
        TR.dex = null;
        if (!r.ok) { toast(r.error); return trRender(); }
        $('dxn').textContent = `${dlg.querySelectorAll('[data-k=s]:checked').length} seen · ${dlg.querySelectorAll('[data-k=c]:checked').length} caught`;
        updateHist();
      }
    });
    dlg.addEventListener('click', (e) => {
      if (e.target === dlg) return dlg.close();
      const b = e.target.closest('button'); if (!b) return;
      const d = b.dataset;
      if (d.trclose) return dlg.close();
      if (d.trt) { TR.tab = d.trt; return trRender(true); }
      const pair = (s) => s.split(':').map(Number);
      if (d.irm) { const [pi, sl] = pair(d.irm); return trOp(() => E.InvRemove(pi, sl)); }
      if (d.iadd) { const pi = +d.iadd; return trOp(() => E.InvAdd(pi, +$(`ia${pi}`).value, Math.trunc(+$(`ic${pi}`).value) || 1)); }
      if (d.imax) return trOp(() => E.InvMax(+d.imax));
      if (d.iall) return trOp(() => E.InvAddAllLegal(+d.iall));
      if (d.iclr) return trOp(() => E.InvClear(+d.iclr));
      if (d.dxall) {
        const seen = d.dxall !== 'n', caught = d.dxall === 'c';
        const r = call(() => J(E.SetDexAll(seen, caught)));
        if (!r.ok) toast(r.error);
        TR.dex = null; trRender(); updateHist();
      }
    });
  }
  dlg.innerHTML = '<div class="trw"><div class="trh"><h3>Trainer and save data</h3></div><div class="trb"><p class="hint" style="margin:0">Reading save data…</p></div></div>';
  dlg.showModal();
  setTimeout(() => {   // reading every field can take a moment
    const r = call(() => J(E.GetSaveProps()));
    if (!r.ok) { dlg.close(); return toast(r.error); }
    TR.props = r.props; TR.inv = TR.dex = null; trRender(true);
  }, 30);
}

// ---- top bar: File menu and undo/redo ----
{
  $('trbtn').onclick = trOpen;
  const fb = $('filebtn'), fm = $('filemenu');
  const close = () => { fm.hidden = true; fb.setAttribute('aria-expanded', 'false'); };
  fb.onclick = (e) => { e.stopPropagation(); fm.hidden = !fm.hidden; fb.setAttribute('aria-expanded', String(!fm.hidden)); };
  document.addEventListener('click', (e) => { if (!fm.contains(e.target) && e.target !== fb) close(); });
  addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
  for (const id of ['load', 'dlsave', 'mk']) $(id).addEventListener('click', close);   // "New blank save" stays open so its form can be filled in
  $('undobtn').onclick = () => afterHistory(call(() => J(E.UndoEdit())));
  $('redobtn').onclick = () => afterHistory(call(() => J(E.RedoEdit())));
  addEventListener('keydown', (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName ?? '')) return;
    const k = e.key.toLowerCase();
    if (k === 'z' && !e.shiftKey) { e.preventDefault(); $('undobtn').click(); }
    else if (k === 'y' || (k === 'z' && e.shiftKey)) { e.preventDefault(); $('redobtn').click(); }
  });
  updateHist();
}

// ---- top bar: Tools menu (legal living dex) ----
{
  const tb = $('toolsbtn'), tm = $('toolsmenu'), stat = $('ldstat'), stopBtn = $('ldstop');
  let running = false, stop = false;
  const close = () => { if (running) return; tm.hidden = true; tb.setAttribute('aria-expanded', 'false'); };
  tb.onclick = (e) => { e.stopPropagation(); $('filemenu').hidden = true; $('filebtn').setAttribute('aria-expanded', 'false'); tm.hidden = !tm.hidden; tb.setAttribute('aria-expanded', String(!tm.hidden)); };
  $('filebtn').addEventListener('click', () => { if (!running) close(); });
  document.addEventListener('click', (e) => { if (!tm.contains(e.target) && e.target !== tb) close(); });
  addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
  stopBtn.onclick = () => { stop = true; stopBtn.disabled = true; };

  async function livingDex(shiny) {
    if (running) return;
    if (!S.info) return toast('Open or create a save first.');
    const plan = call(() => J(E.LivingDexPlan()));
    if (!plan.ok) return toast(plan.error);
    const list = plan.species.slice(0, plan.capacity);
    if (!confirm(`Fill up to ${list.length} slots from Box 1, slot 1 with a legal ${shiny ? 'shiny ' : ''}living dex? Pokémon in those slots will be replaced (undo only covers the last 200 edits).`)) return;
    running = true; stop = false; stopBtn.hidden = false; stopBtn.disabled = false; stat.hidden = false;
    for (const id of ['ldn', 'lds', 'ldall', 'bebtn']) $(id).disabled = true;
    let placed = 0, shinies = 0, cross = 0, goCount = 0; const skipped = [];
    for (let i = 0; i < list.length && !stop; i++) {
      stat.textContent = `Building ${i + 1} / ${list.length}…`;
      await new Promise((r) => setTimeout(r, 0));   // let the page repaint between species
      const r = call(() => J(E.LivingDexAdd(placed, list[i], shiny)));
      if (r.ok) { placed++; if (r.shiny) shinies++; if (r.cross) cross++; if (r.go) goCount++; } else skipped.push(`#${list[i]} (${String(r.error ?? '').slice(0, 600)})`);
    }
    running = false; stopBtn.hidden = true; for (const id of ['ldn', 'lds', 'ldall', 'bebtn']) $(id).disabled = false;
    stat.textContent = `${stop ? 'Stopped. ' : ''}Placed ${placed}` + (shiny ? ` (${shinies} shiny, ${placed - shinies} normal because no legal shiny exists)` : '')
      + (goCount ? `, ${goCount} from Pokémon GO` : '')
      + (cross ? `, ${cross} from another game of this generation (traded in)` : '')
      + (skipped.length ? `. Skipped ${skipped.length}: ${skipped.slice(0, 12).join('; ')}${skipped.length > 12 ? '…' : ''}.` : '.');
    legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist();
  }
  async function legaliseAll() {
    if (running) return;
    if (!S.info) return toast('Open or create a save first.');
    const plan = call(() => J(E.OccupiedSlots()));
    if (!plan.ok) return toast(plan.error);
    if (!plan.slots.length) return toast('No Pokémon to check.');
    if (!confirm(`Check ${plan.slots.length} Pokémon and automatically fix any that are illegal? Fixes can change moves, met data and other fields (undo only covers the last 200 edits).`)) return;
    running = true; stop = false; stopBtn.hidden = false; stopBtn.disabled = false; stat.hidden = false;
    for (const id of ['ldn', 'lds', 'ldall', 'bebtn']) $(id).disabled = true;
    let legal = 0, fixed = 0, partial = 0, failed = 0, done = 0;
    for (const [bx, sl] of plan.slots) {
      if (stop) break;
      stat.textContent = `Checking ${done + 1} / ${plan.slots.length}… (fixed ${fixed})`;
      await new Promise((r) => setTimeout(r, 0));
      const r = call(() => J(E.AutoLegalise(bx, sl)));
      done++;
      if (!r.ok) failed++;
      else if (r.valid && !r.changed) legal++;
      else if (r.changed && r.valid) fixed++;
      else if (r.changed) partial++;
      else failed++;
    }
    running = false; stopBtn.hidden = true;
    for (const id of ['ldn', 'lds', 'ldall', 'bebtn']) $(id).disabled = false;
    stat.textContent = `${stop ? `Stopped after ${done}. ` : ''}${legal} already legal, ${fixed} fixed, ${partial} improved but still need attention, ${failed} couldn't be fixed automatically.`;
    legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist();
  }
  $('ldall').onclick = legaliseAll;
  $('ldn').onclick = () => livingDex(false);
  $('lds').onclick = () => livingDex(true);
}
// ---- Tools menu: batch editor (PKHeX-style filters and instructions) ----
{
  const BE = { running: false, stop: false, script: '', scope: 'box' };
  const PLACEHOLDER = '# One instruction per line, for example:\n=Species=Pikachu\n.CurrentLevel=100\n.IsShiny=True';
  const HELP = `One instruction per line. Lines starting with # are ignored.

FILTERS (only Pokémon that pass every filter are changed)
=Species=Pikachu      equals
!Species=Pikachu      does not equal
>CurrentLevel=50      greater than (also <  >=  <=  ≥  ≤)
=Legal=False          only illegal Pokémon

INSTRUCTIONS
.CurrentLevel=100     set a value
.Move1=Thunderbolt    names work for species, moves, items,
.Nature=Adamant       abilities, natures, balls, languages, forms…
.IsShiny=True
.CurrentLevel=+5      add / subtract / multiply / divide  (+ - * /)
.Species=$rand        random value for that field

SPECIAL FIELDS
.Moves=$suggest       best moveset for the encounter
.RelearnMoves=$suggest
.Ribbons=$suggest     (or $all / $none)
.IVs=31   .IVs=$rand  .EVs=0   .EVs=$rand

Fields that don't exist for a Pokémon's format are skipped and listed in the result.
Preview shows what would change; Run applies it as one Undo step.`;
  const show = (h) => { $('beres').innerHTML = h; };
  const busy = (on) => {
    BE.running = on; $('bestop').hidden = !on;
    for (const id of ['bepre', 'berun', 'beadd', 'bescope']) $(id).disabled = on;
    $('bescript').readOnly = on;
  };
  const summary = (f, apply) => {
    let h = `<p><b>${apply ? 'Done.' : 'Preview.'}</b> Checked ${f.scanned}, matched ${f.matched}, ${apply ? 'changed' : 'would change'} ${f.modified}${f.unchanged ? ` (${f.unchanged} unchanged)` : ''}.</p>`;
    h += f.samples.map((s) => `<div class="bes"><b>${esc(s.name)}</b> <span class="pth">${esc(s.at)}</span><br>${s.changes.map((c) => esc(c)).join(' · ')}</div>`).join('');
    if (f.modified > f.samples.length) h += `<p class="hint">…and ${f.modified - f.samples.length} more.</p>`;
    if (f.errors.length) h += `<p class="bad">Problems:</p><ul>${f.errors.map((x) => `<li>${esc(x.message)} <span class="pth">×${x.count}, first at ${esc(x.at)}</span></li>`).join('')}</ul>`;
    if (f.missing.length) h += `<p class="hint">Skipped where the field doesn't exist for that Pokémon's format: ${f.missing.map((m) => `${esc(m.name)} (${m.count})`).join(', ')}.</p>`;
    return h;
  };

  async function run(apply) {
    if (BE.running) return;
    const script = $('bescript').value, scope = $('bescope').value;
    BE.script = script; BE.scope = scope;
    const b = call(() => J(E.BatchBegin(script, scope, S.box)));
    if (!b.ok) return show(`<p class="bad">${esc(b.error).replace(/\n/g, '<br>')}</p>`);
    if (!b.total) { E.BatchCancel(); return show('<p class="hint">There are no slots in that range.</p>'); }
    if (apply && !b.sets) { E.BatchCancel(); return show('<p class="bad">Nothing to run: add at least one instruction starting with “.”, for example .CurrentLevel=100</p>'); }
    if (apply && !confirm(`Run ${b.sets} instruction${b.sets === 1 ? '' : 's'} on up to ${b.total} slots? One Undo reverts the whole batch.`)) { E.BatchCancel(); return; }
    BE.stop = false; busy(true); $('bestop').disabled = false;
    let done = 0, failed = '';
    while (done < b.total && !BE.stop) {
      const r = call(() => J(E.BatchStep(8)));
      if (!r.ok) { failed = r.error; break; }
      done = r.done;
      show(`<p class="hint">${apply ? 'Running' : 'Previewing'}… ${done} / ${b.total}</p>`);
      await new Promise((res) => setTimeout(res, 0));   // let the page repaint between chunks
    }
    busy(false);
    if (failed || BE.stop) { E.BatchCancel(); return show(failed ? `<p class="bad">${esc(failed)}</p>` : '<p class="hint">Stopped. Nothing was changed.</p>'); }
    const f = call(() => J(E.BatchFinish(apply)));
    if (!f.ok) return show(`<p class="bad">${esc(f.error)}</p>`);
    show(summary(f, apply));
    if (apply && f.modified) { legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist(); }
  }

  function ensure() {
    let dlg = $('bem'); if (dlg) return dlg;
    dlg = document.createElement('dialog'); dlg.id = 'bem'; document.body.appendChild(dlg);
    dlg.innerHTML = `<div class="bew"><div class="beh"><h3>Batch editor</h3><label>Apply to <select id="bescope"><option value="box"></option><option value="boxes">All boxes</option><option value="party">Party</option><option value="all">Entire save</option></select></label></div>
<div class="beb"><div class="beadd"><select id="bemode" aria-label="Instruction type"><option value=".">Set</option><option value="=">Require =</option><option value="!">Exclude ≠</option><option value="&gt;">Require &gt;</option><option value="&lt;">Require &lt;</option><option value="≥">Require ≥</option><option value="≤">Require ≤</option></select><input id="beprop" list="beprops" placeholder="Property" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="Property"><datalist id="beprops"></datalist><input id="beval" placeholder="Value" autocomplete="off" autocapitalize="off" spellcheck="false" aria-label="Value"><button class="btn" id="beadd">Add</button></div>
<textarea id="bescript" spellcheck="false" autocapitalize="off" placeholder="${esc(PLACEHOLDER)}" aria-label="Batch instructions"></textarea>
<details><summary>Syntax</summary><pre>${esc(HELP)}</pre></details><div class="beres" id="beres" aria-live="polite"></div></div>
<div class="bef"><button class="btn" id="beclose">Close</button><span class="sp"></span><button class="btn" id="bestop" hidden>Stop</button><button class="btn" id="bepre">Preview</button><button class="btn pri" id="berun">Run</button></div></div>`;
    dlg.addEventListener('click', (e) => { if (e.target === dlg && !BE.running) dlg.close(); });
    dlg.addEventListener('close', () => { BE.stop = true; BE.script = $('bescript').value; BE.scope = $('bescope').value; });
    $('beclose').onclick = () => dlg.close();
    $('bepre').onclick = () => run(false);
    $('berun').onclick = () => run(true);
    $('bestop').onclick = () => { BE.stop = true; $('bestop').disabled = true; };
    $('beadd').onclick = () => {
      const prop = $('beprop').value.trim(); if (!prop) return $('beprop').focus();
      const ta = $('bescript'), line = `${$('bemode').value}${prop}=${$('beval').value.trim()}`;
      ta.value = ta.value.replace(/\s+$/, '') + (ta.value.trim() ? '\n' : '') + line + '\n';
      $('beval').value = ''; ta.scrollTop = ta.scrollHeight;
    };
    $('beval').onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); $('beadd').click(); } };
    return dlg;
  }

  $('bebtn').onclick = () => {
    $('toolsmenu').hidden = true; $('toolsbtn').setAttribute('aria-expanded', 'false');
    if (!S.info) return toast('Open or create a save first.');
    const dlg = ensure();
    const f = call(() => J(E.BatchFields()));
    $('beprops').innerHTML = (f.props ?? []).map((n) => `<option value="${esc(n)}">`).join('');
    $('bescope').options[0].textContent = `Current view (${S.box < 0 ? 'Party' : 'Box ' + (S.box + 1)})`;
    $('bescope').value = BE.scope; $('bescript').value = BE.script; show('');
    dlg.showModal();
  };
}
// ---- Box tools: import / export many Pokémon files (up to one box) ----
{
  const MAX = 30;
  document.head.appendChild(Object.assign(document.createElement('style'), { textContent:
    '.mbar{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}.mbar .hint{flex-basis:100%;margin:0}' }));
  const bar = document.createElement('div'); bar.className = 'mbar';
  bar.innerHTML = '<button class="btn" id="mbimp" title="Import up to 30 Pokémon files into empty slots">Import files…</button><button class="btn" id="mbexp" title="Download this box as a zip of Pokémon files">Export box…</button><input type="file" id="mbfile" multiple hidden><p class="hint" id="mbstat" hidden></p>';
  $('hint').before(bar);

  const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
  const crc32 = (d) => { let c = ~0; for (let i = 0; i < d.length; i++) c = CRC[(c ^ d[i]) & 255] ^ (c >>> 8); return ~c >>> 0; };
  const u16 = (v) => [v & 255, (v >> 8) & 255], u32 = (v) => [v & 255, (v >> 8) & 255, (v >> 16) & 255, (v >>> 24) & 255];
  // Minimal zip writer (stored, no compression): Pokémon files are tiny.
  const makeZip = (files) => {
    const te = new TextEncoder(), parts = [], cd = []; let off = 0;
    for (const f of files) {
      const nm = te.encode(f.name), crc = crc32(f.data), sz = f.data.length;
      parts.push(new Uint8Array([0x50, 0x4b, 3, 4, ...u16(20), ...u16(0x800), ...u16(0), ...u16(0), ...u16(0x21), ...u32(crc), ...u32(sz), ...u32(sz), ...u16(nm.length), ...u16(0)]), nm, f.data);
      cd.push(new Uint8Array([0x50, 0x4b, 1, 2, ...u16(20), ...u16(20), ...u16(0x800), ...u16(0), ...u16(0), ...u16(0x21), ...u32(crc), ...u32(sz), ...u32(sz), ...u16(nm.length), ...u16(0), ...u16(0), ...u16(0), ...u16(0), ...u32(0), ...u32(off)]), nm);
      off += 30 + nm.length + sz;
    }
    const cdSize = cd.reduce((a, x) => a + x.length, 0);
    return new Blob([...parts, ...cd, new Uint8Array([0x50, 0x4b, 5, 6, 0, 0, 0, 0, ...u16(files.length), ...u16(files.length), ...u32(cdSize), ...u32(off), 0, 0])], { type: 'application/zip' });
  };
  const clean = (x) => String(x ?? '').replace(/[\\/:*?"<>|]/g, '').trim().replace(/\s+/g, '_');
  const say = (t, bad) => { const e = $('mbstat'); e.hidden = !t; e.className = bad ? 'hint bad' : 'hint'; e.textContent = t; };

  $('mbexp').onclick = () => {
    if (!S.info) return toast('Open or create a save first.');
    const files = S.slots.filter((s) => !s.empty).slice(0, MAX).map((s) => ({
      name: `${String(s.slot + 1).padStart(2, '0')}-${String(s.id).padStart(4, '0')}-${clean(N.species[s.id]) || 'Unknown'}.${E.PokemonExtension(S.box, s.slot)}`,
      data: E.ExportPokemon(S.box, s.slot).slice(),
    })).filter((f) => f.data.length);
    if (!files.length) return toast('Nothing to export here.');
    download(makeZip(files), S.box < 0 ? 'party.zip' : `box-${S.box + 1}.zip`);
    say(`Exported ${files.length} Pokémon.`);
  };

  $('mbimp').onclick = () => (S.info ? $('mbfile').click() : toast('Open or create a save first.'));
  $('mbfile').onchange = async (e) => {
    let files = [...e.target.files]; e.target.value = ''; if (!files.length) return;
    const extra = Math.max(0, files.length - MAX); files = files.slice(0, MAX);
    const start = Math.max(0, S.box); let placed = 0, first = null, last = null, full = 0, conv = 0, illegal = 0; const bad = [];
    call(() => J(E.MultiImportBegin()));
    for (const f of files) {
      const bytes = new Uint8Array(await f.arrayBuffer());
      const r = call(() => J(E.MultiImportNext(bytes, start)));
      if (r.ok) { placed++; first ??= r; last = r; if (r.converted) { conv++; if (!r.legal) illegal++; } }
      else if (r.full) { full = files.length - placed - bad.length; break; }
      else bad.push(`${f.name} (${r.error})`);
    }
    E.MultiImportEnd();
    const pos = (r) => `Box ${r.box + 1} slot ${r.slot + 1}`;
    const parts = [placed ? `Imported ${placed} (${pos(first)}${placed > 1 ? ` to ${pos(last)}` : ''}).` : 'Nothing imported.'];
    if (conv) parts.push(`${conv} converted from an older format${illegal ? `, ${illegal} still not legal` : ''}.`);
    if (full) parts.push(`${full} didn’t fit: no empty slots left.`);
    if (extra) parts.push(`Only the first ${MAX} files are used; ${extra} skipped.`);
    if (bad.length) parts.push(`Failed: ${bad.slice(0, 3).join('; ')}${bad.length > 3 ? ` and ${bad.length - 3} more` : ''}.`);
    say(parts.join(' '), !placed || bad.length || full);
    if (placed) { legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist(); }
  };
}
// ---- Toolbar under the top bar: copy, delete, sort all boxes ----
{
  document.head.appendChild(Object.assign(document.createElement('style'), { textContent:
    '.tbar{display:flex;gap:8px;align-items:center;flex-wrap:wrap;padding:8px 14px;background:var(--pn);border-bottom:1px solid var(--bd)}.tbar .hint{margin:0;flex:1;min-width:140px}' }));
  const bar = document.createElement('div'); bar.className = 'tbar'; bar.setAttribute('role', 'toolbar'); bar.setAttribute('aria-label', 'Box tools');
  bar.innerHTML = `<button class="btn" id="tbcopy" title="Copy the selected Pokémon into the next empty slot" disabled>Copy</button><button class="btn" id="tbdel" title="Delete the selected Pokémon" disabled>Delete</button>
<select class="btn" id="tbsort" aria-label="Sort all boxes"><option value="">Sort ▾</option><option value="num-asc">Numerical 1-1025</option><option value="num-desc">Numerical 1025-1</option><option value="alpha-asc">Alphabetical A-Z</option><option value="alpha-desc">Alphabetical Z-A</option><option value="lvl-asc">Level 1-100</option><option value="lvl-desc">Level 100-1</option><option value="legal">Legal first</option><option value="illegal">Illegal first</option></select><span class="hint" id="tbstat" aria-live="polite"></span>`;
  document.querySelector('header').after(bar);
  const say = (t) => { $('tbstat').textContent = t; };

  $('tbcopy').onclick = () => {
    if ($('tbcopy').disabled) return;
    const r = call(() => J(E.CopySlot(S.box, S.slot)));
    if (!r.ok) return toast(r.error);
    loadGrid(); updateHist(); say(`Copied to Box ${r.box + 1}, slot ${r.slot + 1}.`);
  };
  $('tbdel').onclick = () => {
    if ($('tbdel').disabled) return;
    const r = call(() => J(E.DeleteSlot(S.box, S.slot)));
    if (!r.ok) return toast(r.error);
    legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist(); say('Deleted. Undo with ↶.');
  };
  $('tbsort').onchange = async (e) => {
    const sel = e.target, mode = sel.value, label = sel.selectedOptions[0].textContent; sel.value = '';
    if (!mode) return;
    if (!S.info) return toast('Open or create a save first.');
    const b = call(() => J(E.SortBegin(mode)));
    if (!b.ok) return toast(b.error);
    const lock = (on) => { for (const el of [document.querySelector('header'), document.querySelector('main'), bar]) el.inert = on; };
    lock(true);
    try {
      for (let done = 0; done < b.total;) {
        const r = call(() => J(E.SortStep(10)));
        if (!r.ok) return toast(r.error);
        done = r.done; say(`Checking legality… ${done} / ${b.total}`);
        await new Promise((res) => setTimeout(res, 0));   // let the page repaint between chunks
      }
      const f = call(() => J(E.SortFinish(mode)));
      if (!f.ok) return toast(f.error);
      say(f.changed ? `Sorted ${f.count} Pokémon (${label}). Undo with ↶.` : 'Already in that order.');
      if (f.changed) { legalDirty = true; S.slot = -1; empty(); loadGrid(); updateHist(); }
    } finally { lock(false); }
  };
  syncTools();
}
empty();
