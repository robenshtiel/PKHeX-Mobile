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

    // Data is a Span/array depending on PKHeX version; ToArray() compiles for all of them.
    static byte[] PkmBytes(PKM pk) => pk.Data.ToArray();

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
        var list = Editable(pk).Select(p =>
            new { name = p.Name, type = p.PropertyType.Name, value = p.GetValue(pk)?.ToString(), options = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null }).ToList();
        // IsShiny has no setter in PKHeX (it's derived from PID/TID/SID), so it is exposed as a synthetic field.
        list.Add(new { name = "IsShiny", type = "Boolean", value = (string?)pk.IsShiny.ToString(), options = (string[]?)null });
        return J(new { ok = true, props = list });
    }

    [JSExport]
    public static string SetProp(int box, int slot, string name, string value)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is not null && Sav is not null && name == "IsShiny")
            {
                if (!SetShiny(pk, bool.Parse(value))) return Err("Couldn't change shiny status (PKHeX API changed?).");
                pk.RefreshChecksum();
                Store(pk, box, slot);
                return J(new { ok = true });
            }
            var p = pk is null ? null : Editable(pk).FirstOrDefault(x => x.Name == name);
            if (pk is null || p is null || Sav is null) return Err("Unknown property.");
            var t = p.PropertyType;
            object v = t.IsEnum ? Enum.Parse(t, value)
                     : t == typeof(bool) ? bool.Parse(value)
                     : Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            p.SetValue(pk, v);
            if (name == "AbilityNumber") Call(pk, "RefreshAbility", (int)Math.Log2(Math.Max(1, Convert.ToInt32(v))));
            if (name == "Ability")
            {
                var id = Convert.ToInt32(v); var sl = AbilitySlots(pk);
                var curNum = Convert.ToInt32(Prop(pk, "AbilityNumber") ?? 0);
                int curIdx = curNum > 0 ? (int)Math.Log2(curNum) : -1;
                var idx = curIdx >= 0 && curIdx < sl.Count && sl[curIdx] == id ? curIdx : sl.FindIndex(a => a == id);
                if (idx >= 0) Call(pk, "RefreshAbility", idx);
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(name, "^Move[1-4]$")) Call(pk, "HealPP");
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

    // ---------- shiny and readable option lists ----------

    static bool SetShiny(PKM pk, bool on)
    {
        var t = typeof(SaveFile).Assembly.GetType("PKHeX.Core.CommonEdits");
        if (t is null) return false;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length < 2 || !ps[0].ParameterType.IsAssignableFrom(pk.GetType())) continue;
            try
            {
                if (m.Name == "SetIsShiny" && ps.Length == 2 && ps[1].ParameterType == typeof(bool)) { m.Invoke(null, [pk, on]); return true; }
                if (m.Name == "SetShiny" && ps[1].ParameterType.IsEnum)
                {
                    var args = new object?[ps.Length];
                    args[0] = pk; args[1] = Enum.Parse(ps[1].ParameterType, on ? "Random" : "Never");
                    for (int i = 2; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    m.Invoke(null, args);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    record Opt(int v, string t);

    static string Nice(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace('_', ' '), "([a-z])([A-Z0-9])", "$1 $2");
    static List<Opt> Pairs(params string[] names) => names.Select((n, i) => new Opt(i, n)).ToList();

    static List<Opt>? EnumOpts(string typeName)
    {
        var t = typeof(SaveFile).Assembly.GetType(typeName);
        if (t is null || !t.IsEnum) return null;
        return Enum.GetValues(t).Cast<object>().GroupBy(x => Convert.ToInt32(x)).Select(g => new Opt(g.Key, Nice(g.First().ToString()!))).OrderBy(o => o.v).ToList();
    }

    static List<Opt>? Forms(PKM pk)
    {
        var t = typeof(SaveFile).Assembly.GetType("PKHeX.Core.FormConverter");
        var types = NameList("types", "Types"); var forms = NameList("forms", "Forms", "Form");
        if (t is null || types.Length == 0 || forms.Length == 0) return null;
        var lists = new[] { types, forms, ["♂", "♀", "-"] };
        var ctx = Prop(pk, "Context");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetFormList") continue;
            var ps = m.GetParameters(); var args = new object?[ps.Length]; int li = 0; bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt == typeof(ushort) || pt == typeof(int) || pt == typeof(byte)) args[i] = Convert.ChangeType(pk.Species, pt);
                else if (pt.IsEnum) args[i] = ctx is not null && pt.IsInstanceOfType(ctx) ? ctx : Enum.ToObject(pt, 0);
                else if (pt.IsAssignableFrom(typeof(string[])) && li < lists.Length) args[i] = lists[li++];
                else ok = false;
            }
            if (!ok) continue;
            try
            {
                if (m.Invoke(null, args) is IEnumerable<string> r)
                {
                    var l = Pairs(r.ToArray());
                    return l.Count > 1 ? l : null;
                }
            }
            catch { }
        }
        return null;
    }

    static List<Opt>? Locations(PKM pk, bool egg)
    {
        var ver = Prop(pk, "Version") ?? Prop(pk, "Game");
        var ctx = Prop(pk, "Context");
        foreach (var m in typeof(GameInfo).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetLocationList") continue;
            var ps = m.GetParameters(); var args = new object?[ps.Length]; bool ok = true;
            for (int i = 0; i < ps.Length && ok; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt == typeof(bool)) args[i] = egg;
                else if (pt.IsEnum && pt.Name == "GameVersion") args[i] = ver is null ? null : pt.IsInstanceOfType(ver) ? ver : Enum.ToObject(pt, Convert.ToInt32(ver));
                else if (pt.IsEnum) args[i] = ctx is not null && pt.IsInstanceOfType(ctx) ? ctx : Enum.ToObject(pt, 0);
                else ok = false;
            }
            if (!ok) continue;
            try
            {
                if (m.Invoke(null, args) is System.Collections.IEnumerable r)
                {
                    var l = new List<Opt>();
                    foreach (var x in r)
                        if (Prop(x, "Text") is string txt && Prop(x, "Value") is { } val) l.Add(new Opt(Convert.ToInt32(val), txt));
                    if (l.Count > 0) return l;
                }
            }
            catch { }
        }
        return null;
    }

    // Names for fields PKHeX stores as plain numbers. Anything that can't be resolved is left out,
    // and the UI keeps showing a number box for it.
    [JSExport]
    public static string GetOptions(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            var d = new Dictionary<string, List<Opt>>();
            void Add(string[] names, List<Opt>? l)
            {
                if (l is null || l.Count == 0) return;
                foreach (var n in names)
                {
                    var pi = pk.GetType().GetProperty(n);
                    if (pi is null || pi.PropertyType.IsEnum || pi.PropertyType == typeof(bool)) continue;
                    d[n] = l;
                }
            }
            Add(["Gender"], Pairs("Male", "Female", "Genderless"));
            Add(["OriginalTrainerGender", "OT_Gender", "HandlingTrainerGender", "HT_Gender"], Pairs("Male", "Female"));
            Add(["CurrentHandler"], Pairs("Original Trainer", "Handling Trainer"));
            Add(["AbilityNumber"], [new Opt(1, "First ability"), new Opt(2, "Second ability"), new Opt(4, "Hidden ability")]);
            Add(["Language"], [new Opt(1, "Japanese"), new Opt(2, "English"), new Opt(3, "French"), new Opt(4, "Italian"), new Opt(5, "German"),
                               new Opt(7, "Spanish"), new Opt(8, "Korean"), new Opt(9, "Chinese (Simplified)"), new Opt(10, "Chinese (Traditional)")]);
            Add(["Ball"], EnumOpts("PKHeX.Core.Ball"));
            Add(["Form"], Forms(pk));
            Add(["MetLocation", "Met_Location"], Locations(pk, false));
            Add(["EggLocation", "Egg_Location"], Locations(pk, true));
            return J(new { ok = true, opts = d });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- legal moves and abilities ----------

    // Ability ids in each slot of the species/form's personal data (index 0, 1, hidden). 0 = unused slot.
    static List<int> AbilitySlots(PKM pk)
    {
        var res = new List<int>();
        var pi = Prop(pk, "PersonalInfo");
        if (pi is null) return res;
        var ty = pi.GetType();
        var mi = new[] { ty }.Concat(ty.GetInterfaces()).Select(t => t.GetMethod("GetAbilityAtIndex", [typeof(int)])).FirstOrDefault(m => m is not null);
        if (mi is null) return res;
        int count = Prop(pi, "AbilityCount") is { } c ? Convert.ToInt32(c) : 3;
        for (int i = 0; i < count; i++)
        {
            try { res.Add(Convert.ToInt32(mi.Invoke(pi, [i]))); } catch { res.Add(0); }
        }
        return res;
    }

    // Broad candidate list from PKHeX's own helper (every source). Only used to narrow the search;
    // each candidate is then verified against the real legality check below.
    static HashSet<int>? SuggestedMoves(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        foreach (var t in typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic))
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetSuggestedMoves" || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(typeof(LegalityAnalysis))) continue;
                var args = new object?[ps.Length]; args[0] = la;
                for (int i = 1; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(bool)) args[i] = true;
                    else if (pt.IsEnum) args[i] = Enum.ToObject(pt, Enum.GetValues(pt).Cast<object>().Aggregate(0L, (a, x) => a | Convert.ToInt64(x)));
                    else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }
                try
                {
                    var set = new HashSet<int>();
                    var r = m.Invoke(null, args);
                    if (r is ReadOnlyMemory<ushort> mem) foreach (var x in mem.ToArray()) set.Add(x);
                    else if (r is System.Collections.IEnumerable e) foreach (var x in e) set.Add(Convert.ToInt32(x));
                    set.Remove(0);
                    if (set.Count > 0) return set;
                }
                catch { }
            }
        }
        return null;
    }

    // True if this move, placed alone in slot 1 of this exact Pokémon (same encounter data), passes the legality check.
    static bool MoveSlotValid(PKM pk, int move)
    {
        var c = pk.Clone();
        c.Move1 = (ushort)move; c.Move2 = 0; c.Move3 = 0; c.Move4 = 0;
        TrySet(c, ["Move1_PPUps"], 0); TrySet(c, ["Move2_PPUps"], 0); TrySet(c, ["Move3_PPUps"], 0); TrySet(c, ["Move4_PPUps"], 0);
        Call(c, "HealPP");
        c.RefreshChecksum();
        var la = new LegalityAnalysis(c);
        if (Prop(la, "Info") is { } info && Prop(info, "Moves") is System.Collections.IEnumerable mv)
        {
            foreach (var m in mv) if (Prop(m, "Valid") is bool ok) return ok;
        }
        return !la.Report().Split('\n').Any(l => l.StartsWith("Invalid") && (l.Contains("Move 1") || l.Contains("Move1")));
    }

    static readonly Dictionary<string, HashSet<int>> MoveCache = new();

    static string MoveKey(PKM pk) => string.Join("|", pk.Species, Prop(pk, "Form"), pk.CurrentLevel,
        Prop(pk, "Version") ?? Prop(pk, "Game"), Prop(pk, "MetLocation") ?? Prop(pk, "Met_Location"),
        Prop(pk, "EggLocation") ?? Prop(pk, "Egg_Location"), Prop(pk, "MetLevel") ?? Prop(pk, "Met_Level"), pk.IsEgg);

    // Moves that are legal for THIS Pokémon: its species/form, level, and the encounter it matches.
    // Cached per (species, form, level, version, met data) because checking is not free in the browser.
    static HashSet<int>? LegalMoves(PKM pk)
    {
        var key = MoveKey(pk);
        if (MoveCache.TryGetValue(key, out var hit)) return hit;

        var pool = SuggestedMoves(pk);
        IEnumerable<int> cand;
        if (pool is not null)
        {
            foreach (var n in new[] { "Move1", "Move2", "Move3", "Move4" }) pool.Add(Convert.ToInt32(Prop(pk, n) ?? 0));
            pool.Remove(0); cand = pool;
        }
        else
        {
            int max = Prop(Sav!, "MaxMoveID") is { } mm ? Convert.ToInt32(mm) : NameList("Move", "movelist").Length - 1;
            cand = Enumerable.Range(1, Math.Max(1, max));
        }

        var set = new HashSet<int>();
        foreach (var m in cand) { try { if (MoveSlotValid(pk, m)) set.Add(m); } catch { } }
        if (set.Count == 0) return null;
        if (MoveCache.Count > 64) MoveCache.Clear();
        MoveCache[key] = set;
        return set;
    }

    static bool AbilityOk(PKM pk, int slotIndex)
    {
        var c = pk.Clone();
        Call(c, "RefreshAbility", slotIndex);
        c.RefreshChecksum();
        var la = new LegalityAnalysis(c);
        if (Prop(la, "Results") is System.Collections.IEnumerable rs)
        {
            bool any = false;
            foreach (var r in rs)
            {
                if (Prop(r, "Identifier")?.ToString() != "Ability") continue;
                any = true;
                if (Prop(r, "Valid") is false) return false;
            }
            if (any) return true;
        }
        return !la.Report().Split('\n').Any(l => l.StartsWith("Invalid") && l.Contains("Ability"));
    }

    // Dropdown choices limited to what PKHeX's legality checker accepts for this Pokémon.
    // If a list can't be worked out, it is left out and the UI falls back to the full list.
    [JSExport]
    public static string GetLegalChoices(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            var d = new Dictionary<string, List<Opt>>();

            var abNames = NameList("Ability", "abilitylist");
            string AN(int id) => abNames.ElementAtOrDefault(id) ?? ("#" + id);
            var slots = AbilitySlots(pk);
            var legalSlots = new List<int>();
            for (int i = 0; i < slots.Count; i++) if (slots[i] > 0 && AbilityOk(pk, i)) legalSlots.Add(i);
            if (legalSlots.Count > 0)
            {
                if (pk.GetType().GetProperty("Ability") is not null)
                {
                    var abs = new List<Opt>(); var seen = new HashSet<int>();
                    foreach (var i in legalSlots)
                        if (seen.Add(slots[i])) abs.Add(new Opt(slots[i], i == 2 ? AN(slots[i]) + " (Hidden)" : AN(slots[i])));
                    var cur = Convert.ToInt32(Prop(pk, "Ability") ?? 0);
                    if (!abs.Any(o => o.v == cur)) abs.Add(new Opt(cur, AN(cur) + " (not legal)"));
                    d["Ability"] = abs;
                }
                if (pk.GetType().GetProperty("AbilityNumber") is not null)
                {
                    string[] ord = ["First ability", "Second ability", "Hidden ability"];
                    var nums = legalSlots.Select(i => new Opt(1 << i, ord[Math.Min(i, 2)] + " - " + AN(slots[i]))).ToList();
                    var curN = Convert.ToInt32(Prop(pk, "AbilityNumber") ?? 0);
                    if (!nums.Any(o => o.v == curN)) nums.Add(new Opt(curN, "Slot " + curN + " (not legal)"));
                    d["AbilityNumber"] = nums;
                }
            }

            var legal = LegalMoves(pk);
            if (legal is not null)
            {
                var mvNames = NameList("Move", "movelist");
                foreach (var n in new[] { "Move1", "Move2", "Move3", "Move4" })
                {
                    if (pk.GetType().GetProperty(n) is null) continue;
                    var cur = Convert.ToInt32(Prop(pk, n) ?? 0);
                    var l = legal.Select(m => new Opt(m, mvNames.ElementAtOrDefault(m) ?? ("#" + m))).OrderBy(o => o.t, StringComparer.Ordinal).ToList();
                    if (cur != 0 && !legal.Contains(cur)) l.Insert(0, new Opt(cur, (mvNames.ElementAtOrDefault(cur) ?? ("#" + cur)) + " (not legal)"));
                    l.Insert(0, new Opt(0, "(None)"));
                    d[n] = l;
                }
            }
            return J(new { ok = true, opts = d });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- legal generation from the encounter database ----------

    static List<IEncounterable> Encs = [];
    static int EncsSpecies = -1;

    static object? Call(object target, string method, params object[] args)
    {
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != method || m.GetParameters().Length != args.Length) continue;
            try { return m.Invoke(target, args); } catch { }
        }
        return null;
    }

    // GenerateEncounters' signature has changed between PKHeX versions (array, IReadOnlyList,
    // ReadOnlyMemory for moves; one version or a list of versions), so build the arguments at runtime.
    static List<IEncounterable> FindEncounters(PKM template)
    {
        var result = new List<IEncounterable>();
        var t = typeof(SaveFile).Assembly.GetType("PKHeX.Core.EncounterMovesetGenerator");
        if (t is null) return result;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GenerateEncounters") continue;
            var ps = m.GetParameters();
            if (ps.Length != 3 || ps[0].ParameterType != typeof(PKM)) continue;
            object? moves = MakeMoves(ps[1].ParameterType), vers = MakeVersions(ps[2].ParameterType);
            if (moves is null || vers is null) continue;
            try
            {
                if (m.Invoke(null, [template, moves, vers]) is System.Collections.IEnumerable e)
                {
                    foreach (var x in e) if (x is IEncounterable enc) result.Add(enc);
                    return result;
                }
            }
            catch { }
        }
        return result;
    }

    static object? MakeMoves(Type t)
    {
        if (t == typeof(ushort[])) return Array.Empty<ushort>();
        if (t == typeof(ReadOnlyMemory<ushort>)) return ReadOnlyMemory<ushort>.Empty;
        if (t.IsAssignableFrom(typeof(ushort[]))) return Array.Empty<ushort>();
        return null;
    }

    static object? MakeVersions(Type t)
    {
        var v = Sav!.Version;
        if (t == typeof(GameVersion)) return v;
        if (t == typeof(GameVersion[]) || t.IsAssignableFrom(typeof(GameVersion[]))) return new[] { v };
        return null;
    }

    static object? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    static string EncName(IEncounterable e) => (Prop(e, "LongName") ?? Prop(e, "Name") ?? e.GetType().Name).ToString()!;

    static PKM? Template(int species, int form)
    {
        var pk = Sav!.BlankPKM.Clone();
        pk.Species = (ushort)species;
        TrySet(pk, ["Form"], form);
        return pk;
    }

    [JSExport]
    public static string ListEncounters(int species)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (species < 1 || species >= GameInfo.Strings.Species.Count()) return Err("Invalid species number.");
            Encs = FindEncounters(Template(species, 0)!);
            EncsSpecies = species;
            var list = Encs.Select((e, i) => new
            {
                i, name = EncName(e), kind = e.GetType().Name.Replace("Encounter", ""),
                species = Prop(e, "Species")?.ToString(), min = Prop(e, "LevelMin")?.ToString(), max = Prop(e, "LevelMax")?.ToString(),
                version = Prop(e, "Version")?.ToString(),
            });
            return J(new { ok = true, encounters = list });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Names depend on the language, and a blank test save can report an invalid one. Eggs that become another
    // species are treated as hatched. Only un-nicknamed Pokémon are touched, so event nicknames are kept.
    static void FixNames(PKM c, bool speciesChanged)
    {
        if (c.Language < 1 || c.Language > 12) TrySet(c, ["Language"], 2);
        if (speciesChanged && c.IsEgg) c.IsEgg = false;
        if (!speciesChanged && c.IsNicknamed) return;
        Call(c, "ClearNickname");
        var name = GameInfo.Strings.Species.ElementAtOrDefault(c.Species);
        if (name is not null && c.Language == 2 && c.Nickname != name) { c.IsNicknamed = false; c.Nickname = name; }
    }

    // Builds a Pokémon from one encounter, adjusts it to the wanted species/level, and returns it only if it passes the legality check.
    static (PKM? pk, string report) Build(IEncounterable enc, int species, int level, bool shiny)
    {
        if (enc is not IEncounterConvertible conv) return (null, "Encounter can't be converted.");
        if (Sav!.Language < 1 || Sav.Language > 12) TrySet(Sav, ["Language"], 2);
        PKM pk;
        try { pk = conv.ConvertToPKM(Sav!, EncounterCriteria.Unrestricted); }
        catch (Exception e) { return (null, e.Message); }
        if (pk.GetType() != Sav!.BlankPKM.GetType()) return (null, "Encounter is for a different format.");

        var first = true; string report = "";
        int min = Math.Max(1, Convert.ToInt32(Prop(enc, "LevelMin") ?? 1));
        var tries = new List<int> { Math.Max(level, min) };
        if (pk.Species != species) tries.AddRange(new[] { 16, 20, 25, 30, 32, 36, 40, 45, 50, 55, 65, 100 }.Where(l => l > tries[0]));
        foreach (var lv in tries)
        {
            var c = pk.Clone();
            var changed = c.Species != species;
            if (changed)
            {
                c.Species = (ushort)species;
                Call(c, "RefreshAbility", (int)Math.Log2(Math.Max(1, (int)c.AbilityNumber)));
            }
            FixNames(c, changed);
            if (shiny) SetShiny(c, true);
            if (lv > c.CurrentLevel) { c.CurrentLevel = (byte)lv; Call(c, "ResetPartyStats"); }
            c.RefreshChecksum();
            var la = new LegalityAnalysis(c);
            if (first) { report = la.Report(); first = false; }
            if (la.Valid) return (c, "");
        }
        return (null, report);
    }

    [JSExport]
    public static string CreatePokemon(int box, int slot, int species, int level, int encounter, bool shiny)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0 || box >= Sav.BoxCount || slot < 0 || slot >= Sav.BoxSlotCount) return Err("Invalid box slot.");
            if (species < 1 || species >= GameInfo.Strings.Species.Count()) return Err("Invalid species number.");
            if (level < 1 || level > 100) return Err("Level must be 1-100.");
            if (EncsSpecies != species) { Encs = FindEncounters(Template(species, 0)!); EncsSpecies = species; }
            if (Encs.Count == 0) return Err("No legal encounter found for that species in this game.");

            // encounter >= 0: use that one. Otherwise try encounters in order (capped, so it stays fast in the browser).
            var candidates = encounter >= 0 && encounter < Encs.Count ? [Encs[encounter]] : Encs.Take(80).ToList();
            string firstReport = "";
            foreach (var enc in candidates)
            {
                var (pk, report) = Build(enc, species, level, shiny);
                if (pk is null) { if (firstReport == "") firstReport = report; continue; }
                Store(pk, box, slot);
                return J(new { ok = true, encounter = EncName(enc) });
            }
            return Err("Couldn't build a legal version at that level.\n" + firstReport);
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
