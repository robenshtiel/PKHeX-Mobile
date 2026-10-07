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

    // Raw write to the save, without touching the undo history.
    static void Put(PKM pk, int box, int slot)
    {
        if (box < 0) Sav!.SetPartySlotAtIndex(pk, slot);
        else Sav!.SetBoxSlotAtIndex(pk, box, slot);
    }

    // Every edit goes through here, so each one is recorded (before/after copy of the slot) for undo/redo.
    static void Store(PKM pk, int box, int slot)
    {
        var before = Slot(box, slot);
        Put(pk, box, slot);
        if (before is null) return;
        var b = before.Clone(); var a = pk.Clone();
        Record(box, slot, () => Put(b.Clone(), box, slot), () => Put(a.Clone(), box, slot));
    }

    // One undoable step. Box = -2 marks a save-wide edit (trainer fields, items, Pokédex) rather than a Pokémon slot.
    record HistEdit(int Box, int Slot, Action Undo, Action Redo);

    static void Record(int box, int slot, Action undo, Action redo)
    {
        UndoStack.Add(new HistEdit(box, slot, undo, redo));
        if (UndoStack.Count > 200) UndoStack.RemoveAt(0);
        RedoStack.Clear();
    }
    static readonly List<HistEdit> UndoStack = new(), RedoStack = new();

    static string HistStep(List<HistEdit> from, List<HistEdit> to, bool undo)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (from.Count == 0) return Err(undo ? "Nothing to undo." : "Nothing to redo.");
            var e = from[^1]; from.RemoveAt(from.Count - 1);
            (undo ? e.Undo : e.Redo)();
            to.Add(e);
            return J(new { ok = true, kind = e.Box == -2 ? "save" : "slot", box = e.Box, slot = e.Slot });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    [JSExport]
    public static string UndoEdit() => HistStep(UndoStack, RedoStack, true);

    [JSExport]
    public static string RedoEdit() => HistStep(RedoStack, UndoStack, false);

    [JSExport]
    public static string HistoryState() => J(new { undo = UndoStack.Count, redo = RedoStack.Count });

    static string Info() => J(new
    {
        ok = true, game = Sav!.Version.ToString(), generation = Sav.Generation, ot = Sav.OT,
        boxes = Sav.BoxCount, slots = Sav.BoxSlotCount, party = Sav.PartyCount,
    });

    static object Summary(PKM pk, int slot)
    {
        if (pk.Species == 0) return new { slot, empty = true };
        var name = GameInfo.Strings.Species.ElementAtOrDefault(pk.Species) ?? ("#" + pk.Species);
        return new { slot, empty = false, id = (int)pk.Species, species = name, nick = pk.Nickname, level = pk.CurrentLevel, shiny = pk.IsShiny,
                     legal = IsLegalCached(pk), alpha = Prop(pk, "IsAlpha") is true };
    }

    // Legality for the box thumbnails. Cached by the Pokémon's raw data so redrawing a box doesn't re-run every check.
    static readonly Dictionary<string, bool> LegalCache = new();
    static object? LegalCacheSav;

    static bool IsLegalCached(PKM pk)
    {
        if (!ReferenceEquals(LegalCacheSav, Sav)) { LegalCache.Clear(); LegalCacheSav = Sav; }
        var key = pk.GetType().Name + Convert.ToBase64String(pk.Data);
        if (LegalCache.TryGetValue(key, out var v)) return v;
        bool ok;
        try { ok = new LegalityAnalysis(pk).Valid; } catch { ok = false; }
        if (LegalCache.Count > 4000) LegalCache.Clear();
        LegalCache[key] = ok;
        return ok;
    }

    static bool Simple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string);
    static IEnumerable<PropertyInfo> Editable(PKM pk) =>
        pk.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && Simple(p.PropertyType))
          .OrderBy(p => p.Name);

    // Type.GetProperty throws AmbiguousMatchException when a derived type re-declares a member with "new"
    // (e.g. PK9.PersonalInfo hides the base PersonalInfo). In that case use the most derived declaration.
    static PropertyInfo? FindProp(Type t, string name)
    {
        try { return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
        catch (AmbiguousMatchException)
        {
            static int Depth(Type? x) { int d = 0; for (; x?.BaseType is not null; x = x.BaseType) d++; return d; }
            return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name == name && p.GetIndexParameters().Length == 0)
                .OrderByDescending(p => Depth(p.DeclaringType)).FirstOrDefault();
        }
    }

    // ---------- value limits ----------
    // Per-field limits. Anything not listed falls back to the range of its storage type.
    static int Lim(PKM pk, string prop, int fallback) => Prop(pk, prop) is { } v ? Convert.ToInt32(v) : fallback;

    static (string Min, string Max)? Range(PKM pk, PropertyInfo p)
    {
        var n = p.Name;
        if (p.PropertyType.IsEnum) return null;
        if (n.StartsWith("EV_")) return ("0", Lim(pk, "MaxEV", 252).ToString());   // 252 per stat (Gen 1-2 use 65535)
        if (n.StartsWith("IV_")) return ("0", Lim(pk, "MaxIV", 31).ToString());    // 31 (Gen 1-2 use 15)
        if (System.Text.RegularExpressions.Regex.IsMatch(n, "^Move[1-4]_PP$")) return ("0", "64"); // 40 base PP x 1.6 with 3 PP Ups
        if (System.Text.RegularExpressions.Regex.IsMatch(n, "^Move[1-4]_PPUps$")) return ("0", "3");
        switch (n)
        {
            case "CurrentLevel": case "Stat_Level": return ("1", "100");
            case "Met_Level": return ("0", "100");
            case "EXP": return ("0", "1640000");                                   // level 100, Fluctuating growth
            case "HeightScalar": case "WeightScalar": case "Scale": return ("0", "255");
            case "Gender": return ("0", "2");
            case "TrainerTID7": return ("0", "999999");                            // 6-digit display TID
            case "TrainerSID7": return ("0", "4294");                              // 4-digit display SID
        }
        return Type.GetTypeCode(p.PropertyType) switch
        {
            TypeCode.Byte => ("0", "255"),
            TypeCode.SByte => ("-128", "127"),
            TypeCode.UInt16 => ("0", "65535"),
            TypeCode.Int16 => ("-32768", "32767"),
            TypeCode.UInt32 => ("0", "4294967295"),
            TypeCode.Int32 => ("-2147483648", "2147483647"),
            TypeCode.UInt64 => ("0", "18446744073709551615"),
            TypeCode.Int64 => ("-9223372036854775808", "9223372036854775807"),
            _ => null,
        };
    }

    const int MaxEvTotal = 510;
    static readonly string[] EvStats = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];

    // Property names change between PKHeX versions, so these helpers try several names.
    static bool TrySet(object target, string[] names, object value)
    {
        foreach (var n in names)
        {
            var p = FindProp(target.GetType(), n);
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
            var p = FindProp(source.GetType(), n);
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
            Sav = sav; UndoStack.Clear(); RedoStack.Clear();
            return Info();
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static readonly string[] Games =
    [
        "RD", "GN", "BU", "YW", "GD", "SI", "C", "R", "S", "E", "FR", "LG", "D", "P", "Pt", "HG", "SS",
        "B", "W", "B2", "W2", "X", "Y", "OR", "AS", "SN", "MN", "US", "UM", "GP", "GE", "SW", "SH",
        "BD", "SP", "PLA", "SL", "VL", "ZA",
    ];

    [JSExport]
    public static string ListGames() => J(Games.Where(g => Enum.TryParse<GameVersion>(g, out _)));

    [JSExport]
    public static string NewSave(string game, string trainer)
    {
        try
        {
            if (!Enum.TryParse<GameVersion>(game, out var v)) return Err("Unknown game: " + game);
            Sav = MakeBlank(v, string.IsNullOrWhiteSpace(trainer) ? "Rob" : trainer); UndoStack.Clear(); RedoStack.Clear();
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
            new { name = p.Name, type = p.PropertyType.Name, value = p.GetValue(pk)?.ToString(), options = p.PropertyType.IsEnum ? Enum.GetNames(p.PropertyType) : null,
                  min = Range(pk, p)?.Min, max = Range(pk, p)?.Max }).ToList();
        // IsShiny has no setter in PKHeX (it's derived from PID/TID/SID), so it is exposed as a synthetic field.
        list.Add(new { name = "IsShiny", type = "Boolean", value = (string?)pk.IsShiny.ToString(), options = (string[]?)null, min = (string?)null, max = (string?)null });
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
            if (Range(pk, p) is { } r && !t.IsEnum && t != typeof(bool))
            {
                if (!decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                    return Err($"{Nice(name)} must be a whole number.");
                if (num < decimal.Parse(r.Min, CultureInfo.InvariantCulture) || num > decimal.Parse(r.Max, CultureInfo.InvariantCulture))
                    return Err($"{Nice(name)} must be between {r.Min} and {r.Max}.");
                if (name.StartsWith("EV_") && Lim(pk, "MaxEV", 252) <= 255)
                {
                    var total = EvStats.Sum(x => "EV_" + x == name ? (int)num : Convert.ToInt32(Prop(pk, "EV_" + x) ?? 0));
                    if (total > MaxEvTotal) return Err($"EVs can't total more than {MaxEvTotal} (that would be {total}).");
                }
            }
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
            if (AutoLegal && System.Text.RegularExpressions.Regex.IsMatch(name, "^(Species|Form|CurrentLevel|EXP|Version|Move[1-4])$")) ApplyPlusFlags(pk, false);
            if (AutoRelearn && System.Text.RegularExpressions.Regex.IsMatch(name, "^(Species|Form|CurrentLevel|EXP|Version)$")) FillSuggestedRelearn(pk);
            pk.RefreshChecksum();
            Store(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- Plus flags (Legends: Z-A) and move mastery (Legends: Arceus) ----------

    static bool AutoLegal = true;

    [JSExport]
    public static bool AlphaSupported() => Sav is not null && FindProp(Sav.BlankPKM.GetType(), "IsAlpha") is not null;

    [JSExport]
    public static void SetAutoLegal(bool on) => AutoLegal = on;

    static IEnumerable<MethodInfo> StaticMethods(string typeName, string method) =>
        typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic && t.Name == typeName)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)).Where(m => m.Name == method && !m.IsGenericMethodDefinition);

    static MethodInfo? PlusMethod(PKM pk) => StaticMethods("PlusRecordApplicator", "SetPlusFlags").FirstOrDefault(m =>
        { var ps = m.GetParameters(); return ps.Length == 4 && ps[0].ParameterType.IsInstanceOfType(pk) && ps[1].ParameterType == typeof(PKM) && ps[3].ParameterType.IsEnum; });

    static MethodInfo? ShopMethod(PKM pk, bool all) => StaticMethods("MoveShopRecordApplicator", all ? "SetMoveShopFlagsAll" : "SetMoveShopFlags").FirstOrDefault(m =>
        { var ps = m.GetParameters(); return ps.Length == 2 && ps[0].ParameterType.IsInstanceOfType(pk) && ps[1].ParameterType == typeof(PKM); });

    // all = false: flags the legality check needs for the moves it knows. all = true: every flag that could legally be set.
    static bool ApplyPlusFlags(PKM pk, bool all)
    {
        try
        {
            if (PlusMethod(pk) is { } pm)
            {
                var ps = pm.GetParameters();
                var permit = Prop(pk, "PersonalInfo");
                if (permit is null || !ps[2].ParameterType.IsInstanceOfType(permit)) return false;
                var opt = Enum.Parse(ps[3].ParameterType, all ? "LegalSeedTM" : "LegalCurrent");
                pm.Invoke(null, [pk, pk, permit, opt]);
                return true;
            }
            if (ShopMethod(pk, all) is { } sm) { sm.Invoke(null, [pk, pk]); return true; }
        }
        catch { }
        return false;
    }

    [JSExport]
    public static bool PlusSupported(int box, int slot) => Slot(box, slot) is { } pk && (PlusMethod(pk) is not null || ShopMethod(pk, false) is not null);

    [JSExport]
    public static string ApplyPlus(int box, int slot, bool all)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            if (!ApplyPlusFlags(pk, all)) return Err("This Pokémon has no Plus/mastery flags (or PKHeX couldn't apply them).");
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

    // ---------- auto-legalise ----------
    // Stage 1 fixes the Pokémon in place, one kind of problem at a time, keeping a fix only if it reduces the
    // number of failed checks. Stage 2 (only if still illegal) rebuilds it from a matching encounter and carries
    // the user's data (EVs, IVs, nature, moves, ...) over group by group, dropping any group that breaks legality.

    static readonly string[] MoveProps = ["Move1", "Move2", "Move3", "Move4"];

    static int SafeInt(object? o) { try { return o is null ? -1 : Convert.ToInt32(o); } catch { return -1; } }

    static LegalityAnalysis? Analyse(PKM pk) { try { return new LegalityAnalysis(pk); } catch { return null; } }

    // True if a failed check's identifier (or, as a fallback, an "Invalid" report line) mentions `part`.
    static bool Failed(LegalityAnalysis la, string part)
    {
        if (Prop(la, "Results") is System.Collections.IEnumerable rs)
        {
            bool any = false;
            foreach (var r in rs)
            {
                any = true;
                if (Prop(r, "Valid") is false && (Prop(r, "Identifier")?.ToString() ?? "").Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (any) return false;
        }
        return la.Report().Split('\n').Any(l => l.StartsWith("Invalid") && l.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    // Number of failed checks (0 when legal).
    static int Problems(LegalityAnalysis la)
    {
        if (la.Valid) return 0;
        int n = 0;
        if (Prop(la, "Results") is System.Collections.IEnumerable rs)
            foreach (var r in rs) if (Prop(r, "Valid") is false) n++;
        if (n == 0) n = la.Report().Split('\n').Count(l => l.StartsWith("Invalid"));
        return Math.Max(n, 1);
    }

    // PKHeX's own "best moveset for this encounter" helper (MoveSetApplicator.SetMoveset).
    static void SuggestMoveset(PKM pk)
    {
        foreach (var m in StaticMethods("MoveSetApplicator", "SetMoveset"))
        {
            var ps = m.GetParameters();
            if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(pk.GetType())) continue;
            if (ps.Skip(1).Any(p => p.ParameterType != typeof(bool) && !p.HasDefaultValue)) continue;
            try
            {
                var args = new object?[ps.Length]; args[0] = pk;
                for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : false;
                m.Invoke(null, args);
                return;
            }
            catch { }
        }
    }

    static void FixAbility(PKM c)
    {
        var slots = AbilitySlots(c);
        if (slots.Count == 0) return;
        int curNum = SafeInt(Prop(c, "AbilityNumber"));
        int curIdx = curNum > 0 ? (int)Math.Log2(curNum) : -1;
        var order = new List<int>();
        if (curIdx >= 0 && curIdx < slots.Count) order.Add(curIdx);
        for (int i = 0; i < slots.Count; i++) if (!order.Contains(i)) order.Add(i);
        foreach (var i in order)
            if (slots[i] > 0 && AbilityOk(c, i)) { Call(c, "RefreshAbility", i); return; }
    }

    static void FixMoves(PKM c)
    {
        var legal = LegalMoves(c);
        var cur = MoveProps.Select(n => Math.Max(0, SafeInt(Prop(c, n)))).ToArray();
        var keep = new List<int>();
        foreach (var m in cur) if (m > 0 && (legal is null || legal.Contains(m)) && !keep.Contains(m)) keep.Add(m);
        bool bad = cur.Count(m => m > 0) != keep.Count || cur[0] == 0;
        if (!bad) return;
        // Top up from PKHeX's suggested moveset (only moves that pass the per-move check).
        var s = c.Clone();
        foreach (var n in MoveProps) TrySet(s, [n], 0);
        SuggestMoveset(s);
        foreach (var n in MoveProps)
        {
            int m = SafeInt(Prop(s, n));
            if (keep.Count < 4 && m > 0 && !keep.Contains(m) && (legal is null || legal.Contains(m))) keep.Add(m);
        }
        if (keep.Count == 0 && legal is not null) foreach (var m in legal.OrderBy(x => x).Take(4)) keep.Add(m);
        for (int i = 0; i < 4; i++)
        {
            int want = i < keep.Count ? keep[i] : 0;
            if (want == cur[i]) continue;
            TrySet(c, [MoveProps[i]], want);
            TrySet(c, [MoveProps[i] + "_PPUps"], 0);
        }
        Call(c, "HealPP");
    }

    // "Relearn all suggested moves": fills the relearn slots with what the encounter dictates, else PKHeX's suggested relearn moves.
    static bool AutoRelearn = false;

    [JSExport]
    public static void SetAutoRelearn(bool on) => AutoRelearn = on;

    static bool FillSuggestedRelearn(PKM pk)
    {
        if (FindProp(pk.GetType(), "RelearnMove1") is null) return false;
        var la = Analyse(pk);
        if (la is null) return false;
        var want = ExpectedRelearn(la).Where(x => x > 0).ToList();
        if (want.Count == 0) want = SuggestedRelearn(la).ToList();
        for (int i = 0; i < 4; i++) TrySet(pk, [RelearnNames[i]], i < want.Count ? want[i] : 0);
        return true;
    }

    [JSExport]
    public static string RelearnSuggested(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            if (!FillSuggestedRelearn(pk)) return Err("This Pokémon has no relearn moves.");
            pk.RefreshChecksum();
            Store(pk, box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Diagnostic: shows exactly what the legality check says about one move placed alone in slot 1.
    [JSExport]
    public static string MoveDebug(int box, int slot, int move)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            var c = pk.Clone();
            c.Move1 = (ushort)move; c.Move2 = 0; c.Move3 = 0; c.Move4 = 0;
            Call(c, "HealPP");
            bool plus = ApplyPlusFlags(c, false);
            c.RefreshChecksum();
            var la = new LegalityAnalysis(c);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"move id {move}; plus flags applied: {plus}; MaxMoveID: {Prop(Sav!, "MaxMoveID")}");
            sb.AppendLine($"valid overall: {la.Valid}; encounter: {(Prop(la, "EncounterMatch") is { } em ? EncName((IEncounterable)em) : "none")}");
            var info = Prop(la, "Info");
            var mvObj = info is null ? null : Prop(info, "Moves");
            sb.AppendLine($"Info.Moves type: {mvObj?.GetType().FullName ?? "null"}");
            int i = 0;
            foreach (var m in Items(mvObj)) sb.AppendLine($"  slot {++i}: {m} | Valid={Prop(m!, "Valid")}");
            var pm = PoolMethods();
            var learnable = PoolMoves(pk);
            sb.AppendLine($"GetValidMoves overloads: {pm.Count}; learnable pool size: {learnable?.Count.ToString() ?? "none"}");
            var found = typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name is "GetAllMoves" or "GetValidMoves" or "GetSuggestedMoves" or "GetMoveSet" or "GetLegalMoves" or "GetCurrentMoves" || (m.Name.Contains("Moves") && m.IsStatic && m.DeclaringType!.Name.Contains("Move")))
                .Take(40);
            foreach (var m in found) sb.AppendLine("  " + m.DeclaringType!.Name + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);
            try { sb.AppendLine("all-sources pool: " + GroupPool(pk).note); } catch (Exception ex) { sb.AppendLine("all-sources pool failed: " + ex.Message); }
            try { sb.AppendLine("level-up pool: " + LevelUpPool(pk).note); } catch (Exception ex) { sb.AppendLine("level-up pool failed: " + ex.Message); }
            sb.AppendLine("--- report ---");
            sb.AppendLine(la.Report());
            return J(new { ok = true, text = sb.ToString() });
        }
        catch (Exception e) { return Err(e.ToString()); }
    }

    static void FixRelearn(PKM c)
    {
        var rel = LegalRelearn(c);
        if (rel is null) return;
        var la = Analyse(c);
        if (la is null) return;
        var expected = ExpectedRelearn(la);
        var cur = RelearnNames.Select(n => Math.Max(0, SafeInt(Prop(c, n)))).ToArray();
        var want = new int[4];
        if (expected.Length > 0)
        {
            for (int i = 0; i < 4 && i < expected.Length; i++) want[i] = expected[i];
        }
        else
        {
            for (int i = 0; i < 4; i++) want[i] = cur[i] != 0 && rel[i].Contains(cur[i]) ? cur[i] : 0;
            if (want.All(x => x == 0) && Failed(la, "Relearn"))
            {
                var sug = SuggestedRelearn(la).Take(4).ToList();
                for (int i = 0; i < sug.Count; i++) want[i] = sug[i];
            }
        }
        for (int i = 0; i < 4; i++) if (want[i] != cur[i]) TrySet(c, [RelearnNames[i]], want[i]);
    }

    static void FixRibbons(PKM c)
    {
        var (legal, all) = RibbonSets(c);
        var ok = new HashSet<string>(legal);
        foreach (var n in all) if (!ok.Contains(n) && Prop(c, n) is true) TrySet(c, [n], false);
        c.RefreshChecksum();
        // Ribbons PKHeX reports as missing: add each legal one that lowers the ribbon problem count.
        var la = Analyse(c);
        if (la is null || !Failed(la, "Ribbon")) return;
        int cur = RibbonProblems(la);
        foreach (var n in legal)
        {
            if (Prop(c, n) is true) continue;
            TrySet(c, [n], true); c.RefreshChecksum();
            var tl = Analyse(c);
            int p = tl is null ? int.MaxValue : RibbonProblems(tl);
            if (p < cur) cur = p; else TrySet(c, [n], false);
        }
        c.RefreshChecksum();
    }

    static void FixEvs(PKM c)
    {
        int max = Lim(c, "MaxEV", 252), total = 0;
        foreach (var s in EvStats)
        {
            var n = "EV_" + s;
            int v = Math.Clamp(SafeInt(Prop(c, n)), 0, max);
            if (max <= 255 && total + v > MaxEvTotal) v = MaxEvTotal - total;
            TrySet(c, [n], v); total += v;
        }
    }

    static (string Name, string Gate, Action<PKM> Fix) Fixer(string name, string gate, Action<PKM> fix) => (name, gate, fix);

    // Stage 1. Returns the best version found; `steps` lists the fixes that were kept.
    static PKM LegaliseInPlace(PKM start, List<string> steps)
    {
        var cur = start.Clone(); cur.RefreshChecksum();
        var first = Analyse(cur);
        if (first is null) return cur;
        var la = first; int best = Problems(la);
        // Gate = failed-check names that trigger the fixer ("*" = always try).
        var fixers = new List<(string Name, string Gate, Action<PKM> Fix)>
        {
            Fixer("language/nickname", "Nickname|Language", c => FixNames(c, false)),
            Fixer("ability", "Ability", FixAbility),
            Fixer("moves", "Move", FixMoves),
            Fixer("relearn moves", "Relearn", FixRelearn),
            Fixer("ribbons", "Ribbon", FixRibbons),
            Fixer("held item", "Item", c => TrySet(c, ["HeldItem"], 0)),
            Fixer("EVs", "EV", FixEvs),
            Fixer("experience", "Level|Exp", c => { TrySet(c, ["CurrentLevel"], c.CurrentLevel); Call(c, "ResetPartyStats"); }),
            Fixer("stats", "Stat", c => Call(c, "ResetPartyStats")),
            Fixer("Plus/mastery flags", "*", c => ApplyPlusFlags(c, false)),
        };
        for (int pass = 0; pass < 2 && best > 0; pass++)
        {
            bool progress = false;
            foreach (var (name, gate, fix) in fixers)
            {
                if (best == 0) break;
                if (gate != "*" && !gate.Split('|').Any(g => Failed(la, g))) continue;
                var t = cur.Clone();
                try { fix(t); t.RefreshChecksum(); } catch { continue; }
                var nl = Analyse(t);
                if (nl is null) continue;
                int p = Problems(nl);
                if (p < best) { cur = t; la = nl; best = p; if (!steps.Contains(name)) steps.Add(name); progress = true; }
            }
            if (!progress) break;
        }
        return cur;
    }

    static readonly (string Label, string Pattern)[] CarryGroups =
    [
        ("nature", "^(Nature|StatNature)$"),
        ("held item", "^HeldItem$"),
        ("EVs", "^EV_"),
        ("IVs", "^IV_"),
        ("ability", "^(Ability|AbilityNumber)$"),
        ("moves", "^Move[1-4](_PP|_PPUps)?$"),
        ("nickname", "^(Nickname|IsNicknamed)$"),
        ("ball", "^Ball$"),
        ("size", "^(HeightScalar|WeightScalar|Scale)$"),
        ("trainer details", "^(OriginalTrainerName|OT_Name|TID16|SID16|TrainerTID7|TrainerSID7|OriginalTrainerGender|OT_Gender)$"),
    ];

    static void CopyMatching(PKM from, PKM to, string pattern)
    {
        var re = new System.Text.RegularExpressions.Regex(pattern);
        foreach (var p in Editable(from).Where(p => re.IsMatch(p.Name)))
        {
            var q = FindProp(to.GetType(), p.Name);
            if (q is null || !q.CanWrite || q.PropertyType != p.PropertyType) continue;
            try { q.SetValue(to, p.GetValue(from)); } catch { }
        }
    }

    // Stage 2. Tries encounters that match the Pokémon's species/form (closest location and game first) and keeps the
    // legal build that preserves the most of the user's data.
    static (PKM? pk, string encounter, List<string> dropped) RebuildFrom(PKM orig)
    {
        int species = orig.Species, form = Math.Max(0, SafeInt(Prop(orig, "Form"))), level = Math.Max(1, (int)orig.CurrentLevel);
        var tmpl = Template(species, form);
        if (tmpl is null) return (null, "", new List<string>());
        var encs = FindEncountersAny(tmpl);
        bool alphaGame = AlphaSupported(), alpha = Prop(orig, "IsAlpha") is true;
        int metLoc = SafeInt(Prop(orig, "MetLocation") ?? Prop(orig, "Met_Location"));
        int ver = SafeInt(Prop(orig, "Version") ?? Prop(orig, "Game"));
        var ordered = encs.Where(x => !alphaGame || (Prop(x, "IsAlpha") is true) == alpha)
            .OrderBy(x => SafeInt(Prop(x, "Location")) == metLoc ? 0 : 1)
            .ThenBy(x => SafeInt(Prop(x, "Version")) == ver ? 0 : 1)
            .Take(60).ToList();

        PKM? best = null; string bestEnc = ""; var bestDropped = new List<string>(); int bestKept = -1, built = 0;
        foreach (var enc in ordered)
        {
            foreach (var shiny in orig.IsShiny ? new[] { true, false } : new[] { false })
            {
                var (b, _) = Build(enc, species, level, shiny);
                if (b is null) continue;
                if (alphaGame && Prop(b, "IsAlpha") is bool pa && pa != alpha) continue;
                var dropped = new List<string>();
                if (orig.IsShiny && !shiny) dropped.Add("shiny");
                int kept = 0;
                foreach (var (label, pattern) in CarryGroups)
                {
                    var t = b.Clone();
                    CopyMatching(orig, t, pattern);
                    if (label == "moves") Call(t, "HealPP");
                    t.RefreshChecksum();
                    if (Analyse(t) is { Valid: true }) { b = t; kept++; } else dropped.Add(label);
                }
                if (kept > bestKept) { best = b; bestKept = kept; bestEnc = EncName(enc); bestDropped = dropped; }
                built++;
                break;
            }
            if (bestDropped.Count == 0 && best is not null) break;   // nothing lost: done
            if (built >= 6) break;                                    // keep it quick in the browser
        }
        if (best is not null)
        {
            var t = best.Clone();
            ApplyPlusFlags(t, false); t.RefreshChecksum();
            if (Analyse(t) is { Valid: true }) best = t;
        }
        return (best, bestEnc, bestDropped);
    }

    [JSExport]
    public static string AutoLegalise(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null || Sav is null) return Err("No Pokémon in that slot.");
            var la0 = new LegalityAnalysis(pk);
            if (la0.Valid) return J(new { ok = true, valid = true, changed = false, message = "Already legal." });
            int before = Problems(la0);

            var steps = new List<string>();
            var result = LegaliseInPlace(pk, steps);
            var la = new LegalityAnalysis(result);
            var dropped = new List<string>();
            if (!la.Valid)
            {
                var (rb, enc, drop) = RebuildFrom(result);
                if (rb is not null)
                {
                    result = rb; la = new LegalityAnalysis(result); dropped = drop;
                    steps = ["rebuilt from encounter: " + enc];
                }
            }
            int after = Problems(la);
            if (after >= before)
                return J(new { ok = true, valid = false, changed = false, message = "Couldn't find an automatic fix. The report lists what's wrong." });
            result.RefreshChecksum();
            Store(result, box, slot);
            return J(new { ok = true, valid = la.Valid, changed = true, steps, dropped, remaining = after });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Every occupied slot (party first as box -1, then each box) as [box, slot] pairs, for "Auto-legalise all".
    [JSExport]
    public static string OccupiedSlots()
    {
        try
        {
            if (Sav is null) return NoSave();
            var list = new List<int[]>();
            for (int i = 0; i < Sav.PartyCount; i++)
                if (Slot(-1, i) is { } p0 && p0.Species > 0) list.Add([-1, i]);
            for (int b = 0; b < Sav.BoxCount; b++)
                for (int i = 0; i < Sav.BoxSlotCount; i++)
                    if (Slot(b, i) is { } p && p.Species > 0) list.Add([b, i]);
            return J(new { ok = true, slots = list });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- names and helpers for the UI ----------

    static string[] NameList(params string[] candidates)
    {
        var s = GameInfo.Strings; var ty = s.GetType();
        foreach (var c in candidates)
        {
            var v = FindProp(ty, c)?.GetValue(s) ?? ty.GetField(c)?.GetValue(s);
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
                    var pi = FindProp(pk.GetType(), n);
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

    // LegalityInfo.Moves is an array in older PKHeX builds and Memory<MoveResult> in newer ones (not enumerable), so handle both.
    static object? FirstMoveResult(object? mv)
    {
        if (mv is null) return null;
        if (mv is System.Collections.IEnumerable e) { foreach (var m in e) return m; return null; }
        var ts = mv.GetType().GetMethod("ToArray", Type.EmptyTypes);
        if (ts?.Invoke(mv, null) is Array a && a.Length > 0) return a.GetValue(0);
        return null;
    }

    // True if this move, placed alone in slot 1 of this exact Pokémon (same encounter data), passes the legality check.
    static bool MoveSlotValid(PKM pk, int move)
    {
        var c = pk.Clone();
        c.Move1 = (ushort)move; c.Move2 = 0; c.Move3 = 0; c.Move4 = 0;
        TrySet(c, ["Move1_PPUps"], 0); TrySet(c, ["Move2_PPUps"], 0); TrySet(c, ["Move3_PPUps"], 0); TrySet(c, ["Move4_PPUps"], 0);
        Call(c, "HealPP");
        ApplyPlusFlags(c, false);   // Z-A TM/Plus and Arceus mastery moves are only valid once their flags are set
        c.RefreshChecksum();
        var la = new LegalityAnalysis(c);
        if (Prop(la, "Info") is { } info && FirstMoveResult(Prop(info, "Moves")) is { } first && Prop(first, "Valid") is bool ok) return ok;
        return !la.Report().Split('\n').Any(l => l.StartsWith("Invalid") && (l.Contains("Move 1") || l.Contains("Move1")));
    }

    // Every move PKHeX says this Pokémon can learn from any source (level-up, TM, tutor, egg, plus...), independent of
    // the matched encounter's own move rules. This is what PKHeX's own move dropdown is built from.
    static object? ResolveArg(Type pt, PKM pk, LegalityAnalysis la)
    {
        if (pt.IsAssignableFrom(pk.GetType())) return pk;
        if (pt.IsAssignableFrom(typeof(LegalityAnalysis))) return la;
        if (pt == typeof(bool)) return true;
        if (pt.IsEnum) return Enum.ToObject(pt, Enum.GetValues(pt).Cast<object>().Aggregate(0L, (a, x) => a | Convert.ToInt64(x)));
        if (Prop(la, "Info") is { } info)
            foreach (var n in new[] { "EvoChainsAllGens", "EvoChains" })
                if (Prop(info, n) is { } v && pt.IsAssignableFrom(v.GetType())) return v;
        return pt.IsValueType ? Activator.CreateInstance(pt) : null;
    }

    static List<MethodInfo> PoolMethods() => typeof(SaveFile).Assembly.GetTypes()
        .Where(t => t.IsAbstract && t.IsSealed && t.IsPublic)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(m => m.Name == "GetValidMoves" && !m.IsGenericMethodDefinition).ToList();

    static HashSet<int>? PoolMoves(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        HashSet<int>? best = null;
        foreach (var m in PoolMethods())
        {
            try
            {
                var ps = m.GetParameters();
                var args = ps.Select(p => ResolveArg(p.ParameterType, pk, la)).ToArray();
                var r = m.Invoke(null, args);
                var set = new HashSet<int>();
                if (r is ReadOnlyMemory<ushort> mem) foreach (var x in mem.ToArray()) set.Add(x);
                else if (r is System.Collections.IEnumerable e && r is not string) foreach (var x in e) set.Add(Convert.ToInt32(x));
                set.Remove(0);
                if (best is null || set.Count > best.Count) best = set;
            }
            catch { }
        }
        return best is { Count: > 0 } ? best : null;
    }

    // Level-up moves (up to the current level) from the game's own learnset, found via PKHeX's per-game LearnSource class.
    // Covers only level-up moves; TM/tutor/egg sources are added separately once their API is known.
    static string LearnCode(PKM pk) => pk.GetType().Name switch
    {
        "PA9" => "9ZA", "PK9" => "9SV", "PA8" => "8LA", "PK8" => "8SWSH", "PB8" => "8BDSP", _ => "",
    };

    static Learnset? LearnsetFor(PKM pk)
    {
        var code = LearnCode(pk);
        if (code == "") return null;
        var t = typeof(SaveFile).Assembly.GetTypes().FirstOrDefault(x => x.IsPublic && x.Name.StartsWith("LearnSource") && x.Name.EndsWith(code));
        if (t is null) return null;
        var inst = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                   ?? t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var gm = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetLearnset" && m.GetParameters().Length == 2);
        if (inst is null || gm is null) return null;
        var ps = gm.GetParameters();
        return gm.Invoke(inst, [Convert.ChangeType((int)pk.Species, ps[0].ParameterType), Convert.ChangeType(Math.Max(0, SafeInt(Prop(pk, "Form"))), ps[1].ParameterType)]) as Learnset;
    }

    // Gives the Pokémon the moves it would have at its level in-game (its latest level-up moves).
    static bool SetLevelUpMoves(PKM pk)
    {
        var ls = LearnsetFor(pk);
        if (ls is null) return false;
        Span<ushort> mv = stackalloc ushort[4];
        ls.SetEncounterMoves((byte)Math.Max(1, (int)pk.CurrentLevel), mv);
        pk.Move1 = mv[0]; pk.Move2 = mv[1]; pk.Move3 = mv[2]; pk.Move4 = mv[3];
        TrySet(pk, ["Move1_PPUps"], 0); TrySet(pk, ["Move2_PPUps"], 0); TrySet(pk, ["Move3_PPUps"], 0); TrySet(pk, ["Move4_PPUps"], 0);
        Call(pk, "HealPP");
        return true;
    }

    static (HashSet<int>? moves, string note) LevelUpPool(PKM pk)
    {
        var code = LearnCode(pk);
        if (code == "") return (null, "no learn-source mapping for " + pk.GetType().Name);
        var t = typeof(SaveFile).Assembly.GetTypes().FirstOrDefault(x => x.IsPublic && x.Name.StartsWith("LearnSource") && x.Name.EndsWith(code));
        if (t is null) return (null, "no LearnSource*" + code + " type");
        var inst = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                   ?? t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (inst is null) return (null, t.Name + " has no Instance");
        var gm = t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetLearnset" && m.GetParameters().Length == 2);
        if (gm is null) return (null, t.Name + " has no GetLearnset(species, form)");
        var set = new HashSet<int>();
        int form = SafeInt(Prop(pk, "Form"));
        // The Pokémon's own species, plus earlier stages that PKHeX reports for it (best effort).
        foreach (var sp in new[] { (int)pk.Species })
        {
            var ps = gm.GetParameters();
            var ls = gm.Invoke(inst, [Convert.ChangeType(sp, ps[0].ParameterType), Convert.ChangeType(form, ps[1].ParameterType)]) as Learnset;
            if (ls is null) continue;
            foreach (var mv in ls.GetMoveRange((byte)Math.Max(1, (int)pk.CurrentLevel))) if (mv != 0) set.Add(mv);
        }
        return (set.Count > 0 ? set : null, t.Name + ": " + set.Count + " level-up moves");
    }

    // Everything PKHeX says this Pokémon can learn from any source, via ILearnGroup.GetAllMoves, walking back through earlier games.
    static ILearnGroup? CurrentGroup(PKM pk, LegalityAnalysis la)
    {
        foreach (var m in typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic)
                     .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Where(m => typeof(ILearnGroup).IsAssignableFrom(m.ReturnType) && !m.IsGenericMethodDefinition)
                     .OrderByDescending(m => m.Name.Contains("Current")))
        {
            var ps = m.GetParameters();
            if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(pk.GetType())) continue;
            try
            {
                var args = ps.Select(p => ResolveArg(p.ParameterType, pk, la)).ToArray();
                if (m.Invoke(null, args) is ILearnGroup g) return g;
            }
            catch { }
        }
        return null;
    }

    static (HashSet<int>? moves, string note) GroupPool(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        if (Prop(la, "EncounterMatch") is not IEncounterTemplate enc) return (null, "no encounter template");
        if (Prop(la, "Info") is not { } info || Prop(info, "EvoChainsAllGens") is not EvolutionHistory history) return (null, "no evolution history");
        var group = CurrentGroup(pk, la);
        if (group is null) return (null, "no method returning the current ILearnGroup");
        int size = Math.Max(1, Math.Max(SafeInt(Prop(Sav!, "MaxMoveID")), (int)group.MaxMoveID)) + 1;
        var flags = new bool[size + 64];
        int groups = 0;
        for (var g = group; g is not null && groups < 12; groups++)
        {
            g.GetAllMoves(flags, pk, history, enc, MoveSourceType.All, LearnOption.Current);
            g = g.GetPrevious(pk, history, enc, LearnOption.Current);
        }
        var set = new HashSet<int>();
        for (int i = 1; i < flags.Length; i++) if (flags[i]) set.Add(i);
        return (set.Count > 0 ? set : null, $"{groups} group(s) walked, {set.Count} moves");
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

        // PKHeX's suggested list is not complete (it can be just the current level-up moves), so it is only added
        // to, never used to narrow the search: every move id is checked against the real legality check.
        int max = Prop(Sav!, "MaxMoveID") is { } mm ? Convert.ToInt32(mm) : NameList("Move", "movelist").Length - 1;
        IEnumerable<int> cand = Enumerable.Range(1, Math.Max(1, max));
        var pool = SuggestedMoves(pk);
        if (pool is not null) cand = cand.Union(pool);
        cand = cand.Union(MoveProps.Select(n => SafeInt(Prop(pk, n))).Where(x => x > 0)).ToList();

        var set = new HashSet<int>();
        foreach (var m in cand) { try { if (MoveSlotValid(pk, m)) set.Add(m); } catch { } }
        // Moves the Pokémon can learn from any source count as legal choices even when the matched encounter
        // (e.g. a Hyperspace wild encounter) insists on its own starting moves.
        try { if (PoolMoves(pk) is { } learnable) set.UnionWith(learnable); } catch { }
        try { if (GroupPool(pk).moves is { } all) set.UnionWith(all); } catch { }
        try { if (LevelUpPool(pk).moves is { } lvl) set.UnionWith(lvl); } catch { }
        // If the probe accepts nothing beyond the Pokémon's own moves, the probe itself is failing (not the Pokémon),
        // so return null and let the editor offer the full move list instead of a list of 4.
        var own = MoveProps.Select(n => SafeInt(Prop(pk, n))).Where(x => x > 0).ToHashSet();
        if (set.Count == 0 || set.All(own.Contains)) return null;
        if (MoveCache.Count > 64) MoveCache.Clear();
        MoveCache[key] = set;
        return set;
    }

    // ---------- legal relearn moves and ribbons ----------

    static readonly string[] RelearnNames = ["RelearnMove1", "RelearnMove2", "RelearnMove3", "RelearnMove4"];

    // Enumerates arrays, lists and Memory<T>/ReadOnlyMemory<T> values (which are not IEnumerable) alike.
    static List<object?> Items(object? o)
    {
        var res = new List<object?>();
        if (o is null) return res;
        if (o is System.Collections.IEnumerable e && o is not string) { foreach (var x in e) res.Add(x); return res; }
        if (o.GetType().GetMethod("ToArray", Type.EmptyTypes)?.Invoke(o, null) is System.Collections.IEnumerable a)
            foreach (var x in a) res.Add(x);
        return res;
    }

    // Relearn moves the matched encounter itself dictates (events and similar); empty when it dictates none.
    static int[] ExpectedRelearn(LegalityAnalysis la)
    {
        var res = new List<int>();
        try
        {
            var r = Prop(la, "EncounterMatch") is { } enc ? Prop(enc, "Relearn") : null;
            if (r is null) return [];
            foreach (var n in new[] { "Move1", "Move2", "Move3", "Move4" })
                if (FindProp(r.GetType(), n) is not null) res.Add(Convert.ToInt32(Prop(r, n) ?? 0));
            if (res.Count == 0) foreach (var x in Items(r)) res.Add(Convert.ToInt32(x ?? 0));
        }
        catch { return []; }
        return res.Any(x => x > 0) ? res.ToArray() : [];
    }

    // Relearn moves PKHeX suggests for the encounter (GetSuggestedRelearn* helpers); empty if none can be found.
    static HashSet<int> SuggestedRelearn(LegalityAnalysis la)
    {
        var set = new HashSet<int>();
        foreach (var t in typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic))
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!m.Name.StartsWith("GetSuggestedRelearn") || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(typeof(LegalityAnalysis))) continue;
                var args = new object?[ps.Length]; args[0] = la;
                bool skip = false;
                for (int i = 1; i < ps.Length; i++)
                {
                    if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else { skip = true; break; }
                }
                if (skip) continue;
                try
                {
                    var r = m.Invoke(null, args);
                    if (r is ReadOnlyMemory<ushort> mem) foreach (var x in mem.ToArray()) set.Add(x);
                    else if (r is System.Collections.IEnumerable en) foreach (var x in en) set.Add(Convert.ToInt32(x));
                }
                catch { }
            }
        }
        set.Remove(0);
        return set;
    }

    // True if this move, placed alone in relearn slot idx of this exact Pokémon, passes the relearn check.
    static bool RelearnSlotValid(PKM pk, int move, int idx)
    {
        var c = pk.Clone();
        for (int i = 0; i < 4; i++) TrySet(c, [RelearnNames[i]], i == idx ? move : 0);
        c.RefreshChecksum();
        var la = new LegalityAnalysis(c);
        if (Prop(la, "Info") is { } info)
        {
            var rl = Items(Prop(info, "Relearn"));
            if (idx < rl.Count && rl[idx] is { } item && Prop(item, "Valid") is bool ok) return ok;
        }
        return !la.Report().Split('\n').Any(l => l.StartsWith("Invalid") && l.Contains("Relearn") && l.Contains((idx + 1).ToString()));
    }

    static readonly Dictionary<string, HashSet<int>[]> RelearnCache = new();

    // One set of legal moves per relearn slot; every set is empty when the Pokémon's encounter allows no relearn moves.
    // Cached like LegalMoves (the key leaves out the current moves).
    static HashSet<int>[]? LegalRelearn(PKM pk)
    {
        if (FindProp(pk.GetType(), "RelearnMove1") is null) return null;
        var key = MoveKey(pk);
        if (RelearnCache.TryGetValue(key, out var hit)) return hit;

        var la = new LegalityAnalysis(pk);
        var expected = ExpectedRelearn(la);
        var sets = new[] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
        if (expected.Length > 0)
        {
            // The encounter dictates the exact relearn moves, slot by slot.
            // Always list them: a required move must be selectable even if the single-slot probe rejects it
            // (the probe zeroes the other slots, which can trip the "expected moves" check).
            for (int i = 0; i < 4 && i < expected.Length; i++)
                if (expected[i] > 0) sets[i].Add(expected[i]);
        }
        else
        {
            var pool = SuggestedMoves(pk);
            IEnumerable<int> cand;
            if (pool is not null) cand = pool.ToList();
            else
            {
                int max = Prop(Sav!, "MaxMoveID") is { } mm ? Convert.ToInt32(mm) : NameList("Move", "movelist").Length - 1;
                cand = Enumerable.Range(1, Math.Max(1, max));
            }
            var ok = new HashSet<int>();
            foreach (var m in cand) { try { if (RelearnSlotValid(pk, m, 0)) ok.Add(m); } catch { } }
            // Moves PKHeX itself says the encounter requires (e.g. egg moves) are always offered.
            ok.UnionWith(SuggestedRelearn(la));
            foreach (var s in sets) s.UnionWith(ok);
        }
        if (RelearnCache.Count > 64) RelearnCache.Clear();
        RelearnCache[key] = sets;
        return sets;
    }

    // Number of ribbon problems PKHeX reports (invalid and missing ribbons are separate entries).
    static int RibbonProblems(LegalityAnalysis la)
    {
        if (Prop(la, "Results") is System.Collections.IEnumerable rs)
        {
            int n = 0; bool any = false;
            foreach (var r in rs)
            {
                if (Prop(r, "Identifier")?.ToString() != "Ribbon") continue;
                any = true;
                if (Prop(r, "Valid") is false) n++;
            }
            if (any) return n;
        }
        return la.Report().Split('\n').Count(l => l.StartsWith("Invalid") && l.Contains("Ribbon"));
    }

    static readonly Dictionary<string, List<string>> RibbonCache = new();

    // Ribbons and marks that PKHeX accepts on this Pokémon. Each one is tried alone on a ribbon-free copy,
    // so the cost is one legality check per ribbon the first time; results are cached per encounter data.
    static (List<string> legal, List<string> all) RibbonSets(PKM pk)
    {
        var all = Editable(pk).Where(p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Ribbon")).Select(p => p.Name).ToList();
        var key = pk.GetType().Name + "|" + MoveKey(pk);
        if (!RibbonCache.TryGetValue(key, out var legal))
        {
            legal = [];
            var baseline = pk.Clone();
            foreach (var n in all) TrySet(baseline, [n], false);
            baseline.RefreshChecksum();
            int baseProblems = RibbonProblems(new LegalityAnalysis(baseline));
            foreach (var n in all)
            {
                try
                {
                    var c = baseline.Clone();
                    TrySet(c, [n], true);
                    c.RefreshChecksum();
                    if (RibbonProblems(new LegalityAnalysis(c)) <= baseProblems) legal.Add(n);
                }
                catch { }
            }
            if (RibbonCache.Count > 32) RibbonCache.Clear();
            RibbonCache[key] = legal;
        }
        return (legal, all);
    }

    [JSExport]
    public static string GetLegalRibbons(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null) return Err("No Pokémon in that slot.");
            var (legal, all) = RibbonSets(pk);
            return J(new { ok = true, legal, all });
        }
        catch (Exception e) { return Err(e.Message); }
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
                if (FindProp(pk.GetType(), "Ability") is not null)
                {
                    var abs = new List<Opt>(); var seen = new HashSet<int>();
                    foreach (var i in legalSlots)
                        if (seen.Add(slots[i])) abs.Add(new Opt(slots[i], i == 2 ? AN(slots[i]) + " (Hidden)" : AN(slots[i])));
                    var cur = Convert.ToInt32(Prop(pk, "Ability") ?? 0);
                    if (!abs.Any(o => o.v == cur)) abs.Add(new Opt(cur, AN(cur) + " (not legal)"));
                    d["Ability"] = abs;
                }
                if (FindProp(pk.GetType(), "AbilityNumber") is not null)
                {
                    string[] ord = ["First ability", "Second ability", "Hidden ability"];
                    var nums = legalSlots.Select(i => new Opt(1 << i, ord[Math.Min(i, 2)] + " - " + AN(slots[i]))).ToList();
                    var curN = Convert.ToInt32(Prop(pk, "AbilityNumber") ?? 0);
                    if (!nums.Any(o => o.v == curN)) nums.Add(new Opt(curN, "Slot " + curN + " (not legal)"));
                    d["AbilityNumber"] = nums;
                }
            }

            var legal = LegalMoves(pk);
            if (legal is null)
            {
                // The legality probe gave no usable list: offer every move rather than locking the fields.
                var all = NameList("Move", "movelist");
                int mx = Prop(Sav!, "MaxMoveID") is { } mm2 ? Convert.ToInt32(mm2) : all.Length - 1;
                legal = Enumerable.Range(1, Math.Max(1, Math.Min(mx, all.Length - 1))).Where(i => !string.IsNullOrWhiteSpace(all[i]) && all[i] != "???").ToHashSet();
            }
            if (legal is not null)
            {
                var mvNames = NameList("Move", "movelist");
                foreach (var n in new[] { "Move1", "Move2", "Move3", "Move4" })
                {
                    if (FindProp(pk.GetType(), n) is null) continue;
                    var cur = Convert.ToInt32(Prop(pk, n) ?? 0);
                    var l = legal.Select(m => new Opt(m, mvNames.ElementAtOrDefault(m) ?? ("#" + m))).OrderBy(o => o.t, StringComparer.Ordinal).ToList();
                    if (cur != 0 && !legal.Contains(cur)) l.Insert(0, new Opt(cur, (mvNames.ElementAtOrDefault(cur) ?? ("#" + cur)) + " (not legal)"));
                    l.Insert(0, new Opt(0, "(None)"));
                    d[n] = l;
                }
            }

            var rel = LegalRelearn(pk);
            if (rel is not null)
            {
                var mvNames = NameList("Move", "movelist");
                for (int i = 0; i < 4; i++)
                {
                    if (FindProp(pk.GetType(), RelearnNames[i]) is null) continue;
                    var cur = Convert.ToInt32(Prop(pk, RelearnNames[i]) ?? 0);
                    var l = rel[i].Select(m => new Opt(m, mvNames.ElementAtOrDefault(m) ?? ("#" + m))).OrderBy(o => o.t, StringComparer.Ordinal).ToList();
                    if (cur != 0 && !rel[i].Contains(cur)) l.Insert(0, new Opt(cur, (mvNames.ElementAtOrDefault(cur) ?? ("#" + cur)) + " (not legal)"));
                    l.Insert(0, new Opt(0, "(None)"));
                    d[RelearnNames[i]] = l;
                }
            }

            if (FindProp(pk.GetType(), "HeldItem") is not null)
            {
                var itNames = NameList("Item", "itemlist");
                var curI = Convert.ToInt32(Prop(pk, "HeldItem") ?? 0);
                var cacheKey = pk.GetType().Name + "|" + pk.Format;
                if (!HeldCache.TryGetValue(cacheKey, out var allowed))
                {
                allowed = new List<Opt>();
                for (int id = 1; id < itNames.Length; id++)
                {
                    var nm = itNames[id];
                    if (string.IsNullOrWhiteSpace(nm) || nm == "???" || nm.StartsWith("(")) continue;
                    if (HeldItemAllowed(pk, id)) allowed.Add(new Opt(id, nm));
                }
                allowed = allowed.OrderBy(o => o.t, StringComparer.Ordinal).ToList();
                HeldCache[cacheKey] = allowed;
                }
                if (allowed.Count > 0)
                {
                    allowed = allowed.ToList();
                    if (curI != 0 && !allowed.Any(o => o.v == curI)) allowed.Insert(0, new Opt(curI, (itNames.ElementAtOrDefault(curI) ?? ("#" + curI)) + " (not legal)"));
                    allowed.Insert(0, new Opt(0, "(None)"));
                    d["HeldItem"] = allowed;
                }
            }
            return J(new { ok = true, opts = d });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static readonly Dictionary<string, List<Opt>> HeldCache = new();

    // Asks PKHeX whether this item can be held in this Pokémon's generation/context.
    static bool HeldItemAllowed(PKM pk, int item)
    {
        foreach (var t in typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic && t.Name == "ItemRestrictions"))
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "IsHeldItemAllowed") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                try
                {
                    object? a1;
                    if (ps[1].ParameterType == typeof(int) || ps[1].ParameterType == typeof(byte)) a1 = Convert.ChangeType(pk.Format, ps[1].ParameterType);
                    else if (ps[1].ParameterType.IsEnum) a1 = Enum.ToObject(ps[1].ParameterType, Convert.ToInt32(Prop(pk, "Context") ?? 0));
                    else continue;
                    var a0 = Convert.ChangeType(item, ps[0].ParameterType);
                    if (m.Invoke(null, [a0, a1]) is bool b) return b;
                }
                catch { }
            }
        }
        return true; // no checker found: leave the item in rather than hide everything
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
    static List<IEncounterable> FindEncounters(PKM template) => FindEncounters(template, null);

    // Other games of the same generation (same Pokémon file format), e.g. R/S/E when the save is FireRed. Used only when the
    // save's own game has no encounter: such Pokémon are legal when they were caught in another game and traded over.
    static GameVersion[] CrossVersions()
    {
        string[] names = Sav!.Generation switch
        {
            3 => ["S", "R", "E", "FR", "LG"],
            4 => ["D", "P", "Pt", "HG", "SS"],
            5 => ["B", "W", "B2", "W2"],
            6 => ["X", "Y", "OR", "AS"],
            7 => ["SN", "MN", "US", "UM"],
            8 => ["SW", "SH", "BD", "SP"],
            9 => ["SL", "VL", "ZA"],
            _ => [],
        };
        return names.Select(n => Enum.TryParse<GameVersion>(n, out var v) ? (GameVersion?)v : null)
            .Where(v => v is not null && v.Value != Sav.Version).Select(v => v!.Value).Distinct().ToArray();
    }

    // Native encounters first; only if the save's own game has none, look in the other games of the generation.
    static List<IEncounterable> FindEncountersAny(PKM template)
    {
        var native = FindEncounters(template, null);
        if (native.Count > 0) return native;
        var others = CrossVersions();
        return others.Length == 0 ? native : FindEncounters(template, others);
    }

    static bool IsCrossGame(IEncounterable enc)
    {
        var v = Prop(enc, "Version");
        return v is not null && SafeInt(v) > 0 && SafeInt(v) != SafeInt(Sav!.Version);
    }

    static List<IEncounterable> FindEncounters(PKM template, GameVersion[]? versions)
    {
        var result = new List<IEncounterable>();
        var t = typeof(SaveFile).Assembly.GetType("PKHeX.Core.EncounterMovesetGenerator");
        if (t is null) return result;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GenerateEncounters") continue;
            var ps = m.GetParameters();
            if (ps.Length != 3 || ps[0].ParameterType != typeof(PKM)) continue;
            object? moves = MakeMoves(ps[1].ParameterType), vers = MakeVersions(ps[2].ParameterType, versions);
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

    static object? MakeVersions(Type t, GameVersion[]? over = null)
    {
        var all = over is { Length: > 0 } ? over : new[] { Sav!.Version };
        if (t == typeof(GameVersion)) return all[0];
        if (t == typeof(GameVersion[]) || t.IsAssignableFrom(typeof(GameVersion[]))) return all;
        return null;
    }

    static object? Prop(object o, string name) => FindProp(o.GetType(), name)?.GetValue(o);

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
            Encs = FindEncountersAny(Template(species, 0)!);
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

    // Names depend on the language, and a blank save can report an invalid one. Eggs that become another
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

    // Trade evolutions (e.g. Milotic) require a Pokémon that has been traded, i.e. one with a handling trainer.
    static void ForceTraded(PKM pk)
    {
        var ht = TryGet(pk, ["HandlingTrainerName", "HT_Name"])?.ToString();
        if (!string.IsNullOrEmpty(ht)) return;
        var ot = TryGet(pk, ["OriginalTrainerName", "OT_Name"])?.ToString() ?? "";
        var saveOt = Prop(Sav!, "OT")?.ToString() ?? "";
        var name = saveOt.Length > 0 && saveOt != ot ? saveOt : "Partner";
        int gender = SafeInt(Prop(Sav!, "Gender")); if (gender < 0) gender = 0;
        int lang = SafeInt(Prop(Sav!, "Language")); if (lang < 1) lang = 2;
        TrySet(pk, ["HandlingTrainerName", "HT_Name"], name);
        TrySet(pk, ["HandlingTrainerGender", "HT_Gender"], gender);
        TrySet(pk, ["HandlingTrainerLanguage", "HT_Language"], lang);
        TrySet(pk, ["CurrentHandler"], 1);
        TrySet(pk, ["HandlingTrainerFriendship", "HT_Friendship"], 50);
    }

    // Events and gifts come with their own fixed OT. If that isn't the save's trainer, the save's trainer has to be the
    // current handler (HT name, gender, language, memories) or the check says "Current handler cannot be the OT".
    static void FixHandler(PKM pk, IEncounterable enc)
    {
        try
        {
            var otName = TryGet(pk, ["OriginalTrainerName", "OT_Name"])?.ToString();
            var saveOt = Prop(Sav!, "OT")?.ToString();
            if (string.IsNullOrEmpty(otName) || string.IsNullOrEmpty(saveOt) || otName == saveOt) return;

            // PKHeX's own helper sets the handler and a matching memory, so prefer it.
            foreach (var m in typeof(SaveFile).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed && t.IsPublic)
                         .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                         .Where(m => m.Name is "SetHandlerAndMemory" or "SetHandlerandMemory" && !m.IsGenericMethodDefinition))
            {
                var ps = m.GetParameters();
                if (ps.Length < 2 || !ps[0].ParameterType.IsAssignableFrom(pk.GetType())) continue;
                try
                {
                    var args = new object?[ps.Length]; args[0] = pk;
                    for (int i = 1; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        args[i] = pt.IsInstanceOfType(Sav) ? Sav : pt.IsInstanceOfType(enc) ? enc : ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    }
                    m.Invoke(null, args);
                    if (SafeInt(TryGet(pk, ["CurrentHandler"])) == 1) return;
                }
                catch { }
            }

            // Fallback: set the handler fields directly.
            TrySet(pk, ["HandlingTrainerName", "HT_Name"], saveOt!);
            int gender = SafeInt(Prop(Sav!, "Gender")); if (gender < 0) gender = 0;
            int lang = SafeInt(Prop(Sav!, "Language")); if (lang < 1) lang = 2;
            TrySet(pk, ["HandlingTrainerGender", "HT_Gender"], gender);
            TrySet(pk, ["HandlingTrainerLanguage", "HT_Language"], lang);
            TrySet(pk, ["CurrentHandler"], 1);
            TrySet(pk, ["HandlingTrainerFriendship", "HT_Friendship"], 50);
        }
        catch { }
    }

    // Evolutions that count something (Rage Fist uses, damage taken, critical hits) store it in the form argument, and the
    // required value differs per species. Rather than hard-coding per game, set it directly (and the Rage Fist counter if
    // this PKHeX version has one separately).
    static void SetFormArg(PKM pk, int value)
    {
        TrySet(pk, ["FormArgument"], value);
        if (pk.Species == 979 || pk.Species == 57) TrySet(pk, ["RageFistTimes"], value);
    }

    // Builds a Pokémon from one encounter, adjusts it to the wanted species/level, and returns it only if it passes the legality check.
    static (PKM? pk, string report) Build(IEncounterable enc, int species, int level, bool shiny, bool allowFix = false)
    {
        if (enc is not IEncounterConvertible conv) return (null, "Encounter can't be converted.");
        if (Sav!.Language < 1 || Sav.Language > 12) TrySet(Sav, ["Language"], 2);
        PKM pk;
        try { pk = conv.ConvertToPKM(Sav!, EncounterCriteria.Unrestricted); }
        catch (Exception e) { return (null, e.Message); }
        if (pk.GetType() != Sav!.BlankPKM.GetType()) return (null, "Encounter is for a different format.");
        FixHandler(pk, enc);

        var first = true; string report = ""; string fixReport = "";
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
                // Regional/alternate forms of the earlier stage (Galarian Farfetch'd, roaming Gimmighoul...) don't carry over
                // to species that only have one form.
                if (FormCountOf(species) <= 1 && SafeInt(Prop(c, "Form")) > 0) TrySet(c, ["Form"], 0);
                Call(c, "RefreshAbility", (int)Math.Log2(Math.Max(1, (int)c.AbilityNumber)));
                SetFormArg(c, 0);   // the earlier stage's counter doesn't apply to the evolved species
            }
            FixNames(c, changed);
            if (shiny) SetShiny(c, true);
            if (lv > c.CurrentLevel) { c.CurrentLevel = (byte)lv; Call(c, "ResetPartyStats"); }
            ApplyPlusFlags(c, false);   // current PKHeX requires Plus/mastery flags for the moves it generates
            c.RefreshChecksum();
            var la = new LegalityAnalysis(c);
            if (first) { report = la.Report(); first = false; }
            if (la.Valid) return (c, "");
            if (allowFix)
            {
                // Evolved species often need small fixes (moves, relearn, flags) that the auto-legaliser knows how to make,
                // plus the state a special evolution leaves behind (traded, Rage Fist used 20 times).
                var variants = new List<Action<PKM>>
                {
                    _ => { },
                    x => ForceTraded(x),
                    x => { TrySet(x, ["RageFistTimes"], 20); if (x.Species == 979) x.Move1 = 889; },   // Annihilape: Rage Fist used 20 times
                    x => { ForceTraded(x); TrySet(x, ["RageFistTimes"], 20); if (x.Species == 979) x.Move1 = 889; },
                    x => SetLevelUpMoves(x),
                    x => { ForceTraded(x); SetLevelUpMoves(x); },
                    x => { ForceTraded(x); TrySet(x, ["HeldItem"], 537); },                        // Prism Scale (Feebas)
                };
                foreach (var arg in new[] { 1, 3, 20, 49 })   // Sirfetch'd crits, Annihilape Rage Fist, Runerigus damage...
                {
                    int a = arg;
                    variants.Add(x => { SetFormArg(x, a); if (x.Species == 979) x.Move1 = 889; });
                    variants.Add(x => { ForceTraded(x); SetFormArg(x, a); if (x.Species == 979) x.Move1 = 889; });
                    variants.Add(x => { ForceTraded(x); SetFormArg(x, a); SetLevelUpMoves(x); if (x.Species == 979) x.Move1 = 889; });
                }
                for (int vi = 0; vi < variants.Count; vi++)
                {
                    try
                    {
                        var d = c.Clone(); variants[vi](d);
                        var fixedPk = LegaliseInPlace(d, new List<string>());
                        ApplyPlusFlags(fixedPk, false); fixedPk.RefreshChecksum();
                        var fl = new LegalityAnalysis(fixedPk);
                        if (fl.Valid) return (fixedPk, "");
                        if (vi == 0 && fixReport == "") fixReport = fl.Report();   // what is still wrong after the normal fixes
                    }
                    catch { }
                }
            }
        }
        return (null, fixReport != "" ? fixReport : report);
    }

    // ---------- legal living dex ----------

    // Species that exist in the loaded game, in dex order, and how many box slots the save has.
    [JSExport]
    public static string LivingDexPlan()
    {
        try
        {
            if (Sav is null) return NoSave();
            int top = GameInfo.Strings.Species.Count() - 1;
            int declared = SafeInt(Prop(Sav, "MaxSpeciesID"));
            int max = declared > 0 ? Math.Min(declared, top) : top;
            var personal = Prop(Sav, "Personal");
            var mi = personal?.GetType().GetMethods().FirstOrDefault(x => x.Name == "IsSpeciesInGame" && x.GetParameters().Length == 1);
            var list = new List<int>();
            for (int sp = 1; sp <= max; sp++)
            {
                bool present = true;
                if (mi is not null) { try { present = (bool)mi.Invoke(personal, [Convert.ChangeType(sp, mi.GetParameters()[0].ParameterType)])!; } catch { present = true; } }
                if (present) list.Add(sp);
            }
            return J(new { ok = true, species = list, capacity = Sav.BoxCount * Sav.BoxSlotCount });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static int FormCountOf(int species)
    {
        try
        {
            var personal = Prop(Sav!, "Personal");
            if (personal is null) return 1;
            var entry = personal.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(int))
                .Select(p => p.GetValue(personal, [species])).FirstOrDefault(x => x is not null);
            return Math.Max(1, Math.Min(40, SafeInt(entry is null ? null : Prop(entry, "FormCount"))));
        }
        catch { return 1; }
    }

    // First line of a legality report that says something is wrong, for telling the user why a species was skipped.
    static string FirstProblem(string report)
    {
        var lines = report.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Invalid") || l.StartsWith("Fishy")).Take(3).ToList();
        return lines.Count > 0 ? string.Join(" | ", lines) : report.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    // Builds one legal Pokémon of this species and stores it at position `index` (box-major). Shiny is tried first when
    // asked for; if no encounter can legally be shiny, the normal one is used instead. If the default form has no legal
    // encounter, other forms are tried; evolved species that fail get a second pass with the auto-legaliser's fixes.
    [JSExport]
    public static string LivingDexAdd(int index, int species, bool shiny)
    {
        try
        {
            if (Sav is null) return NoSave();
            int box = index / Sav.BoxSlotCount, slot = index % Sav.BoxSlotCount;
            if (box >= Sav.BoxCount) return Err("Out of box space.");
            var alphaGame = AlphaSupported();
            int forms = FormCountOf(species);
            string reason = "no encounter found";
            PKM? best = null; var gotShiny = false, gotCross = false;

            foreach (var fix in new[] { false, true })
            {
                for (int form = 0; form < forms && best is null; form++)
                {
                    var encs = FindEncountersAny(Template(species, form)!)
                        .OrderBy(x => alphaGame && Prop(x, "IsAlpha") is true ? 1 : 0).Take(80).ToList();
                    if (encs.Count == 0) continue;
                    if (shiny)
                        foreach (var enc in encs)
                        {
                            var (pk, rep) = Build(enc, species, 1, true, fix);
                            if (pk is not null && pk.IsShiny) { best = pk; gotShiny = true; gotCross = IsCrossGame(enc); break; }
                        }
                    if (best is null)
                        foreach (var enc in encs)
                        {
                            var (pk, rep) = Build(enc, species, 1, false, fix);
                            if (pk is not null) { best = pk; gotCross = IsCrossGame(enc); break; }
                            if (fix && reason == "no encounter found") reason = FirstProblem(rep);
                        }
                }
                if (best is not null) break;
            }
            if (best is null) return Err(reason);
            Store(best, box, slot);
            return J(new { ok = true, shiny = gotShiny, cross = gotCross });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string CreatePokemon(int box, int slot, int species, int level, int encounter, bool shiny, bool alpha)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0 || box >= Sav.BoxCount || slot < 0 || slot >= Sav.BoxSlotCount) return Err("Invalid box slot.");
            if (species < 1 || species >= GameInfo.Strings.Species.Count()) return Err("Invalid species number.");
            if (level < 1 || level > 100) return Err("Level must be 1-100.");
            if (EncsSpecies != species) { Encs = FindEncountersAny(Template(species, 0)!); EncsSpecies = species; }
            if (Encs.Count == 0) return Err("No legal encounter found for that species in this game.");

            // encounter >= 0: use that one. Otherwise try encounters in order (capped, so it stays fast in the browser).
            var alphaGame = AlphaSupported();
            var candidates = encounter >= 0 && encounter < Encs.Count ? [Encs[encounter]]
                : Encs.Where(x => !alphaGame || (Prop(x, "IsAlpha") is true) == alpha).Take(80).ToList();
            if (candidates.Count == 0) return Err(alpha ? "No Alpha encounter found for that species in this game." : "No legal encounter found for that species in this game.");
            string firstReport = "", fixedReport = "";
            foreach (var enc in candidates)
            {
                var (pk, report) = Build(enc, species, level, shiny);
                if (pk is null) { if (firstReport == "") firstReport = report; continue; }
                if (alphaGame && encounter < 0 && Prop(pk, "IsAlpha") is bool pa && pa != alpha) continue;
                if (AutoLegal) { ApplyPlusFlags(pk, false); pk.RefreshChecksum(); }
                Store(pk, box, slot);
                return J(new { ok = true, encounter = EncName(enc), cross = IsCrossGame(enc) });
            }
            // Second pass: let the auto-legaliser's fixes and special-evolution state (traded, Rage Fist) be applied.
            foreach (var enc in candidates.Take(25))
            {
                var (pk, report) = Build(enc, species, level, shiny, true);
                if (pk is null) { if (!string.IsNullOrEmpty(report)) fixedReport = report; continue; }
                if (alphaGame && encounter < 0 && Prop(pk, "IsAlpha") is bool pa2 && pa2 != alpha) continue;
                if (AutoLegal) { ApplyPlusFlags(pk, false); pk.RefreshChecksum(); }
                Store(pk, box, slot);
                return J(new { ok = true, encounter = EncName(enc), cross = IsCrossGame(enc) });
            }
            return Err("Couldn't build a legal version at that level.\n" + (fixedReport != "" ? fixedReport : firstReport));
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
