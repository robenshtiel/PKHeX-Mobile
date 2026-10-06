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
const LISTS = [[/^Species$/, 'species'], [/^(Move[1-4]|RelearnMove[1-4])$/, 'moves'], [/^(HeldItem|Item)$/, 'items'], [/^Ability$/, 'abilities'], [/^(Nature|StatNature)$/, 'natures']];
const NUM = /^(Byte|SByte|U?Int(16|32|64))$/;
const TAB = {
  Main: /^(Species|Nickname|IsNicknamed|IsShiny|CurrentLevel|EXP|Nature|StatNature|Ability|AbilityNumber|HeldItem|Gender|Form|IsEgg|CurrentFriendship|HeightScalar|WeightScalar|Scale|Tera\w*|PID|EncryptionConstant)$/,
  Moves: /^(Move[1-4]|RelearnMove[1-4])(_PP|_PPUps)?$/,
  Cosmetic: /^(Contest|Marking|Ribbon|AffixedRibbon|HasBattle|HasContest)/,
  'OT / Misc': /^(OriginalTrainer|OT_|HandlingTrainer|HT_|TID|SID|Ball|Met|Egg|Version|Language|Geo)/,
};
const S = { info: null, box: 0, slot: -1, slots: [], props: [], opts: {}, legal: {}, tab: 'Main' };
let legalDirty = true;
const LEGAL_DEP = /^(Species|Form|CurrentLevel|EXP|Version|Met|Egg|IsEgg|Ability|Move[1-4]$)/;
const ONLY_LEGAL = /^(Move[1-4]|Ability)$/;
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
  return `<label class="f">${lbl}<input data-p="${p.name}" type="${NUM.test(p.type) ? 'number' : 'text'}" value="${esc(v)}"></label>`;
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
<div class="fg"><label class="f">Species<select id="nsp">${listOpts('species')}</select></label><label class="f">Level<input id="nlv" type="number" min="1" max="100" value="50"></label><label class="f">Encounter<select id="nenc"><option value="-1">Automatic (first legal)</option></select></label><label class="f chk"><input type="checkbox" id="nsh">Shiny</label></div>
<p><button class="btn pri" id="create">Create Pokémon</button></p>
<label class="f">Or import a Pokémon file<input type="file" id="impf"></label>`;
  if ($('nsp')) { $('nsp').value = 25; loadEncounters(); }
  ed.classList.add('open');
}

function loadEncounters() {
  const sel = $('nenc'); if (!sel) return;
  const r = call(() => J(E.ListEncounters(+$('nsp').value)));
  sel.innerHTML = '<option value="-1">Automatic (first legal)</option>' + (r.encounters ?? []).map((e) => {
    const lv = e.min ? (e.min === e.max ? ` Lv ${e.min}` : ` Lv ${e.min}-${e.max}`) : '';
    return `<option value="${e.i}">${esc(nice(e.kind))}: ${esc(e.name)}${lv}</option>`;
  }).join('');
  if (!r.ok) toast(r.error);
}

function view() {
  const P = S.props, m = by('Species'), lvl = by('CurrentLevel')?.value ?? '?';
  const id = +(m?.value ?? 0), name = N.species[id] ?? '';
  const nick = by('Nickname')?.value || name, shiny = S.slots[S.slot]?.shiny;
  const list = (re) => P.filter((p) => re.test(p.name));
  let h = '';
  if (S.tab === 'Main') h = `<div class="fg">${list(TAB.Main).map(ctl).join('')}</div>`;
  if (S.tab === 'Stats') {
    const st = ['HP', 'ATK', 'DEF', 'SPA', 'SPD', 'SPE'], c = (n) => by(n) ? `<td>${ctl(by(n)).replace(/<label[^>]*>[^<]*(?=<input)/, '<label>')}</td>` : '<td></td>';
    h = `<table><tr><th></th><th>IV</th><th>EV</th></tr>${st.map((x) => `<tr><td>${x}</td>${c('IV_' + x)}${c('EV_' + x)}</tr>`).join('')}</table>`;
  }
  if (S.tab === 'Moves') {
    h = [1, 2, 3, 4].map((i) => { const mv = by('Move' + i); if (!mv) return ''; const u = +(by(`Move${i}_PPUps`)?.value ?? 0);
      return `<div class="mv">${ctl(mv)}<div class="r">${by(`Move${i}_PP`) ? ctl(by(`Move${i}_PP`)) : ''}<div class="seg" role="group" aria-label="PP Ups">${[0, 1, 2, 3].map((x) => `<button data-up="Move${i}_PPUps" data-x="${x}" class="${u === x ? 'on' : ''}">${x}</button>`).join('')}</div></div></div>`; }).join('')
      + `<h3>Relearn moves</h3><div class="fg">${list(/^RelearnMove/).map(ctl).join('')}</div>`;
  }
  if (S.tab === 'Cosmetic') {
    const rb = P.filter((p) => p.type === 'Boolean' && /^Ribbon/.test(p.name)), on = rb.filter((p) => p.value === 'True').length;
    const rest = P.filter((p) => TAB.Cosmetic.test(p.name) && !rb.includes(p));
    h = `<div class="fg">${rest.map(ctl).join('')}</div><h3>Ribbons and marks (${on}/${rb.length})</h3><div class="chips">${rb.map((p) => `<button class="chip${p.value === 'True' ? ' on' : ''}" data-chip="${p.name}">${nice(p.name.replace(/^Ribbon/, ''))}</button>`).join('')}</div>
<p><button class="btn" data-all="1">Set all</button> <button class="btn" data-all="0">Clear all</button></p>`;
  }
  if (S.tab === 'OT / Misc') h = `<div class="fg">${list(TAB['OT / Misc']).map(ctl).join('')}</div>`;
  if (S.tab === 'All fields') h = `<label class="f">Search<input id="q" type="search" placeholder="Filter fields"></label><div class="fg all" style="margin-top:10px">${P.map(ctl).join('')}</div>`;
  if (S.tab === 'Legality') { const r = call(() => J(E.Legality(S.box, S.slot))); h = r.ok ? `<p class="${r.valid ? 'ok' : 'bad'}">${r.valid ? 'Legal' : 'Not legal'}</p><pre>${esc(r.report)}</pre>` : `<p class="bad">${esc(r.error)}</p>`; }
  const tabs = ['Main', 'Stats', 'Moves', 'Cosmetic', 'OT / Misc', 'All fields', 'Legality'];
  $('ed').innerHTML = `<div class="top"><div class="av">${img(id, shiny)}</div><div><h2>${esc(nick)}${shiny ? ' ✦' : ''}</h2><small>${esc(name)} · Lv ${esc(lvl)}</small></div><button class="btn" id="exp">Export</button><button class="btn cl" id="cl">Close</button></div>
<div class="tabs" role="tablist">${tabs.map((t) => `<button role="tab" class="${t === S.tab ? 'on' : ''}" data-t="${t}">${t}</button>`).join('')}</div>${h}`;
  $('ed').querySelectorAll('select[data-v]').forEach((s) => { s.value = s.dataset.v; });
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
  else if (t.id === 'create') { const r = call(() => J(E.CreatePokemon(S.box, S.slot, +$('nsp').value, +$('nlv').value, +$('nenc').value, $('nsh').checked))); r.ok ? pick(S.slot) : toast(r.error); }
  else if (t.id === 'exp') { const b = E.ExportPokemon(S.box, S.slot); b.length ? download(b, 'pokemon.' + E.PokemonExtension(S.box, S.slot)) : toast('Export failed.'); }
  else if (t.dataset.t) { S.tab = t.dataset.t; view(); }
  else if (t.dataset.up) edit(t.dataset.up, t.dataset.x);
  else if (t.dataset.chip) edit(t.dataset.chip, by(t.dataset.chip).value !== 'True');
  else if (t.dataset.all) {
    const on = t.dataset.all === '1';
    S.props.filter((p) => p.type === 'Boolean' && /^Ribbon/.test(p.name)).forEach((p) => E.SetProp(S.box, S.slot, p.name, String(on)));
    refresh();
  }
});
$('load').onclick = () => $('file').click();
$('file').onchange = async (e) => { const f = e.target.files[0]; if (!f) return; const b = new Uint8Array(await f.arrayBuffer()); setup(call(() => J(E.LoadSave(b, f.name)))); e.target.value = ''; };
$('newbtn').onclick = () => { $('newp').hidden = !$('newp').hidden; };
for (const g of J(E.ListGames())) $('game').add(new Option(g, g));
if ([...$('game').options].some((o) => o.value === 'E')) $('game').value = 'E';
$('mk').onclick = () => setup(call(() => J(E.NewSave($('game').value, $('trainer').value))));
$('dlsave').onclick = () => S.info ? download(E.ExportSave(), 'edited.sav') : toast('Open or create a save first.');
$('theme').onclick = () => { const r = document.documentElement, d = (r.dataset.theme || (matchMedia('(prefers-color-scheme:dark)').matches ? 'dark' : 'light')) === 'dark'; r.dataset.theme = d ? 'light' : 'dark'; };
empty();
