using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PKHeX.Core;

namespace PkhexWeb;

// Save-wide editing for the Trainer window: trainer/adventure/date fields, items and the Pokédex.
// PKHeX's save classes differ a lot between games, so everything here is found by reflection and anything
// a game doesn't have is simply reported as unsupported. Every change is recorded for undo/redo.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;

    static void RecordSave(Action undo, Action redo) => Record(-2, -1, undo, redo);

    // Property or public field (some PKHeX types expose data as fields).
    static object? Mem(object o, string name)
    {
        try
        {
            var p = FindProp(o.GetType(), name);
            if (p is not null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            return o.GetType().GetField(name, Inst)?.GetValue(o);
        }
        catch { return null; }
    }

    static bool SetMem(object o, string name, object value)
    {
        try
        {
            var p = FindProp(o.GetType(), name);
            if (p is not null && p.SetMethod is { IsPublic: true })
            {
                p.SetValue(o, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture));
                return true;
            }
            var f = o.GetType().GetField(name, Inst);
            if (f is not null && !f.IsInitOnly)
            {
                f.SetValue(o, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
                return true;
            }
        }
        catch { }
        return false;
    }

    // ---------- save fields (trainer, money, adventure, dates, ...) ----------

    static bool SaveLeaf(Type t) => Simple(t) || t == typeof(DateTime) || t == typeof(DateOnly);

    static readonly HashSet<string> SaveSkip = ["Edited", "BlankPKM", "Metadata", "Personal", "PersonalInfo", "State", "Provider"];

    static string? FmtSaveValue(object? v) => v switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    static (string Min, string Max)? TypeRange(Type t) => Type.GetTypeCode(t) switch
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

    // Numeric range of a save field: the storage type's range, tightened by the save's own MaxXxx property when it has one (MaxMoney, ...).
    static (string Min, string Max)? SaveRange(object owner, PropertyInfo p)
    {
        var t = p.PropertyType;
        if (t.IsEnum || t == typeof(bool) || t == typeof(string) || t == typeof(DateTime) || t == typeof(DateOnly)) return null;
        var rg = TypeRange(t);
        if (rg is null) return null;
        if (Mem(owner, "Max" + p.Name) is { } mx && Type.GetTypeCode(mx.GetType()) is >= TypeCode.SByte and <= TypeCode.UInt64)
        {
            var cap = Convert.ToDecimal(mx, CultureInfo.InvariantCulture);
            if (cap >= decimal.Parse(rg.Value.Min, CultureInfo.InvariantCulture) && cap < decimal.Parse(rg.Value.Max, CultureInfo.InvariantCulture))
                return (rg.Value.Min, cap.ToString(CultureInfo.InvariantCulture));
        }
        return rg;
    }

    [JSExport]
    public static string GetSaveProps()
    {
        try
        {
            if (Sav is null) return NoSave();
            var list = new List<object>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            void Walk(object o, string path, int depth)
            {
                if (seen.Count > 300 || !seen.Add(o)) return;
                var ty = o.GetType();
                foreach (var name in ty.GetProperties(Inst).Select(x => x.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (SaveSkip.Contains(name)) continue;
                    var p = FindProp(ty, name);
                    if (p is null || p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true }) continue;
                    var pt = p.PropertyType; var full = path == "" ? name : path + "." + name;
                    if (SaveLeaf(pt))
                    {
                        if (p.SetMethod is not { IsPublic: true }) continue;
                        object? v; try { v = p.GetValue(o); } catch { continue; }
                        var rg = SaveRange(o, p);
                        list.Add(new
                        {
                            path = full, name, type = pt.Name, value = FmtSaveValue(v),
                            options = pt.IsEnum ? Enum.GetNames(pt) : null, min = rg?.Min, max = rg?.Max,
                        });
                    }
                    else if (depth < 3 && !pt.IsValueType && !typeof(System.Collections.IEnumerable).IsAssignableFrom(pt))
                    {
                        object? child; try { child = p.GetValue(o); } catch { continue; }
                        if (child is null) continue;
                        var ct = child.GetType();
                        if (!(ct.Namespace ?? "").StartsWith("PKHeX.Core") || child is PKM || child is SaveFile) continue;
                        Walk(child, full, depth + 1);
                    }
                }
            }

            Walk(Sav, "", 0);
            return J(new { ok = true, props = list });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static (object? Owner, PropertyInfo? Prop) ResolveSaveProp(string path)
    {
        object? cur = Sav;
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1 && cur is not null; i++) cur = Mem(cur, parts[i]);
        return cur is null ? (null, null) : (cur, FindProp(cur.GetType(), parts[^1]));
    }

    static object ParseSaveValue(Type t, string value)
    {
        if (t.IsEnum) return Enum.Parse(t, value);
        if (t == typeof(bool)) return bool.Parse(value);
        if (t == typeof(string)) return value;
        if (t == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture);
        if (t == typeof(DateOnly)) return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }

    static void SetSavePath(string path, object? v)
    {
        var (owner, p) = ResolveSaveProp(path);
        if (owner is not null && p is not null) p.SetValue(owner, v);
    }

    [JSExport]
    public static string SetSaveProp(string path, string value)
    {
        try
        {
            if (Sav is null) return NoSave();
            var (owner, p) = ResolveSaveProp(path);
            if (owner is null || p is null || !SaveLeaf(p.PropertyType) || p.SetMethod is not { IsPublic: true }) return Err("Unknown field.");
            var t = p.PropertyType;
            if (SaveRange(owner, p) is { } r)
            {
                if (!decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                    return Err($"{Nice(p.Name)} must be a whole number.");
                if (num < decimal.Parse(r.Min, CultureInfo.InvariantCulture) || num > decimal.Parse(r.Max, CultureInfo.InvariantCulture))
                    return Err($"{Nice(p.Name)} must be between {r.Min} and {r.Max}.");
            }
            object nv;
            try { nv = ParseSaveValue(t, value); }
            catch { return Err($"'{value}' isn't a valid value for {Nice(p.Name)}."); }
            var old = p.GetValue(owner);
            p.SetValue(owner, nv);
            var now = p.GetValue(owner);   // a setter may clamp or adjust the value
            if (!Equals(old, now)) RecordSave(() => SetSavePath(path, old), () => SetSavePath(path, now));
            return J(new { ok = true, value = FmtSaveValue(now) });
        }
        catch (Exception e) { return Err(e.InnerException?.Message ?? e.Message); }
    }

    // ---------- items ----------

    sealed class Inv
    {
        public PropertyInfo Prop = null!;
        public object Value = null!;
        public List<object> Pouches = [];
    }

    // PKHeX builds the pouches fresh from the save on every read; changes are written back by setting Inventory again.
    static Inv? LoadInv()
    {
        var p = FindProp(Sav!.GetType(), "Inventory");
        if (p is null || p.GetIndexParameters().Length > 0) return null;
        object? v; try { v = p.GetValue(Sav); } catch { return null; }
        if (v is not System.Collections.IEnumerable e) return null;
        var list = e.Cast<object?>().Where(x => x is not null).Select(x => x!).ToList();
        return list.Count == 0 ? null : new Inv { Prop = p, Value = v, Pouches = list };
    }

    static void CommitInv(Inv inv) { if (inv.Prop.SetMethod is { IsPublic: true }) inv.Prop.SetValue(Sav, inv.Value); }

    static Array? PouchItems(object pouch) => Mem(pouch, "Items") as Array;
    static int Cap(object pouch) { var m = SafeInt(Mem(pouch, "MaxCount")); return m > 0 ? m : 999; }
    static string PouchName(object pouch) => Nice(Mem(pouch, "Type")?.ToString() ?? "Items");

    static int[]? LegalIds(object pouch)
    {
        var v = Mem(pouch, "LegalItems");
        // Some PKHeX builds expose the list as ReadOnlyMemory<ushort> instead of an array.
        if (v is not null && v is not System.Collections.IEnumerable)
            v = v.GetType().GetMethod("ToArray", Type.EmptyTypes)?.Invoke(v, null);
        if (v is System.Collections.IEnumerable e and not string)
        {
            var r = e.Cast<object?>().Select(SafeInt).Where(x => x > 0).Distinct().ToArray();
            return r.Length > 0 ? r : null;
        }
        return null;
    }

    // Item numbers differ between generations, so names have to come from the save's own game rather than
    // the default (newest) list, otherwise older games show the wrong names.
    static string[] SaveItemNames()
    {
        try
        {
            if (Sav is not null)
            {
                var strings = GameInfo.Strings;
                var ctx = Mem(Sav, "Context"); var ver = Mem(Sav, "Version"); var gen = Mem(Sav, "Generation");
                foreach (var m in strings.GetType().GetMethods(Inst | BindingFlags.Static)
                             .Where(m => m.Name == "GetItemStrings").OrderBy(m => m.GetParameters().Length))
                {
                    try
                    {
                        var ps = m.GetParameters();
                        var args = new object?[ps.Length];
                        var usable = true;
                        for (int i = 0; i < ps.Length && usable; i++)
                        {
                            var t = ps[i].ParameterType;
                            if (ctx is not null && t == ctx.GetType()) args[i] = ctx;
                            else if (ver is not null && t == ver.GetType()) args[i] = ver;
                            else if (t == typeof(bool)) args[i] = false;
                            else if (gen is not null && NumericParam(t)) args[i] = Convert.ChangeType(gen, t, CultureInfo.InvariantCulture);
                            else usable = false;
                        }
                        if (!usable) continue;
                        if (m.Invoke(strings, args) is System.Collections.IEnumerable list and not string)
                        {
                            var names = list.Cast<object?>().Select(x => x?.ToString() ?? "").ToArray();
                            if (names.Length > 0) return names;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return NameList("Item", "itemlist");
    }

    [JSExport]
    public static string GetItemNames() => J(SaveItemNames());

    static (int Index, int Count) ReadItem(object? el) => el is null ? (0, 0) : (Math.Max(0, SafeInt(Mem(el, "Index"))), Math.Max(0, SafeInt(Mem(el, "Count"))));

    // Items may be classes or structs, so each one is read, changed and written back into the array.
    static void WriteItem(Array arr, int slot, int index, int count)
    {
        var el = arr.GetValue(slot);
        if (el is null) return;
        SetMem(el, "Index", index); SetMem(el, "Count", count);
        arr.SetValue(el, slot);
    }

    static (int Index, int Count)[] Snap(Array arr)
    {
        var r = new (int, int)[arr.Length];
        for (int i = 0; i < arr.Length; i++) r[i] = ReadItem(arr.GetValue(i));
        return r;
    }

    // Moves filled slots to the front (keeping their order); the game expects no gaps.
    static void Compact(Array arr)
    {
        var full = new List<object>(); var empty = new List<object>();
        for (int i = 0; i < arr.Length; i++)
            if (arr.GetValue(i) is { } el) (ReadItem(el).Index > 0 ? full : empty).Add(el);
        int k = 0;
        foreach (var el in full.Concat(empty)) arr.SetValue(el, k++);
    }

    static void RestorePouch(int pi, (int Index, int Count)[] snap)
    {
        var inv = LoadInv();
        if (inv is null || pi >= inv.Pouches.Count || PouchItems(inv.Pouches[pi]) is not { } arr) return;
        for (int i = 0; i < arr.Length && i < snap.Length; i++) WriteItem(arr, i, snap[i].Index, snap[i].Count);
        CommitInv(inv);
    }

    [JSExport]
    public static string GetInventory()
    {
        try
        {
            if (Sav is null) return NoSave();
            var inv = LoadInv();
            if (inv is null) return J(new { ok = true, supported = false });
            var pouches = inv.Pouches.Select((p, i) =>
            {
                var arr = PouchItems(p);
                var items = new List<object>();
                if (arr is not null)
                    for (int s = 0; s < arr.Length; s++)
                    {
                        var (ix, ct) = ReadItem(arr.GetValue(s));
                        if (ix > 0) items.Add(new { slot = s, item = ix, count = ct });
                    }
                return new { index = i, name = PouchName(p), type = Mem(p, "Type")?.ToString() ?? "", max = Cap(p), size = arr?.Length ?? 0, legal = LegalIds(p), items, editable = arr is not null };
            }).ToList();
            return J(new { ok = true, supported = true, pouches });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Runs one change on a pouch, tidies it, writes it back and records it for undo. `edit` returns an error message or null.
    static string InvEdit(int pi, Func<object, Array, int, string?> edit)
    {
        try
        {
            if (Sav is null) return NoSave();
            var inv = LoadInv();
            if (inv is null || pi < 0 || pi >= inv.Pouches.Count) return Err("Items aren't available for this save.");
            if (inv.Prop.SetMethod is not { IsPublic: true }) return Err("This game's items can't be written here.");
            var pouch = inv.Pouches[pi];
            if (PouchItems(pouch) is not { } arr) return Err("Can't read this pouch.");
            var before = Snap(arr);
            var msg = edit(pouch, arr, Cap(pouch));
            if (msg is not null) return Err(msg);
            Compact(arr);
            var after = Snap(arr);
            CommitInv(inv);
            if (!before.SequenceEqual(after)) RecordSave(() => RestorePouch(pi, before), () => RestorePouch(pi, after));
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static int FindSlot(Array arr, Func<int, bool> match)
    {
        for (int i = 0; i < arr.Length; i++) if (match(ReadItem(arr.GetValue(i)).Index)) return i;
        return -1;
    }

    // Adds `count` of an item: tops up an existing stack, otherwise uses the first free slot. Returns false if there is no room.
    static bool GiveItem(Array arr, int cap, int item, int count)
    {
        int at = FindSlot(arr, x => x == item);
        if (at >= 0) { WriteItem(arr, at, item, Math.Min(cap, ReadItem(arr.GetValue(at)).Count + count)); return true; }
        at = FindSlot(arr, x => x == 0);
        if (at < 0) return false;
        WriteItem(arr, at, item, Math.Clamp(count, 1, cap));
        return true;
    }

    [JSExport]
    public static string InvSetCount(int pouch, int slot, int count) => InvEdit(pouch, (p, arr, cap) =>
    {
        if (slot < 0 || slot >= arr.Length) return "No such slot.";
        var (ix, _) = ReadItem(arr.GetValue(slot));
        if (ix == 0) return "That slot is empty.";
        count = Math.Clamp(count, 0, cap);
        WriteItem(arr, slot, count == 0 ? 0 : ix, count);
        return null;
    });

    [JSExport]
    public static string InvRemove(int pouch, int slot) => InvEdit(pouch, (p, arr, cap) =>
    {
        if (slot < 0 || slot >= arr.Length) return "No such slot.";
        WriteItem(arr, slot, 0, 0);
        return null;
    });

    [JSExport]
    public static string InvAdd(int pouch, int item, int count) => InvEdit(pouch, (p, arr, cap) =>
    {
        if (item <= 0) return "Choose an item.";
        var legal = LegalIds(p);
        if (legal is not null && !legal.Contains(item)) return "That item can't go in this pouch.";
        return GiveItem(arr, cap, item, Math.Max(1, count)) ? null : "This pouch is full.";
    });

    // Sets how many of one item the pouch holds, by item number: adds it if missing, removes it at 0.
    [JSExport]
    public static string InvSetItem(int pouch, int item, int count) => InvEdit(pouch, (p, arr, cap) =>
    {
        if (item <= 0) return "Choose an item.";
        count = Math.Clamp(count, 0, cap);
        int at = FindSlot(arr, x => x == item);
        if (count == 0) { if (at >= 0) WriteItem(arr, at, 0, 0); return null; }
        if (at >= 0) { WriteItem(arr, at, item, count); return null; }
        var legal = LegalIds(p);
        if (legal is not null && !legal.Contains(item)) return "That item can't go in this pouch.";
        return GiveItem(arr, cap, item, count) ? null : "This pouch is full.";
    });

    [JSExport]
    public static string InvMax(int pouch) => InvEdit(pouch, (p, arr, cap) =>
    {
        for (int i = 0; i < arr.Length; i++)
        {
            var (ix, _) = ReadItem(arr.GetValue(i));
            if (ix > 0) WriteItem(arr, i, ix, cap);
        }
        return null;
    });

    [JSExport]
    public static string InvAddAllLegal(int pouch) => InvEdit(pouch, (p, arr, cap) =>
    {
        var legal = LegalIds(p);
        if (legal is null) return "No item list is available for this pouch.";
        foreach (var id in legal.OrderBy(x => x))
            if (!GiveItem(arr, cap, id, Math.Min(cap, 99))) break;
        return null;
    });

    [JSExport]
    public static string InvClear(int pouch) => InvEdit(pouch, (p, arr, cap) =>
    {
        for (int i = 0; i < arr.Length; i++) WriteItem(arr, i, 0, 0);
        return null;
    });

    // ---------- Pokédex ----------

    sealed record DexApi(object Owner, MethodInfo GetSeen, MethodInfo GetCaught, MethodInfo? SetSeen, MethodInfo? SetCaught);

    static bool NumericParam(Type t) => !t.IsEnum && Type.GetTypeCode(t) is >= TypeCode.SByte and <= TypeCode.UInt64;

    static MethodInfo? DexMethod(object o, string name, int nParams)
    {
        foreach (var m in o.GetType().GetMethods(Inst))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length != nParams || !NumericParam(ps[0].ParameterType)) continue;
            if (nParams == 1 && m.ReturnType != typeof(bool)) continue;
            if (nParams == 2 && ps[1].ParameterType != typeof(bool)) continue;
            return m;
        }
        return null;
    }

    // The Pokédex API lives on the save itself in some versions and on a Dex/Zukan object in others.
    static DexApi? FindDex()
    {
        if (Sav is null) return null;
        foreach (var owner in new[] { Sav, Mem(Sav, "Dex"), Mem(Sav, "Zukan") })
        {
            if (owner is null) continue;
            var gs = DexMethod(owner, "GetSeen", 1); var gc = DexMethod(owner, "GetCaught", 1);
            if (gs is null || gc is null) continue;
            return new DexApi(owner, gs, gc, DexMethod(owner, "SetSeen", 2), DexMethod(owner, "SetCaught", 2));
        }
        return null;
    }

    static bool DexGet(DexApi d, MethodInfo m, int sp)
    {
        try { return m.Invoke(d.Owner, [Convert.ChangeType(sp, m.GetParameters()[0].ParameterType)]) is true; }
        catch { return false; }
    }

    static void DexSet(DexApi d, MethodInfo? m, int sp, bool v)
    {
        if (m is null) return;
        try { m.Invoke(d.Owner, [Convert.ChangeType(sp, m.GetParameters()[0].ParameterType), v]); }
        catch { }
    }

    // Caught always implies seen.
    static void DexState(DexApi d, int sp, bool seen, bool caught)
    {
        if (caught) { DexSet(d, d.SetSeen, sp, true); DexSet(d, d.SetCaught, sp, true); }
        else { DexSet(d, d.SetCaught, sp, false); DexSet(d, d.SetSeen, sp, seen); }
    }

    static int DexMax()
    {
        int m = SafeInt(Prop(Sav!, "MaxSpeciesID")), n = NameList("Species", "specieslist").Length - 1;
        return m > 0 ? Math.Min(m, n) : n;
    }

    [JSExport]
    public static string GetDex()
    {
        try
        {
            if (Sav is null) return NoSave();
            var d = FindDex();
            if (d is null || (d.SetSeen is null && d.SetCaught is null)) return J(new { ok = true, supported = false });
            int max = DexMax();
            var seen = new List<int>(); var caught = new List<int>();
            for (int i = 1; i <= max; i++)
            {
                if (DexGet(d, d.GetSeen, i)) seen.Add(i);
                if (DexGet(d, d.GetCaught, i)) caught.Add(i);
            }
            return J(new { ok = true, supported = true, max, seen, caught });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static void DexApply(IReadOnlyList<(int Sp, bool Seen, bool Caught)> states)
    {
        var d = FindDex();
        if (d is null) return;
        foreach (var (sp, s, c) in states) DexState(d, sp, s, c);
    }

    [JSExport]
    public static string SetDexEntry(int species, bool seen, bool caught)
    {
        try
        {
            if (Sav is null) return NoSave();
            var d = FindDex();
            if (d is null) return Err("Pokédex editing isn't available for this game.");
            if (species < 1 || species > DexMax()) return Err("Invalid species number.");
            var old = new[] { (species, DexGet(d, d.GetSeen, species), DexGet(d, d.GetCaught, species)) };
            var now = new[] { (species, seen || caught, caught) };
            DexApply(now);
            RecordSave(() => DexApply(old), () => DexApply(now));
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string SetDexAll(bool seen, bool caught)
    {
        try
        {
            if (Sav is null) return NoSave();
            var d = FindDex();
            if (d is null) return Err("Pokédex editing isn't available for this game.");
            var names = NameList("Species", "specieslist");
            var ids = Enumerable.Range(1, DexMax()).Where(i => !string.IsNullOrWhiteSpace(names.ElementAtOrDefault(i))).ToList();
            var old = ids.Select(i => (i, DexGet(d, d.GetSeen, i), DexGet(d, d.GetCaught, i))).ToList();
            var now = ids.Select(i => (i, seen || caught, caught)).ToList();
            DexApply(now);
            RecordSave(() => DexApply(old), () => DexApply(now));
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }
}
