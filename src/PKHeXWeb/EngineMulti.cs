using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PKHeX.Core;

namespace PkhexWeb;

// Importing many Pokémon files in one go. JS calls MultiImportBegin, then MultiImportNext once per file, then MultiImportEnd.
// Files go into the first EMPTY slot at or after the start box (rolling over to the next box); nothing is ever overwritten.
// Older-format files are converted forward and legalised on the way in.
// Same partial class as Engine.cs / EngineBatch.cs.
[SupportedOSPlatform("browser")]
public static partial class Engine
{
    static readonly List<HistEdit> MultiEdits = new();

    [JSExport]
    public static string MultiImportBegin()
    {
        MultiEdits.Clear();
        return Sav is null ? NoSave() : J(new { ok = true });
    }

    [JSExport]
    public static string MultiImportNext(byte[] data, int startBox)
    {
        try
        {
            if (Sav is null) return NoSave();
            var pk = EntityFormat.GetFromBytes(data);
            if (pk is null) return Err("Not a recognized Pokémon file.");
            for (int b = Math.Max(0, startBox); b < Sav.BoxCount; b++)
            {
                for (int s = 0; s < Sav.BoxSlotCount; s++)
                {
                    if (Sav.GetBoxSlotAtIndex(b, s).Species != 0) continue;
                    // Older formats are converted forward and legalised (see EngineConvert.cs); a file that can't be converted uses no slot.
                    var (error, path, legal, _) = PlaceImported(pk, b, s);
                    if (error is not null) return Err(error);
                    if (UndoStack.Count > 0) MultiEdits.Add(UndoStack[^1]);
                    return J(new { ok = true, box = b, slot = s, converted = path, legal });
                }
            }
            return J(new { ok = false, full = true, error = "No empty slots left." });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // Folds every slot written since MultiImportBegin into a single undo step.
    [JSExport]
    public static string MultiImportEnd()
    {
        var edits = MultiEdits.Where(e => UndoStack.Any(u => ReferenceEquals(u, e))).ToList();
        MultiEdits.Clear();
        if (edits.Count >= 2)
        {
            UndoStack.RemoveAll(u => edits.Any(e => ReferenceEquals(u, e)));
            var first = edits[0];
            UndoStack.Add(new HistEdit(first.Box, first.Slot,
                () => { for (int i = edits.Count - 1; i >= 0; i--) edits[i].Undo(); },
                () => { foreach (var e in edits) e.Redo(); }));
        }
        return J(new { ok = true });
    }
}
