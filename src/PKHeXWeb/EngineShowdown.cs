using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using PKHeX.Core;

namespace PkhexWeb;

// Showdown set import/export for a single Pokémon. Same partial class as Engine.cs / EngineBatch.cs.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    [JSExport]
    public static string ExportShowdown(int box, int slot)
    {
        try
        {
            var pk = Slot(box, slot);
            if (pk is null || pk.Species == 0) return Err("No Pokémon in that slot.");
            // The exporter has lived on ShowdownParsing and on ShowdownSet in different PKHeX versions, so look it up.
            var asm = typeof(SaveFile).Assembly;
            foreach (var typeName in new[] { "PKHeX.Core.ShowdownParsing", "PKHeX.Core.ShowdownSet" })
            {
                var t = asm.GetType(typeName);
                if (t is null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetShowdownText" || m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || !ps[0].ParameterType.IsInstanceOfType(pk) || ps.Skip(1).Any(p => !p.HasDefaultValue)) continue;
                    var args = ps.Select((p, i) => i == 0 ? (object?)pk : p.DefaultValue).ToArray();
                    if (m.Invoke(null, args) is string text) return J(new { ok = true, text });
                }
            }
            return Err("This PKHeX version has no Showdown export method.");
        }
        catch (Exception e) { return Err((e as TargetInvocationException)?.InnerException?.Message ?? e.Message); }
    }

    // Applies a pasted set to the Pokémon in the slot. An empty box slot first gets a legal Pokémon of that species and
    // level (same path as "Create Pokémon"), then the set is applied on top. Creating and applying is one undo step.
    [JSExport]
    public static string ImportShowdown(string text, int box, int slot)
    {
        var mark = UndoStack.Count;
        var created = false;
        try
        {
            if (Sav is null) return NoSave();
            if (string.IsNullOrWhiteSpace(text)) return Err("Paste a Showdown set first.");
            var pk = Slot(box, slot);
            if (pk is null) return Err("Invalid slot.");

            var set = new ShowdownSet(text);
            var species = Convert.ToInt32(Prop(set, "Species") ?? 0);
            if (species <= 0)
                return Err("Couldn't find a Pokémon in that text. The first line should start with the species (optionally nickname, gender and @ item).");
            var skipped = (Prop(set, "InvalidLines") as System.Collections.IEnumerable)?.Cast<object>()
                .Select(o => o?.ToString() ?? "").Where(s => s != "").ToList() ?? [];

            if (pk.Species == 0)
            {
                if (box < 0) return Err("Adding to the party isn't supported yet. Use a box.");
                var level = Math.Clamp(Convert.ToInt32(Prop(set, "Level") ?? 50), 1, 100);
                using var made = JsonDocument.Parse(CreatePokemon(box, slot, species, level, -1, Prop(set, "Shiny") is true, false));
                if (!made.RootElement.GetProperty("ok").GetBoolean())
                    return Err(made.RootElement.TryGetProperty("error", out var em) && em.GetString() is { } msg ? msg : "Couldn't create that Pokémon.");
                created = true;
                pk = Slot(box, slot)!;
            }

            ApplySet(pk, set);
            if (AutoLegal) ApplyPlusFlags(pk, false);
            if (AutoRelearn) FillSuggestedRelearn(pk);
            pk.RefreshChecksum();
            Store(pk, box, slot);

            if (created && UndoStack.Count >= mark + 2)
            {
                var a = UndoStack[^2]; var b = UndoStack[^1];
                UndoStack.RemoveRange(UndoStack.Count - 2, 2);
                UndoStack.Add(new HistEdit(box, slot, () => { b.Undo(); a.Undo(); }, () => { a.Redo(); b.Redo(); }));
            }
            return J(new { ok = true, created, skipped });
        }
        catch (Exception e)
        {
            if (created && UndoStack.Count > mark) HistStep(UndoStack, RedoStack, true);   // don't leave a half-built Pokémon behind
            return Err((e as TargetInvocationException)?.InnerException?.Message ?? e.Message);
        }
    }

    // PKHeX's ApplySetDetails(pk, set) is an extension method; find it at runtime like the other version-sensitive calls.
    static void ApplySet(PKM pk, object set)
    {
        foreach (var t in typeof(SaveFile).Assembly.GetTypes())
        {
            if (!t.IsAbstract || !t.IsSealed) continue;   // static classes, where extension methods live
            foreach (var found in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (found.Name != "ApplySetDetails" || found.GetParameters().Length != 2) continue;
                MethodInfo m;
                try { m = found.IsGenericMethodDefinition ? found.MakeGenericMethod(pk.GetType()) : found; } catch { continue; }
                var ps = m.GetParameters();
                if (!ps[0].ParameterType.IsInstanceOfType(pk) || !ps[1].ParameterType.IsInstanceOfType(set)) continue;
                m.Invoke(null, [pk, set]);
                return;
            }
        }
        throw new InvalidOperationException("This PKHeX version has no ApplySetDetails, so sets can't be applied.");
    }
}
