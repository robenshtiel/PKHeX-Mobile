using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PKHeX.Core;

namespace PkhexWeb;

// Every PKHeX.Core call lives in this file. If an upstream update renames something,
// this is the only file that needs fixing.
// Slot addressing: box >= 0 is a box, box = -1 is the party.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    static SaveFile? Sav;
    static string J(object o) => JsonSerializer.Serialize(o);
    static string Err(string m) => J(new { ok = false, error = m });
    static string NoSave() => Err("No save loaded.");

    static PKM? Slot(int box, int slot)
    {
        if (Sav is null) return null;
        if (box < 0) return slot >= 0 && slot < Sav.PartyCount ? Sav.GetPartySlotAtIndex(slot) : null;
        return Sav.GetBoxSlotAtIndex(box, slot);
    }

    static void Store(PKM pk, int box, int slot)
    {
        if (box < 0) Sav!.SetPartySlotAtIndex(pk, slot);
        else Sav!.SetBoxSlotAtIndex(pk, box, slot);
    }

    static string Info() => J(new
    {
        ok = true, game = Sav!.Version.ToString(), generation = Sav.Generation, ot = Sav.OT,
        boxes = Sav.BoxCount, slots = Sav.BoxSlotCount, party = Sav.PartyCount,
    });

    static object Summary(PKM pk, int slot)
    {
        if (pk.Species == 0) return new { slot, empty = true };
        var name = GameInfo.Strings.Species.ElementAtOrDefault(pk.Species) ?? ("#" + pk.Species);
        return new { slot, empty = false, id = (int)pk.Species, species = name, nick = pk.Nickname, level = pk.CurrentLevel, shiny = pk.IsShiny };
    }

    static bool Simple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string);
    static IEnumerable<PropertyInfo> Editable(PKM pk) =>
        pk.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && Simple(p.PropertyType))
          .OrderBy(p => p.Name);

    // Property names change between PKHeX versions, so these helpers try several names.
    static bool TrySet(object target, string[] names, object value)
    {
        foreach (var n in names)
        {
            var p = target.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || !p.CanWrite) continue;
            try
            {
                var v = p.PropertyType.IsEnum ? Enum.ToObject(p.PropertyType, value)
                      : Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture);
                p.SetValue(target, v);
                return true;
            }
            catch { }
        }
        return false;
    }

    static object? TryGet(object source, string[] names)
    {
        foreach (var n in names)
        {
            var p = source.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p is not null && p.CanRead) return p.GetValue(source);
        }
        return null;
    }

    // PKHeX moved/renamed these APIs between versions, so look them up at runtime.
    static SaveFile MakeBlank(GameVersion v, string name)
    {
        foreach (var typeName in new[] { "PKHeX.Core.BlankSaveFile", "PKHeX.Core.SaveUtil" })
        {
            var t = typeof(SaveFile).Assembly.GetType(typeName);
            if (t is null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name is not ("Get" or "GetBlankSAV")) continue;
                var ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(GameVersion) || ps[1].ParameterType != typeof(string)) continue;
                if (!typeof(SaveFile).IsAssignableFrom(m.ReturnType)) continue;
                var args = new object?[ps.Length];
                args[0] = v; args[1] = name;
                for (int i = 2; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                            : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                if (m.Invoke(null, args) is SaveFile sf) return sf;
            }
        }
        throw new InvalidOperationException("Could not find PKHeX's blank-save method.");
    }

    static byte[] PkmBytes(PKM pk)
    {
        foreach (var n in new[] { "DecryptedPartyData", "DecryptedBoxData", "Data" })
        {
            var ty = pk.GetType();
            var v = ty.GetProperty(n)?.GetValue(pk) ?? ty.GetField(n)?.GetValue(pk);
            if (v is byte[] b) return (byte[])b.Clone();
            if (v is Memory<byte> m) return m.ToArray();
            if (v is ReadOnlyMemory<byte> rm) return rm.ToArray();
        }
        throw new InvalidOperationException("Could not read this Pokémon's raw data.");
    }

    // ---------- saves ----------

    [JSExport]
    public static string LoadSave(byte[] data, string fileName)
    {
        try
        {
            if (!SaveUtil.TryGetSaveFile(data, out var sav, fileName) || sav is null)
                return Err("Not a recognized save file.");
            Sav = sav;
            return Info();
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static readonly string[] Games =
    [
        "RD", "GN", "BU", "YW", "GD", "SI", "C", "R", "S", "E", "FR", "LG", "D", "P", "Pt", "HG", "SS",
        "B", "W", "B2", "W2", "X", "Y", "OR", "AS", "SN", "MN", "US", "UM", "GP", "GE", "SW", "SH",
        "BD", "SP", "PLA", "SL", "VL",
    ];

    [JSExport]
    public static string ListGames() => J(Games.Where(g => Enum.TryParse<GameVersion>(g, out _)));

    [JSExport]
    public static string NewSave(string game, string trainer)
    {
        try
        {
            if (!Enum.TryParse<GameVersion>(game, out var v)) return Err("Unknown game: " + game);
            Sav = MakeBlank(v, string.IsNullOrWhiteSpace(trainer) ? "PKHeX" : trainer);
            return Info();
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static byte[] ExportSave() => Sav is null ? [] : Sav.Write().ToArray();

    // ---------- browsing ----------

    [JSExport]
    public static string GetBox(int box)
    {
        if (Sav is null) return NoSave();
        var list = new List<object>();
        for (int i = 0; i < Sav.BoxSlotCount; i++)
            list.Add(Summary(Sav.GetBoxSlotAtIndex(box, i), i));
        return J(new { ok = true, box, slots = list });
    }

    [JSExport]
    public static string GetParty()
    {
        if (Sav is null) return NoSave();
        var list = new List<object>();
        for (int i = 0; i < Sav.PartyCount; i++)
            list.Add(Summary(Sav.GetPartySlotAtIndex(i), i));
        return J(new { ok = true, slots = list });
    }

    // ---------- editing ----------

    [JSExport]
    public static string GetProps(int box, int slot)
    {
        var pk = Slot(box, slot);
        if (pk is null) return Err("No Pokémon in that slot.");
        return J(new { ok = true, props = Editable(pk).Select(p =>
            new { name = p.Name, type = p.PropertyType.Name, value = p.GetValue(pk)?.ToString(), options = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null }) });
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
            Store(pk, box, slot);
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

    // ---------- names and helpers for the UI ----------

    static string[] NameList(params string[] candidates)
    {
        var s = GameInfo.Strings; var ty = s.GetType();
        foreach (var c in candidates)
        {
            var v = ty.GetProperty(c)?.GetValue(s) ?? ty.GetField(c)?.GetValue(s);
            if (v is System.Collections.IEnumerable e && v is not string)
                return e.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
        }
        return [];
    }

    [JSExport]
    public static string GetNames() => J(new
    {
        species = NameList("Species", "specieslist"), moves = NameList("Move", "movelist"),
        items = NameList("Item", "itemlist"), abilities = NameList("Ability", "abilitylist"),
        natures = NameList("Natures", "natures"),
    });

    [JSExport]
    public static string HealPP(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null || Sav is null) return Err("No Pokémon in that slot.");
            pk.GetType().GetMethod("HealPP", Type.EmptyTypes)?.Invoke(pk, null);
            pk.RefreshChecksum();
            Store(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- creating and moving Pokémon (boxes only for now) ----------

    [JSExport]
    public static string CreatePokemon(int box, int slot, int species, int level)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0 || box >= Sav.BoxCount || slot < 0 || slot >= Sav.BoxSlotCount) return Err("Invalid box slot.");
            var names = GameInfo.Strings.Species;
            if (species < 1 || species >= names.Count()) return Err("Invalid species number.");
            if (level < 1 || level > 100) return Err("Level must be 1-100.");

            var pk = Sav.BlankPKM.Clone();
            pk.Species = (ushort)species;
            pk.CurrentLevel = (byte)level;
            pk.Nickname = names.ElementAt(species);
            pk.PID = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
            TrySet(pk, ["OriginalTrainerName", "OT_Name"], Sav.OT);
            var tid = TryGet(Sav, ["TID16", "TID"]); if (tid is not null) TrySet(pk, ["TID16", "TID"], tid);
            var sid = TryGet(Sav, ["SID16", "SID"]); if (sid is not null) TrySet(pk, ["SID16", "SID"], sid);
            TrySet(pk, ["Language"], 2);
            foreach (var iv in new[] { "IV_HP", "IV_ATK", "IV_DEF", "IV_SPA", "IV_SPD", "IV_SPE" })
                TrySet(pk, [iv], 31);
            TrySet(pk, ["Move1"], 1);
            pk.RefreshChecksum();
            Store(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static byte[] ExportPokemon(int box, int slot)
    {
        var pk = Slot(box, slot);
        return pk is null ? [] : PkmBytes(pk);
    }

    [JSExport]
    public static string PokemonExtension(int box, int slot) => Slot(box, slot)?.Extension ?? "pk";

    [JSExport]
    public static string ImportPokemon(byte[] data, int box, int slot)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0 || box >= Sav.BoxCount || slot < 0 || slot >= Sav.BoxSlotCount) return Err("Invalid box slot.");
            var pk = EntityFormat.GetFromBytes(data);
            if (pk is null) return Err("Not a recognized Pokémon file.");
            var need = Sav.BlankPKM.GetType();
            if (pk.GetType() != need)
                return Err($"That file is a {pk.GetType().Name}, but this save needs a {need.Name}. Format conversion is not supported yet.");
            pk.RefreshChecksum();
            Store(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }
}
