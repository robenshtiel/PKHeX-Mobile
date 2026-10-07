using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PKHeX.Core;

namespace PkhexWeb;

// Importing a Pokémon file whose format is OLDER than the save's (a PK3 into Sun, say): convert it up the way the games'
// transfers do, then auto-legalise it. Never converts backwards. Same partial class as Engine.cs / EngineBatch.cs.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    // Like ImportPokemon, but converts older formats. Replaces it for single-file import.
    [JSExport]
    public static string ImportPokemonAny(byte[] data, int box, int slot)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0 || box >= Sav.BoxCount || slot < 0 || slot >= Sav.BoxSlotCount) return Err("Invalid box slot.");
            var pk = EntityFormat.GetFromBytes(data);
            if (pk is null) return Err("Not a recognized Pokémon file.");
            var (error, path, legal, note) = PlaceImported(pk, box, slot);
            return error is not null ? Err(error) : J(new { ok = true, converted = path, legal, note });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Stores the Pokémon in the slot. If it isn't the save's format it is converted forward first, then legalised.
    // The whole thing is one undo step. Returns an error message, or the conversion path ("PK3 → PK7"), whether the
    // result is legal, and anything the legaliser had to drop.
    static (string? Error, string? Path, bool? Legal, string? Note) PlaceImported(PKM pk, int box, int slot)
    {
        var mark = UndoStack.Count;
        string? path = null;
        if (pk.GetType() != Sav!.BlankPKM.GetType())
        {
            var from = pk.GetType().Name;
            var converted = ConvertForward(pk, out var error);
            if (converted is null) return (error, null, null, null);
            pk = converted;
            path = $"{from} → {pk.GetType().Name}";
        }
        pk.RefreshChecksum();
        Store(pk, box, slot);
        if (path is null) return (null, null, null, null);

        string? note = null;
        try
        {
            using var doc = JsonDocument.Parse(AutoLegalise(box, slot));
            if (doc.RootElement.TryGetProperty("dropped", out var dropped) && dropped.ValueKind == JsonValueKind.Array && dropped.GetArrayLength() > 0)
                note = "Couldn't keep: " + string.Join(", ", dropped.EnumerateArray().Select(x => x.GetString())) + ".";
        }
        catch { }
        MergeUndoSince(mark, box, slot);
        var stored = Slot(box, slot);
        return (null, path, stored is null ? null : IsLegalCached(stored), note);
    }

    // Folds every undo entry added since `mark` into one.
    static void MergeUndoSince(int mark, int box, int slot)
    {
        var n = UndoStack.Count - mark;
        if (mark < 0 || n < 2) return;
        var edits = UndoStack.GetRange(mark, n);
        UndoStack.RemoveRange(mark, n);
        UndoStack.Add(new HistEdit(box, slot,
            () => { for (int i = edits.Count - 1; i >= 0; i--) edits[i].Undo(); },
            () => { foreach (var e in edits) e.Redo(); }));
    }

    static int FormatOf(PKM pk) => Convert.ToInt32(Prop(pk, "Format") ?? 0);

    // Converts to the save's format, one generation at a time if PKHeX can't do it in one go. Forward only.
    static PKM? ConvertForward(PKM pk, out string? error)
    {
        error = null;
        var target = Sav!.BlankPKM.GetType();
        if (pk.GetType() == target) return pk;
        int from = FormatOf(pk), to = FormatOf(Sav.BlankPKM);
        if (from > to) { error = $"Can't convert backwards ({pk.GetType().Name} to {target.Name})."; return null; }

        TryUpdateConverterConfig(pk);
        var direct = ConvertStep(pk, target, out var why);
        if (direct is not null) return direct;
        if (from == to) { error = $"No conversion from {pk.GetType().Name} to {target.Name}: {why}"; return null; }

        var asm = typeof(SaveFile).Assembly;
        var cur = pk;
        for (int g = from + 1; g <= to; g++)
        {
            var next = g == to ? target : asm.GetType($"PKHeX.Core.PK{g}");
            if (next is null) { error = $"No converter for generation {g}."; return null; }
            var step = ConvertStep(cur, next, out var stepWhy);
            if (step is null) { error = $"Couldn't convert {cur.GetType().Name} to {next.Name}: {stepWhy}"; return null; }
            cur = step;
        }
        return cur;
    }

    // PKHeX's converter entry point has been EntityConverter.ConvertToType (result enum) and PKMConverter.ConvertToType (message).
    static PKM? ConvertStep(PKM pk, Type dest, out string why)
    {
        why = "this PKHeX version has no converter";
        var asm = typeof(SaveFile).Assembly;
        foreach (var typeName in new[] { "PKHeX.Core.EntityConverter", "PKHeX.Core.PKMConverter" })
        {
            var t = asm.GetType(typeName);
            if (t is null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "ConvertToType") continue;
                var ps = m.GetParameters();
                if (ps.Length != 3 || !ps[0].ParameterType.IsInstanceOfType(pk) || ps[1].ParameterType != typeof(Type) || !ps[2].IsOut) continue;
                var args = new object?[] { pk, dest, null };
                try
                {
                    if (m.Invoke(null, args) is PKM made && made.GetType() == dest) return made;
                    why = System.Text.RegularExpressions.Regex.Replace(args[2]?.ToString() ?? "no route", "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
                }
                catch (TargetInvocationException e) { why = e.InnerException?.Message ?? e.Message; }
                return null;
            }
        }
        return null;
    }

    // The converter fills in handler/trainer details for the new format from a shared config; point it at this save's
    // trainer so converted Pokémon look transferred by the save's owner. Best effort: the signature changes between versions.
    static void TryUpdateConverterConfig(PKM pk)
    {
        var asm = typeof(SaveFile).Assembly;
        foreach (var typeName in new[] { "PKHeX.Core.EntityConverter", "PKHeX.Core.PKMConverter" })
        {
            var t = asm.GetType(typeName);
            if (t is null) continue;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "UpdateConfig") continue;
                var ps = m.GetParameters();
                try
                {
                    if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(Sav!)) { m.Invoke(null, [Sav]); return; }
                    if (ps.Length == 3 && ps[2].ParameterType.IsInstanceOfType(Sav!) && ps[0].ParameterType == ps[1].ParameterType
                        && Prop(pk, "Version") is { } v && ps[0].ParameterType.IsInstanceOfType(v) && Prop(Sav!, "Version") is { } dv && ps[1].ParameterType.IsInstanceOfType(dv))
                    { m.Invoke(null, [v, dv, Sav]); return; }
                }
                catch { }
            }
        }
    }
}
