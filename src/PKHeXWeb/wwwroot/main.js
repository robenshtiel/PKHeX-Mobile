import { dotnet } from './_framework/dotnet.js';
const $ = (id) => document.getElementById(id);
const out = $('out');
const log = (o) => (out.textContent = typeof o === 'string' ? o : JSON.stringify(o, null, 2));
addEventListener('error', (e) => log('Error: ' + e.message));
addEventListener('unhandledrejection', (e) => log('Error: ' + (e.reason?.message ?? e.reason)));

const { getAssemblyExports, getConfig, runMain } = await dotnet.create();
const E = (await getAssemblyExports(getConfig().mainAssemblyName)).PkhexWeb.Engine;
await runMain();
$('status').textContent = 'Engine loaded. Open a save or create a new one.';

const J = (s) => JSON.parse(s);
const guard = (name, fn) => {
  try { return fn(); } catch (e) { return { ok: false, error: name + ': ' + (e?.message ?? e) }; }
};
for (const g of J(E.ListGames())) $('game').add(new Option(g, g));
if ([...$('game').options].some((o) => o.value === 'E')) $('game').value = 'E';

let info = null, first = null, empty = null;

function download(bytes, name) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([bytes]));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}

function scan() {
  const party = guard('GetParty', () => J(E.GetParty()));
  const partyMons = party.ok ? party.slots.filter((s) => !s.empty) : [];
  first = partyMons.length ? { box: -1, slot: partyMons[0].slot } : null;
  empty = null;
  const counts = [];
  for (let b = 0; b < info.boxes; b++) {
    const box = guard('GetBox ' + b, () => J(E.GetBox(b)));
    if (!box.ok) return { error: box };
    const mons = box.slots.filter((s) => !s.empty);
    counts.push(mons.length);
    if (!first && mons.length) first = { box: b, slot: mons[0].slot };
    if (!empty) { const e = box.slots.find((s) => s.empty); if (e) empty = { box: b, slot: e.slot }; }
  }
  return { party: partyMons, pokemonPerBox: counts };
}

function report(note) {
  if (!info?.ok) return log(info);
  const res = { note, info, ...scan() };
  if (first) {
    const props = guard('GetProps', () => J(E.GetProps(first.box, first.slot)));
    const want = /Tera|Height|Weight|Scale|PP|Ribbon|Contest|Cool|Beauty|Cute|Smart|Tough|Sheen|Mark/i;
    res.firstPokemonAt = first.box < 0 ? 'party slot ' + first.slot : 'box ' + first.box + ' slot ' + first.slot;
    res.editablePropertyCount = props.props?.length;
    res.cosmeticAndMoveProps = props.props?.filter((p) => want.test(p.name)).map((p) => p.name + ' = ' + p.value);
    res.legal = guard('Legality', () => J(E.Legality(first.box, first.slot)));
  }
  log(res);
}

$('file').onchange = async (e) => {
  const f = e.target.files[0]; if (!f) return;
  log('Reading ' + f.name + ' (' + f.size + ' bytes)…');
  const bytes = new Uint8Array(await f.arrayBuffer());
  info = guard('LoadSave', () => J(E.LoadSave(bytes, f.name)));
  report('Loaded ' + f.name);
};

$('new').onclick = () => {
  info = guard('NewSave', () => J(E.NewSave($('game').value, $('trainer').value)));
  report('Created a new ' + $('game').value + ' save');
};

$('add').onclick = () => {
  if (!info?.ok) return log('Open or create a save first.');
  if (!empty) return log('No empty box slot found.');
  const r = guard('CreatePokemon', () => J(E.CreatePokemon(empty.box, empty.slot, +$('species').value, +$('level').value)));
  if (!r.ok) return log(r);
  report('Added species #' + $('species').value);
};

$('monfile').onchange = async (e) => {
  const f = e.target.files[0]; if (!f) return;
  if (!info?.ok) return log('Open or create a save first.');
  if (!empty) return log('No empty box slot found.');
  const bytes = new Uint8Array(await f.arrayBuffer());
  const r = guard('ImportPokemon', () => J(E.ImportPokemon(bytes, empty.box, empty.slot)));
  if (!r.ok) return log(r);
  report('Imported ' + f.name);
};

$('dlmon').onclick = () => {
  if (!first) return log('No Pokémon to export yet.');
  const bytes = E.ExportPokemon(first.box, first.slot);
  if (!bytes.length) return log('Export failed.');
  download(bytes, 'pokemon.' + E.PokemonExtension(first.box, first.slot));
};

$('dlsave').onclick = () => {
  if (!info?.ok) return log('Open or create a save first.');
  download(E.ExportSave(), 'edited.sav');
};
