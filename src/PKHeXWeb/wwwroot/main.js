import { dotnet } from './_framework/dotnet.js';
const out = document.getElementById('out');
const log = (o) => (out.textContent = typeof o === 'string' ? o : JSON.stringify(o, null, 2));
addEventListener('error', (e) => log('Error: ' + e.message));
addEventListener('unhandledrejection', (e) => log('Error: ' + (e.reason?.message ?? e.reason)));

const { getAssemblyExports, getConfig } = await dotnet.create();
const E = (await getAssemblyExports(getConfig().mainAssemblyName)).PkhexWeb.Engine;
await dotnet.run();
document.getElementById('status').textContent = 'Engine loaded. Open a save file.';

const step = (name, fn) => {
  try { return fn(); } catch (e) { return { ok: false, error: name + ': ' + (e?.message ?? e) }; }
};

document.getElementById('file').onchange = async (e) => {
  const f = e.target.files[0]; if (!f) return;
  log('Reading ' + f.name + ' (' + f.size + ' bytes)…');
  try {
    const bytes = new Uint8Array(await f.arrayBuffer());
    const info = step('LoadSave', () => JSON.parse(E.LoadSave(bytes, f.name)));
    if (!info.ok) return log(info);
    log({ info, note: 'Reading boxes…' });
    const box = step('GetBox', () => JSON.parse(E.GetBox(0)));
    if (!box.ok) return log({ info, box });
    const first = box.slots.find((s) => !s.empty);
    const props = first ? step('GetProps', () => JSON.parse(E.GetProps(0, first.slot))) : null;
    const legal = first ? step('Legality', () => JSON.parse(E.Legality(0, first.slot))) : null;
    log({ info, box0: box.slots.filter((s) => !s.empty), firstProps: props?.props?.slice(0, 15) ?? props, legal });
    window.pkhex = E;
  } catch (err) {
    log('Error: ' + (err?.message ?? err));
  }
};
