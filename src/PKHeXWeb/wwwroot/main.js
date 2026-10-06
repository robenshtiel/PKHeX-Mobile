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
  Main: /^(Species|Nickname|IsNicknamed|IsShiny|IsAlpha|AlphaMove|CurrentLevel|EXP|Nature|StatNature|Ability|AbilityNumber|HeldItem|Gender|Form|IsEgg|CurrentFriendship|HeightScalar|WeightScalar|Scale|Tera\w*|PID|EncryptionConstant)$/,
  Moves: /^(Move[1-4]|RelearnMove[1-4])(_PP|_PPUps)?$/,
  Cosmetic: /^(Contest|Marking|Ribbon|AffixedRibbon|HasBattle|HasContest)/,
  Met: /^(Ball|Version|Fateful|Met|Egg)/,
  'OT / Misc': /^(OriginalTrainer|OT_|HandlingTrainer|HT_|TID|SID|Language|Geo)/,
};
// Friendly names for the Origin game dropdown. Only games listed here are offered (plus the current value).
const GAME = { RD: 'Red', GN: 'Green', BU: 'Blue', YW: 'Yellow', GD: 'Gold', SI: 'Silver', C: 'Crystal', R: 'Ruby', S: 'Sapphire', E: 'Emerald', FR: 'FireRed', LG: 'LeafGreen', CXD: 'Colosseum / XD', D: 'Diamond', P: 'Pearl', Pt: 'Platinum', HG: 'HeartGold', SS: 'SoulSilver', B: 'Black', W: 'White', B2: 'Black 2', W2: 'White 2', X: 'X', Y: 'Y', OR: 'Omega Ruby', AS: 'Alpha Sapphire', SN: 'Sun', MN: 'Moon', US: 'Ultra Sun', UM: 'Ultra Moon', GO: 'Pokémon GO', GP: 'Let’s Go, Pikachu!', GE: 'Let’s Go, Eevee!', SW: 'Sword', SH: 'Shield', BD: 'Brilliant Diamond', SP: 'Shining Pearl', PLA: 'Legends: Arceus', SL: 'Scarlet', VL: 'Violet', ZA: 'Legends: Z-A' };
const S = { info: null, box: 0, slot: -1, slots: [], props: [], opts: {}, legal: {}, tab: 'Main' };
let legalDirty = true;
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

function loadGrid() {
  const r = call(() => J(S.box < 0 ? E.GetParty() : E.GetBox(S.box)));
  S.slots = r.slots ?? [];
  $('grid').innerHTML = S.slots.map((s) => s.empty
    ? `<button class="slot${s.slot === S.slot ? ' sel' : ''}" data-s="${s.slot}" aria-label="Empty slot"></button>`
    : `<button class="slot f${s.slot === S.slot ? ' sel' : ''}" data-s="${s.slot}" aria-label="${esc(s.nick)} level ${s.level}">${img(s.id, s.shiny)}<span>${s.level}</span></button>`).join('');
  $('boxsel').value = S.box;
}

function setup(info) {
  if (!info.ok) return toast(info.error);
  S.info = info; S.slot = -1;
  $('boxsel').innerHTML = `<option value="-1">Party</option>` + Array.from({ length: info.boxes }, (_, i) => `<option value="${i}">Box ${i + 1}</option>`).join('');
  S.box = info.party > 0 ? -1 : 0;
  $('hint').textContent = `${info.game} · generation ${info.generation} · trainer ${info.ot}`;
  $('newp').hidden = true; loadGrid(); empty();
}

function empty() { $('ed').classList.remove('open'); $('ed').innerHTML = '<p class="hint" style="margin:0">Select a Pokémon to edit it, or an empty slot to add one.</p>'; }

function pick(i) {
  S.slot = i; const s = S.slots[i]; loadGrid();
  if (s.empty) return showEmpty();
  S.props = call(() => J(E.GetProps(S.box, i))).props ?? []; legalDirty = true; loadOpts(); S.tab = 'Main'; view();
}

function showEmpty() {
  const ed = $('ed');
  ed.innerHTML = S.box < 0
    ? '<div class="top"><div><h2>Empty party slot</h2><small>Adding to the party isn’t supported yet. Use a box.</small></div><button class="btn cl" id="cl">Close</button></div>'
    : `<div class="top"><div><h2>Empty slot</h2><small>Box ${S.box + 1}, slot ${S.slot + 1}</small></div><button class="btn cl" id="cl">Close</button></div>
<div class="fg"><label class="f">Species<select id="nsp">${listOpts('species')}</select></label><label class="f">Level<input id="nlv" type="number" min="1" max="100" value="50"></label><label class="f">Encounter<select id="nenc"><option value="-1">Automatic (first legal)</option></select></label><label class="f chk"><input type="checkbox" id="nsh">Shiny</label>${E.AlphaSupported() ? '<label class="f chk"><input type="checkbox" id="nal">Alpha</label>' : ''}</div>
<p><button class="btn pri" id="create">Create Pokémon</button></p>
<label class="f">Or import a Pokémon file<input type="file" id="impf"></label>`;
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

// <species number>-<species name>-<ball>-<origin game>
const exportName = () => {
  const id = +(by('Species')?.value ?? 0);
  const bv = by('Ball')?.value, ball = S.opts.Ball?.find((o) => String(o.v) === bv)?.t ?? bv;
  const gv = by('Version')?.value, game = gv ? (GAME[gv] ?? gv) : '';
  const clean = (x) => String(x ?? '').replace(/[\\/:*?"<>|]/g, '').trim().replace(/\s+/g, '_');
  return [String(id).padStart(4, '0'), clean(N.species[id]) || 'Unknown', clean(ball), clean(game)].filter(Boolean).join('-');
};

function view() {
  const P = S.props, m = by('Species'), lvl = by('CurrentLevel')?.value ?? '?';
  const id = +(m?.value ?? 0), name = N.species[id] ?? '';
  const nick = by('Nickname')?.value || name, shiny = S.slots[S.slot]?.shiny;
  const list = (re) => P.filter((p) => re.test(p.name));
  let h = '';
  if (S.tab === 'Main') {
    const ORDER = ['Species', 'Form', 'Nickname', 'IsNicknamed', 'Gender', 'IsShiny', 'IsAlpha', 'AlphaMove', 'IsEgg', 'CurrentLevel', 'EXP', 'Nature', 'StatNature', 'Ability', 'AbilityNumber', 'HeldItem', 'CurrentFriendship', 'HeightScalar', 'WeightScalar', 'Scale', 'PID', 'EncryptionConstant'];
    const rank = (n) => { const i = ORDER.indexOf(n); return i < 0 ? ORDER.length : i; };
    h = `<div class="fg">${list(TAB.Main).sort((a, b) => rank(a.name) - rank(b.name)).map(ctl).join('')}</div>`;
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
  if (S.tab === 'Legality') { const r = call(() => J(E.Legality(S.box, S.slot))); h = r.ok ? `<p class="${r.valid ? 'ok' : 'bad'}">${r.valid ? 'Legal' : 'Not legal'}</p><pre>${esc(r.report)}</pre>` : `<p class="bad">${esc(r.error)}</p>`; }
  const tabs = ['Main', 'Stats', 'Moves', 'Cosmetic', 'Met', 'OT / Misc', 'All fields', 'Legality'];
  $('ed').innerHTML = `<div class="top"><div class="av">${img(id, shiny)}</div><div><h2>${esc(nick)}${shiny ? ' ✦' : ''}</h2><small>${esc(name)} · Lv ${esc(lvl)}</small></div><button class="btn" id="exp">Export</button><button class="btn cl" id="cl">Close</button></div>
<div class="tabs" role="tablist">${tabs.map((t) => `<button role="tab" class="${t === S.tab ? 'on' : ''}" data-t="${t}">${t}</button>`).join('')}</div>${h}`;
  $('ed').querySelectorAll('select[data-v]').forEach((s) => { s.value = s.dataset.v; });
  enhance($('ed'));
  $('ed').classList.add('open');
}

function refresh() { S.props = call(() => J(E.GetProps(S.box, S.slot))).props ?? S.props; loadOpts(); loadGrid(); view(); }
function edit(n, v) {
  if (LEGAL_DEP.test(n)) legalDirty = true;
  const r = call(() => J(E.SetProp(S.box, S.slot, n, String(v))));
  if (!r.ok) toast(r.error);
  if (r.ok && /_PPUps$/.test(n)) E.HealPP(S.box, S.slot);
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
  if (t.id === 'nsp') return loadEncounters();
  if (t.dataset.p) return edit(t.dataset.p, t.type === 'checkbox' ? t.checked : t.value);
  if (t.id === 'impf' && t.files[0]) {
    const b = new Uint8Array(await t.files[0].arrayBuffer());
    const r = call(() => J(E.ImportPokemon(b, S.box, S.slot)));
    r.ok ? pick(S.slot) : toast(r.error);
  }
});
$('ed').addEventListener('click', (e) => {
  const t = e.target.closest('button'); if (!t) return;
  if (t.id === 'cl') $('ed').classList.remove('open');
  else if (t.id === 'create') { const r = call(() => J(E.CreatePokemon(S.box, S.slot, +$('nsp').value, +$('nlv').value, +$('nenc').value, $('nsh').checked, !!$('nal')?.checked))); r.ok ? pick(S.slot) : toast(r.error); }
  else if (t.id === 'exp') { const b = E.ExportPokemon(S.box, S.slot); b.length ? download(b, exportName() + '.' + E.PokemonExtension(S.box, S.slot)) : toast('Export failed.'); }
  else if (t.dataset.t) { S.tab = t.dataset.t; view(); }
  else if (t.dataset.plus) { const r = call(() => J(E.ApplyPlus(S.box, S.slot, t.dataset.plus === '1'))); r.ok ? (legalDirty = true, refresh()) : toast(r.error); }
  else if (t.dataset.up) edit(t.dataset.up, t.dataset.x);
  else if (t.dataset.max) { const v = maxFor(t.dataset.max); if (v != null) edit(t.dataset.max, v); }
  else if (t.id === 'rbopen') openRibbons();
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
empty();
