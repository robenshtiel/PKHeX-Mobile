using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PKHeX.Core;

namespace PkhexWeb;

// Toolbar actions: copy / delete the selected Pokémon, and sort every box.
// Same partial class as Engine.cs / EngineBatch.cs.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    // Copies the Pokémon into the next empty slot after it (rolling over to the following boxes).
    // A party Pokémon goes into the first empty box slot.
    [JSExport]
    public static string CopySlot(int box, int slot)
    {
        try
        {
            if (Sav is null) return NoSave();
            var pk = Slot(box, slot);
            if (pk is null || pk.Species == 0) return Err("Select a Pokémon to copy first.");
            int per = Sav.BoxSlotCount, total = Sav.BoxCount * per;
            for (int i = box < 0 ? 0 : box * per + slot + 1; i < total; i++)
            {
                int b = i / per, s = i % per;
                if (Sav.GetBoxSlotAtIndex(b, s).Species != 0) continue;
                Store(pk.Clone(), b, s);
                return J(new { ok = true, box = b, slot = s });
            }
            return Err(box < 0 ? "No empty box slots." : "No empty slot after this one.");
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string DeleteSlot(int box, int slot)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (box < 0) return Err("Deleting from the party isn't supported yet.");
            var pk = Slot(box, slot);
            if (pk is null || pk.Species == 0) return Err("That slot is already empty.");
            Store(Sav.BlankPKM.Clone(), box, slot);
            return J(new { ok = true });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // ---------- sort all boxes ----------
    // JS calls SortBegin, then SortStep until done (legality sorts only: it fills the legality cache in small chunks so the
    // page can repaint), then SortFinish, which re-reads the boxes, sorts them and writes them back as ONE undo step.

    static readonly string[] SortModes = ["num-asc", "num-desc", "alpha-asc", "alpha-desc", "lvl-asc", "lvl-desc", "legal", "illegal"];
    static List<PKM>? SortWarm;
    static int SortCursor;

    [JSExport]
    public static string SortBegin(string mode)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (!SortModes.Contains(mode)) return Err("Unknown sort order.");
            SortWarm = null; SortCursor = 0;
            if (mode is not ("legal" or "illegal")) return J(new { ok = true, total = 0 });
            var list = new List<PKM>();
            int per = Sav.BoxSlotCount;
            for (int i = 0; i < Sav.BoxCount * per; i++)
            {
                var pk = Sav.GetBoxSlotAtIndex(i / per, i % per);
                if (pk.Species != 0) list.Add(pk);
            }
            SortWarm = list;
            return J(new { ok = true, total = list.Count });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string SortStep(int count)
    {
        try
        {
            if (SortWarm is null) return J(new { ok = true, done = 0 });
            var end = Math.Min(SortWarm.Count, SortCursor + Math.Max(1, count));
            for (; SortCursor < end; SortCursor++) IsLegalCached(SortWarm[SortCursor]);
            return J(new { ok = true, done = SortCursor });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string SortFinish(string mode)
    {
        try
        {
            if (Sav is null) return NoSave();
            if (!SortModes.Contains(mode)) return Err("Unknown sort order.");
            SortWarm = null;
            int per = Sav.BoxSlotCount, total = Sav.BoxCount * per;
            var before = new PKM[total];
            var items = new List<PKM>();
            for (int i = 0; i < total; i++)
            {
                before[i] = Sav.GetBoxSlotAtIndex(i / per, i % per);
                if (before[i].Species != 0) items.Add(before[i]);
            }
            var sorted = SortOrder(items, mode);
            var after = new PKM[total];
            for (int i = 0; i < total; i++) after[i] = i < sorted.Count ? sorted[i] : Sav.BlankPKM.Clone();

            bool same = true;
            for (int i = 0; i < total && same; i++) same = PkmBytes(before[i]).SequenceEqual(PkmBytes(after[i]));
            if (same) return J(new { ok = true, changed = false, count = items.Count });

            void Write(PKM[] set) { for (int i = 0; i < total; i++) Put(set[i].Clone(), i / per, i % per); }
            Write(after);
            Record(-2, 0, () => Write(before), () => Write(after));   // -2 = whole-save edit, so undo reloads the grid
            return J(new { ok = true, changed = true, count = items.Count });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    static List<PKM> SortOrder(List<PKM> items, string mode)
    {
        static int Form(PKM p) => Convert.ToInt32(Prop(p, "Form") ?? 0);
        static string Name(PKM p) => GameInfo.Strings.Species.ElementAtOrDefault(p.Species) ?? "";
        var cmp = StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<PKM> q = mode switch
        {
            "num-asc" => items.OrderBy(p => (int)p.Species).ThenBy(p => Form(p)),
            "num-desc" => items.OrderByDescending(p => (int)p.Species).ThenBy(p => Form(p)),
            "alpha-asc" => items.OrderBy(p => Name(p), cmp).ThenBy(p => (int)p.Species).ThenBy(p => Form(p)),
            "alpha-desc" => items.OrderByDescending(p => Name(p), cmp).ThenBy(p => (int)p.Species).ThenBy(p => Form(p)),
            "lvl-asc" => items.OrderBy(p => (int)p.CurrentLevel),
            "lvl-desc" => items.OrderByDescending(p => (int)p.CurrentLevel),
            "legal" => items.OrderByDescending(p => IsLegalCached(p)),
            "illegal" => items.OrderBy(p => IsLegalCached(p)),
            _ => throw new ArgumentException("Unknown sort order."),
        };
        return q.ToList();
    }
}
