using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.RegularExpressions;
using PKHeX.Core;

namespace PkhexWeb;

// Batch editor: PKHeX-style instruction scripts run over a box, the party or the whole save.
//
//   =Prop=Value   keep only Pokémon where Prop equals Value      (also  !  >  <  >=  <=  ≥  ≤)
//   .Prop=Value   set Prop to Value
//
// Values can be numbers, names (Pikachu, Thunderbolt, Adamant, Poké Ball ...), True/False, $rand,
// or +N -N *N /N on numeric fields. Moves/RelearnMoves/Ribbons/IVs/EVs are extra "virtual" fields.
//
// The JS side drives it in small steps so the page stays responsive:
//   BatchBegin(script, scope, box) -> BatchStep(n) ... -> BatchFinish(apply)   (BatchCancel() to abort)
// Nothing is written to the save until BatchFinish(true), and the whole batch is ONE undo step.
public static partial class Engine
{
    enum BatchCmp { Eq, Ne, Gt, Lt, Ge, Le }
    sealed record BatchFilter(int Line, string Prop, BatchCmp Cmp, string Value);
    sealed record BatchSet(int Line, string Prop, string Value);
    sealed record BatchChange(int Box, int Slot, PKM Before, PKM After);

    sealed class BatchJob
    {
        public SaveFile Save = null!;
        public List<BatchFilter> Filters = new();
        public List<BatchSet> Sets = new();
        public List<(int Box, int Slot)> Targets = new();
        public int Next, Scanned, Matched;
        public List<BatchChange> Changes = new();
        public Dictionary<string, (int Count, string At)> Errors = new();
        public Dictionary<string, int> Missing = new();
        public List<object> Samples = new();
        public Random Rng = new();
        public Dictionary<string, string[]> Lists = new();
        public Dictionary<string, Dictionary<string, List<Opt>>> OptCache = new();
    }

    static BatchJob? Job;

    // ---------- small helpers ----------

    // Case-, space- and punctuation-insensitive key, so "move 1", "Move1" and "MOVE1" match, as do "Farfetch'd" and "farfetchd".
    static string BatchNorm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '♀': sb.Append('f'); break;
                case '♂': sb.Append('m'); break;
                case 'é': case 'É': sb.Append('e'); break;
                default: if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch)); break;
            }
        }
        return sb.ToString();
    }

    static Exception BatchRoot(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException is not null) e = e.InnerException;
        return e;
    }

    static bool BatchParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw new FormatException($"“{s}” isn't True or False."),
    };

    static void BatchError(BatchJob job, string message, string at) =>
        job.Errors[message] = job.Errors.TryGetValue(message, out var e) ? (e.Count + 1, e.At) : (1, at);

    static void BatchMissing(BatchJob job, string prop) =>
        job.Missing[prop] = job.Missing.TryGetValue(prop, out var n) ? n + 1 : 1;

    static int BatchMax(string prop, int fallback) =>
        Sav is not null && Prop(Sav, prop) is { } v ? Convert.ToInt32(v) : fallback;

    // A readable (or writable) simple-typed property, found by its normalised name.
    static PropertyInfo? BatchFindProp(PKM pk, string name, bool writable)
    {
        var key = BatchNorm(name);
        var type = pk.GetType();
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && (!writable || p.CanWrite) && p.GetIndexParameters().Length == 0 && Simple(p.PropertyType) && BatchNorm(p.Name) == key)
            .OrderBy(p => p.DeclaringType == type ? 0 : 1)
            .FirstOrDefault();
    }

    // Name lists for the fields PKHeX stores as plain numbers.
    static string[]? BatchList(BatchJob job, string prop)
    {
        string? key =
            prop == "Species" ? "species" :
            (prop is "HeldItem" or "Item") ? "items" :
            prop == "Ability" ? "abilities" :
            (prop is "Nature" or "StatNature") ? "natures" :
            Regex.IsMatch(prop, "^(Move[1-4]|RelearnMove[1-4]|AlphaMove)$") ? "moves" : null;
        if (key is null) return null;
        if (!job.Lists.TryGetValue(key, out var list))
        {
            list = key switch
            {
                "species" => NameList("Species", "specieslist"),
                "items" => NameList("Item", "itemlist"),
                "abilities" => NameList("Ability", "abilitylist"),
                "natures" => NameList("Natures", "natures"),
                _ => NameList("Move", "movelist"),
            };
            job.Lists[key] = list;
        }
        return list.Length == 0 ? null : list;
    }

    // Ball / Language / Gender / Form / locations ..., cached per format+species+form+game.
    static Dictionary<string, List<Opt>> BatchOpts(BatchJob job, PKM pk)
    {
        var key = $"{pk.GetType().Name}|{pk.Species}|{pk.Form}|{pk.Version}";
        if (!job.OptCache.TryGetValue(key, out var d))
        {
            try { d = OptsFor(pk); } catch { d = new Dictionary<string, List<Opt>>(); }
            if (job.OptCache.Count > 200) job.OptCache.Clear();
            job.OptCache[key] = d;
        }
        return d;
    }

    // Name -> number for list-backed fields (exact name first, then the loose match).
    static int? BatchNameToId(BatchJob job, PKM pk, string prop, string raw)
    {
        var list = BatchList(job, prop);
        if (list is not null)
        {
            var i = Array.FindIndex(list, x => x.Length > 0 && string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { var k = BatchNorm(raw); if (k.Length > 0) i = Array.FindIndex(list, x => x.Length > 0 && BatchNorm(x) == k); }
            return i >= 0 ? i : null;
        }
        if (BatchOpts(job, pk).TryGetValue(prop, out var opts))
        {
            var k = BatchNorm(raw);
            var m = opts.FirstOrDefault(x => string.Equals(x.t, raw, StringComparison.OrdinalIgnoreCase)) ?? opts.FirstOrDefault(x => BatchNorm(x.t) == k);
            if (m is not null) return m.v;
        }
        return null;
    }

    // What the user typed -> the plain string ApplyProp understands (number, True/False or enum name).
    static string BatchResolve(BatchJob job, PKM pk, PropertyInfo p, string raw)
    {
        var t = p.PropertyType;
        raw = raw.Trim();
        if (t == typeof(bool)) return BatchParseBool(raw) ? "True" : "False";
        if (t == typeof(string)) return raw;
        if (t.IsEnum)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)) return Enum.ToObject(t, num).ToString()!;
            var key = BatchNorm(raw);
            return Enum.GetNames(t).FirstOrDefault(x => BatchNorm(x) == key)
                ?? throw new FormatException($"“{raw}” isn't a valid {Nice(p.Name)}.");
        }
        if (decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return raw;
        var id = BatchNameToId(job, pk, p.Name, raw);
        return id?.ToString(CultureInfo.InvariantCulture)
            ?? throw new FormatException($"“{raw}” isn't a known value for {Nice(p.Name)}.");
    }

    // Value as shown in the preview: names for list fields, plain text otherwise.
    static string BatchShow(BatchJob job, PKM pk, string name)
    {
        if (name == "IsShiny") return pk.IsShiny ? "True" : "False";
        var value = FindProp(pk.GetType(), name)?.GetValue(pk);
        if (value is null) return "";
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return text;
        var list = BatchList(job, name);
        if (list is not null) return id >= 0 && id < list.Length && list[id].Length > 0 ? list[id] : text;
        if (BatchOpts(job, pk).TryGetValue(name, out var opts) && opts.FirstOrDefault(x => x.v == id) is { } hit) return hit.t;
        return text;
    }

    static string BatchMoveList(BatchJob job, PKM pk, string prefix) =>
        string.Join(", ", Enumerable.Range(1, 4).Select(i => BatchShow(job, pk, prefix + i)).Where(x => x.Length > 0 && x != "0"));

    // +N -N *N /N on a numeric field. Null if the value isn't one of those.
    static string? BatchRelative(PKM pk, PropertyInfo p, string v)
    {
        if (v.Length < 2 || "+-*/".IndexOf(v[0]) < 0) return null;
        var code = Type.GetTypeCode(p.PropertyType);
        if ((int)code < (int)TypeCode.SByte || (int)code > (int)TypeCode.UInt64) return null;
        bool signed = code is TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64;
        if (v[0] == '-' && signed) return null;   // a plain negative number for a signed field
        if (!decimal.TryParse(v[1..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        var cur = Convert.ToDecimal(p.GetValue(pk), CultureInfo.InvariantCulture);
        decimal res = v[0] switch
        {
            '+' => cur + amount,
            '-' => cur - amount,
            '*' => cur * amount,
            _ => amount == 0 ? throw new DivideByZeroException("Can't divide by zero.") : cur / amount,
        };
        res = Math.Truncate(res);
        if (Range(pk, p) is { } r)
            res = Math.Clamp(res, decimal.Parse(r.Min, CultureInfo.InvariantCulture), decimal.Parse(r.Max, CultureInfo.InvariantCulture));
        return res.ToString(CultureInfo.InvariantCulture);
    }

    // $rand for one field.
    static string BatchRandom(BatchJob job, PKM pk, PropertyInfo p)
    {
        var t = p.PropertyType; var rng = job.Rng; var n = p.Name;
        if (t == typeof(bool)) return rng.Next(2) == 0 ? "False" : "True";
        if (t.IsEnum)
        {
            var names = Enum.GetNames(t).Where(x => x != "Random").Distinct().ToArray();
            return names[rng.Next(names.Length)];
        }
        if (t == typeof(string)) throw new FormatException($"{Nice(n)} can't be set to $rand.");
        if (n == "Gender") return Convert.ToInt32(p.GetValue(pk)) == 2 ? "2" : rng.Next(2).ToString(CultureInfo.InvariantCulture);
        var list = BatchList(job, n);
        if (list is not null)
        {
            int hi = list.Length - 1;
            if (n == "Species") hi = BatchMax("MaxSpeciesID", hi);
            else if (n is "HeldItem" or "Item") hi = BatchMax("MaxItemID", hi);
            else if (n == "Ability") hi = BatchMax("MaxAbilityID", hi);
            else if (n is "Nature" or "StatNature") hi = Math.Min(hi, 24);
            else hi = BatchMax("MaxMoveID", hi);
            hi = Math.Min(hi, list.Length - 1);
            int lo = (n is "Nature" or "StatNature") ? 0 : 1;
            var pool = Enumerable.Range(lo, Math.Max(hi - lo + 1, 0)).Where(i => list[i].Length > 0).ToList();
            if (pool.Count == 0) throw new FormatException($"No values to pick from for {Nice(n)}.");
            return pool[rng.Next(pool.Count)].ToString(CultureInfo.InvariantCulture);
        }
        if (BatchOpts(job, pk).TryGetValue(n, out var opts) && opts.Count > 0)
            return opts[rng.Next(opts.Count)].v.ToString(CultureInfo.InvariantCulture);
        var r = Range(pk, p) ?? throw new FormatException($"{Nice(n)} can't be set to $rand.");
        var rLo = (long)Math.Max(decimal.Parse(r.Min, CultureInfo.InvariantCulture), long.MinValue);
        var rHi = (long)Math.Min(decimal.Parse(r.Max, CultureInfo.InvariantCulture), long.MaxValue - 1);
        return rng.NextInt64(rLo, rHi + 1).ToString(CultureInfo.InvariantCulture);
    }

    // ---------- parsing ----------

    // Fills job.Filters / job.Sets. Returns the syntax errors (empty when the script is fine).
    static List<string> ParseBatch(string script, BatchJob job)
    {
        var errors = new List<string>();
        var lines = script.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int no = i + 1;
            string body; var cmp = BatchCmp.Eq; bool isSet = false;
            if (line.StartsWith(">=", StringComparison.Ordinal)) { cmp = BatchCmp.Ge; body = line[2..]; }
            else if (line.StartsWith("<=", StringComparison.Ordinal)) { cmp = BatchCmp.Le; body = line[2..]; }
            else switch (line[0])
            {
                case '.': isSet = true; body = line[1..]; break;
                case '=': body = line[1..]; break;
                case '!': cmp = BatchCmp.Ne; body = line[1..]; break;
                case '>': cmp = BatchCmp.Gt; body = line[1..]; break;
                case '<': cmp = BatchCmp.Lt; body = line[1..]; break;
                case '≥': cmp = BatchCmp.Ge; body = line[1..]; break;
                case '≤': cmp = BatchCmp.Le; body = line[1..]; break;
                default:
                    errors.Add($"Line {no}: start with “.” to set a value, or = ! > < >= <= to filter.");
                    continue;
            }
            int eq = body.IndexOf('=');
            if (eq <= 0 || body[..eq].Trim().Length == 0) { errors.Add($"Line {no}: expected Property=Value."); continue; }
            var prop = body[..eq].Trim();
            var value = body[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (isSet) job.Sets.Add(new BatchSet(no, prop, value));
            else job.Filters.Add(new BatchFilter(no, prop, cmp, value));
        }
        return errors;
    }

    static List<(int Box, int Slot)> BatchTargets(string scope, int box)
    {
        var sav = Sav!;
        var list = new List<(int Box, int Slot)>();
        void Party() { for (int i = 0; i < sav.PartyCount; i++) list.Add((-1, i)); }
        void Boxes(int first, int last) { for (int b = first; b < last; b++) for (int i = 0; i < sav.BoxSlotCount; i++) list.Add((b, i)); }
        switch (scope)
        {
            case "party": Party(); break;
            case "boxes": Boxes(0, sav.BoxCount); break;
            case "all": Party(); Boxes(0, sav.BoxCount); break;
            default:
                if (box < 0) Party(); else Boxes(box, box + 1);
                break;
        }
        return list;
    }

    // ---------- filters ----------

    // true = passes, false = doesn't, null = the field doesn't exist for this Pokémon's format.
    static bool? BatchMatch(BatchJob job, PKM pk, BatchFilter f)
    {
        string cur, want;
        if (BatchNorm(f.Prop) == "legal")
        {
            cur = IsLegalCached(pk) ? "True" : "False";
            want = BatchParseBool(f.Value) ? "True" : "False";
        }
        else
        {
            var p = BatchFindProp(pk, f.Prop, false);
            if (p is null) return null;
            cur = Convert.ToString(p.GetValue(pk), CultureInfo.InvariantCulture) ?? "";
            want = BatchResolve(job, pk, p, f.Value);
        }
        int c;
        if (decimal.TryParse(cur, NumberStyles.Number, CultureInfo.InvariantCulture, out var da) && decimal.TryParse(want, NumberStyles.Number, CultureInfo.InvariantCulture, out var db)) c = da.CompareTo(db);
        else if (f.Cmp is BatchCmp.Eq or BatchCmp.Ne) c = string.Equals(cur, want, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        else throw new FormatException($"{Nice(f.Prop)} isn't a number, so > and < can't be used on it.");
        return f.Cmp switch
        {
            BatchCmp.Eq => c == 0,
            BatchCmp.Ne => c != 0,
            BatchCmp.Gt => c > 0,
            BatchCmp.Lt => c < 0,
            BatchCmp.Ge => c >= 0,
            _ => c <= 0,
        };
    }

    // ---------- instructions ----------

    // Error = problem message; Missing = the field doesn't exist for this format (skipped, not an error);
    // Note = "Level: 5 → 100" for the preview, null when nothing changed.
    static (string? Error, bool Missing, string? Note) BatchApply(BatchJob job, PKM pk, BatchSet s)
    {
        var key = BatchNorm(s.Prop);
        var v = s.Value;
        var tok = v.StartsWith('$') ? v.ToLowerInvariant() : null;
        switch (key)
        {
            case "moves":
            {
                if (tok != "$suggest") return ("Moves only accepts $suggest (use Move1 to Move4 to set one particular move).", false, null);
                var was = BatchMoveList(job, pk, "Move");
                SuggestMoveset(pk);
                Call(pk, "HealPP");
                pk.RefreshChecksum();
                var now = BatchMoveList(job, pk, "Move");
                return (null, false, was == now ? null : $"Moves: {was} → {now}");
            }
            case "relearnmoves":
            {
                if (FindProp(pk.GetType(), "RelearnMove1") is null) return (null, true, null);
                if (tok != "$suggest") return ("RelearnMoves only accepts $suggest.", false, null);
                var was = BatchMoveList(job, pk, "RelearnMove");
                if (!FillSuggestedRelearn(pk)) return ("Couldn't work out the relearn moves for this Pokémon.", false, null);
                pk.RefreshChecksum();
                var now = BatchMoveList(job, pk, "RelearnMove");
                return (null, false, was == now ? null : $"Relearn moves: {was} → {now}");
            }
            case "ribbons":
            {
                var all = Editable(pk).Where(q => q.PropertyType == typeof(bool) && q.Name.StartsWith("Ribbon", StringComparison.Ordinal)).Select(q => q.Name).ToList();
                if (all.Count == 0) return (null, true, null);
                HashSet<string> on;
                if (tok == "$suggest") on = RibbonSets(pk).legal.ToHashSet();
                else if (tok == "$all") on = all.ToHashSet();
                else if (tok == "$none") on = new HashSet<string>();
                else return ("Ribbons accepts $suggest, $all or $none.", false, null);
                int before = all.Count(x => Prop(pk, x) is true);
                foreach (var name in all) TrySet(pk, [name], on.Contains(name));
                pk.RefreshChecksum();
                int after = all.Count(x => Prop(pk, x) is true);
                return (null, false, before == after ? null : $"Ribbons: {before} → {after}");
            }
            case "ivs":
            case "evs":
            {
                bool iv = key == "ivs";
                var label = iv ? "IVs" : "EVs";
                var pre = iv ? "IV_" : "EV_";
                var props = EvStats.Select(x => FindProp(pk.GetType(), pre + x)).ToArray();
                if (props.Any(x => x is null || !x.CanWrite)) return (null, true, null);
                int max = Lim(pk, iv ? "MaxIV" : "MaxEV", iv ? 31 : 252);
                var vals = new int[6];
                if (tok == "$rand")
                {
                    if (iv) { for (int k = 0; k < 6; k++) vals[k] = job.Rng.Next(max + 1); }
                    else
                    {
                        int left = max <= 255 ? MaxEvTotal : int.MaxValue;
                        foreach (var k in Enumerable.Range(0, 6).OrderBy(_ => job.Rng.Next()))
                        {
                            vals[k] = job.Rng.Next(Math.Min(max, left) + 1);
                            if (left != int.MaxValue) left -= vals[k];
                        }
                    }
                }
                else if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                {
                    if (amount < 0 || amount > max) return ($"{label} must be between 0 and {max}.", false, null);
                    if (!iv && max <= 255 && amount * 6 > MaxEvTotal) return ($"EVs can't total more than {MaxEvTotal} (that would be {amount * 6}).", false, null);
                    Array.Fill(vals, amount);
                }
                else return ($"{label} accepts a number (used for all six stats) or $rand.", false, null);
                string Show() => string.Join("/", props.Select(x => x!.GetValue(pk)));
                var was = Show();
                for (int k = 0; k < 6; k++) props[k]!.SetValue(pk, Convert.ChangeType(vals[k], props[k]!.PropertyType, CultureInfo.InvariantCulture));
                pk.RefreshChecksum();
                var now = Show();
                return (null, false, was == now ? null : $"{label}: {was} → {now}");
            }
            case "isshiny":
            {
                bool on = tok == "$rand" ? job.Rng.Next(2) == 0 : BatchParseBool(v);
                var was = pk.IsShiny;
                var bad = ApplyProp(pk, "IsShiny", on ? "True" : "False");
                if (bad is not null) return (bad, false, null);
                return (null, false, was == pk.IsShiny ? null : $"Shiny: {was} → {pk.IsShiny}");
            }
        }

        var prop = BatchFindProp(pk, s.Prop, true);
        if (prop is null) return (null, true, null);
        var pname = prop.Name;
        var old = BatchShow(job, pk, pname);
        string newVal;
        if (tok is not null)
        {
            if (tok != "$rand") return ($"Unknown value {v}. Only $rand works on {Nice(pname)}.", false, null);
            newVal = BatchRandom(job, pk, prop);
        }
        else if (BatchRelative(pk, prop, v) is { } rel) newVal = rel;
        else newVal = BatchResolve(job, pk, prop, v);
        var problem = ApplyProp(pk, pname, newVal);
        if (problem is not null) return (problem, false, null);
        var shown = BatchShow(job, pk, pname);
        return (null, false, old == shown ? null : $"{Nice(pname)}: {old} → {shown}");
    }

    // One Pokémon: filters, then every instruction on a copy. Changes are only collected, not written.
    static void BatchOne(BatchJob job, PKM src, int box, int slot)
    {
        var at = box < 0 ? $"Party {slot + 1}" : $"Box {box + 1}, slot {slot + 1}";
        foreach (var f in job.Filters)
        {
            bool? hit;
            try { hit = BatchMatch(job, src, f); }
            catch (Exception e) { BatchError(job, $"Line {f.Line}: {BatchRoot(e).Message}", at); return; }
            if (hit is null) { BatchMissing(job, f.Prop); return; }
            if (hit == false) return;
        }
        job.Matched++;
        var pk = src.Clone();
        var notes = new List<string>();
        foreach (var s in job.Sets)
        {
            try
            {
                var (error, missing, note) = BatchApply(job, pk, s);
                if (missing) BatchMissing(job, s.Prop);
                else if (error is not null) BatchError(job, $"Line {s.Line}: {error}", at);
                else if (note is not null) notes.Add(note);
            }
            catch (Exception e) { BatchError(job, $"Line {s.Line}: {BatchRoot(e).Message}", at); }
        }
        if (Enumerable.SequenceEqual(PkmBytes(pk), PkmBytes(src))) return;
        pk.RefreshChecksum();
        job.Changes.Add(new BatchChange(box, slot, src.Clone(), pk));
        if (job.Samples.Count < 30)
        {
            var name = GameInfo.Strings.Species.ElementAtOrDefault(src.Species) ?? ("#" + src.Species);
            job.Samples.Add(new { at, name, changes = notes });
        }
    }

    // ---------- JS entry points ----------

    // Property names for the "Add instruction" box in the UI.
    [JSExport]
    public static string BatchFields()
    {
        try
        {
            if (Sav is null) return NoSave();
            var names = Editable(Sav.BlankPKM).Select(p => p.Name).ToList();
            foreach (var extra in new[] { "IsShiny", "Moves", "RelearnMoves", "Ribbons", "IVs", "EVs", "Legal" })
                if (!names.Contains(extra)) names.Add(extra);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return J(new { ok = true, props = names });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    // scope: "box" (the box with this index, or the party when box < 0), "boxes", "party" or "all".
    [JSExport]
    public static string BatchBegin(string script, string scope, int box)
    {
        try
        {
            if (Sav is null) return NoSave();
            var job = new BatchJob { Save = Sav };
            var errors = ParseBatch(script ?? "", job);
            if (errors.Count > 0) return Err(string.Join("\n", errors.Take(8)));
            job.Targets = BatchTargets(scope ?? "box", box);
            Job = job;
            return J(new { ok = true, total = job.Targets.Count, filters = job.Filters.Count, sets = job.Sets.Count });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static string BatchStep(int count)
    {
        try
        {
            var job = Job;
            if (job is null) return Err("No batch is running.");
            if (!ReferenceEquals(job.Save, Sav)) { Job = null; return Err("The save changed while the batch was running."); }
            for (int k = 0; k < count && job.Next < job.Targets.Count; k++, job.Next++)
            {
                var (box, slot) = job.Targets[job.Next];
                var src = Slot(box, slot);
                if (src is null || src.Species == 0) continue;
                job.Scanned++;
                BatchOne(job, src, box, slot);
            }
            return J(new { ok = true, done = job.Next, total = job.Targets.Count });
        }
        catch (Exception e) { Job = null; return Err(e.Message); }
    }

    // apply = false: just report (preview). apply = true: write every change as ONE undo step.
    [JSExport]
    public static string BatchFinish(bool apply)
    {
        try
        {
            var job = Job; Job = null;
            if (job is null) return Err("No batch is running.");
            if (!ReferenceEquals(job.Save, Sav)) return Err("The save changed while the batch was running.");
            if (apply && job.Changes.Count > 0)
            {
                var items = job.Changes;
                foreach (var x in items) Put(x.After.Clone(), x.Box, x.Slot);
                Record(-2, -1,
                    () => { foreach (var y in items) Put(y.Before.Clone(), y.Box, y.Slot); },
                    () => { foreach (var z in items) Put(z.After.Clone(), z.Box, z.Slot); });
            }
            return J(new
            {
                ok = true,
                applied = apply,
                scanned = job.Scanned,
                matched = job.Matched,
                modified = job.Changes.Count,
                unchanged = job.Matched - job.Changes.Count,
                samples = job.Samples,
                errors = job.Errors.OrderByDescending(e => e.Value.Count).Take(12).Select(e => new { message = e.Key, count = e.Value.Count, at = e.Value.At }).ToList(),
                missing = job.Missing.Select(m => new { name = m.Key, count = m.Value }).ToList(),
            });
        }
        catch (Exception e) { return Err(e.Message); }
    }

    [JSExport]
    public static void BatchCancel() => Job = null;
}
