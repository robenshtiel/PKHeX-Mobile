using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PKHeX.Core;

namespace PkhexWeb;

// Every PKHeX.Core call lives in this file. If an upstream update renames something,
// this is the only file that needs fixing.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    static SaveFile? Sav;
    static string J(object o) => JsonSerializer.Serialize(o);
    static string Err(string m) => J(new { ok = false, error = m });

    static PKM? Slot(int box, int slot) => Sav?.GetBoxSlotAtIndex(box, slot);

    static bool Simple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string);
    static IEnumerable<PropertyInfo> Editable(PKM pk) =>
        pk.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && Simple(p.PropertyType))
          .OrderBy(p => p.Name);

    [JSExport]
    public static string LoadSave(byte[] data, string fileName)
    {
        try
        {
            if (!SaveUtil.TryGetSaveFile(data, out var sav, fileName) || sav is null)
                return Err("Not a recognized save file.");
            Sav = sav;
            return J(new { ok = true, game = sav.Version.ToString(), generation = sav.Generation,
                           ot = sav.OT, boxes = sav.BoxCount, slots = sav.BoxSlotCount });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string GetBox(int box)
    {
        if (Sav is null) return Err("No save loaded.");
        var names = GameInfo.Strings.Species;
        var list = new List<object>();
        for (int i = 0; i < Sav.BoxSlotCount; i++)
        {
            var pk = Slot(box, i);
            if (pk is null || pk.Species == 0) { list.Add(new { slot = i, empty = true }); continue; }
            list.Add(new { slot = i, empty = false, species = names[pk.Species], nick = pk.Nickname,
                           level = pk.CurrentLevel, shiny = pk.IsShiny });
        }
        return J(new { ok = true, box, slots = list });
    }

    [JSExport]
    public static string GetProps(int box, int slot)
    {
        var pk = Slot(box, slot);
        if (pk is null) return Err("No Pokémon in that slot.");
        return J(new { ok = true, props = Editable(pk).Select(p =>
            new { name = p.Name, type = p.PropertyType.Name, value = p.GetValue(pk)?.ToString() }) });
    }

    [JSExport]
    public static string SetProp(int box, int slot, string name, string value)
    {
        try
        {
            var pk = Slot(box, slot);
            var p = pk is null ? null : Editable(pk).FirstOrDefault(x => x.Name == name);
            if (pk is null || p is null || Sav is null) return Err("Unknown property.");
            var t = p.PropertyType;
            object v = t.IsEnum ? Enum.Parse(t, value)
                     : t == typeof(bool) ? bool.Parse(value)
                     : Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            p.SetValue(pk, v);
            pk.RefreshChecksum();
            Sav.SetBoxSlotAtIndex(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string Legality(int box, int slot)
    {
        var pk = Slot(box, slot);
        if (pk is null) return Err("No Pokémon in that slot.");
        var la = new LegalityAnalysis(pk);
        return J(new { ok = true, valid = la.Valid, report = la.Report() });
    }

    [JSExport]
    public static byte[] ExportSave() => Sav is null ? [] : Sav.Write().ToArray();
}
