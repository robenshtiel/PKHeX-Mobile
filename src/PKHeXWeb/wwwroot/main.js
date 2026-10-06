import { dotnet } from './_framework/dotnet.js';
const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const E = exports.PkhexWeb.Engine;
await dotnet.run();

const out = document.getElementById('out');
const log = (o) => (out.textContent = typeof o === 'string' ? o : JSON.stringify(o, null, 2));
const call = (r) => JSON.parse(r);
document.getElementById('status').textContent = 'Engine loaded. Open a save file.';

document.getElementById('file').onchange = async (e) => {
  const f = e.target.files[0]; if (!f) return;
  const info = call(E.LoadSave(new Uint8Array(await f.arrayBuffer()), f.name));
  if (!info.ok) return log(info);
  const box = call(E.GetBox(0));
  const first = box.slots.find((s) => !s.empty);
  const props = first ? call(E.GetProps(0, first.slot)) : null;
  const legal = first ? call(E.Legality(0, first.slot)) : null;
  log({ info, box0: box.slots.filter((s) => !s.empty), firstProps: props?.props?.slice(0, 15), legal });
  window.pkhex = E; // try E.SetProp(0, slot, 'CurrentLevel', '100') in the console
};
