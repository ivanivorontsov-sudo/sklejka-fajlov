using System.Text.RegularExpressions;

namespace Sklejka.Core;

public enum PartKind { Numbered, SevenZ, ZipRaw, PartN, ExtN, Underscore, GSplit, Unix, ZipSpan, RarVolumes }

public static class PartKindInfo
{
    public static string Title(this PartKind k) => k switch
    {
        PartKind.Numbered => "Части .001 / .002 (HJSplit, Total Commander, 7-Zip)",
        PartKind.SevenZ => "Разрезанный архив 7z (.7z.001)",
        PartKind.ZipRaw => "Разрезанный архив zip (.zip.001)",
        PartKind.PartN => "Части .part1 / .part01",
        PartKind.ExtN => "Части файл.расширение.1",
        PartKind.Underscore => "Части файл_1.расширение",
        PartKind.GSplit => "Части GSplit (.gsd)",
        PartKind.Unix => "Части split (.aa / .ab)",
        PartKind.ZipSpan => "Многотомный zip (.z01 … .zip)",
        PartKind.RarVolumes => "Многотомный rar (.part1.rar)",
        _ => k.ToString(),
    };
    public static bool Joinable(this PartKind k) => k is not (PartKind.ZipSpan or PartKind.RarVolumes);
}

public sealed record PartName(string Group, string OutputName, int Index, PartKind Kind);

/// <summary>A piece: <see cref="Id"/> is the full path.</summary>
public sealed record Part(string Id, string Name, long Size, PartName Parsed);

public sealed class PartGroup(string outputName, PartKind kind, IReadOnlyList<Part> parts)
{
    public string OutputName { get; } = outputName;
    public PartKind Kind { get; } = kind;
    public IReadOnlyList<Part> Parts { get; } = parts;
    public long TotalSize => Parts.Sum(p => p.Size);
    public IEnumerable<int> Indices => Parts.Select(p => p.Parsed.Index);

    public List<int> Missing
    {
        get
        {
            if (Parts.Count == 0) return [];
            var have = Indices.ToHashSet();
            int start = have.Contains(0) ? 0 : 1, end = Indices.Max();
            return Enumerable.Range(start, Math.Max(0, end - start + 1)).Where(i => !have.Contains(i)).ToList();
        }
    }
    public List<int> Duplicates => Indices.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
    public List<Part> OddSized
    {
        get
        {
            if (Parts.Count < 3) return [];
            var body = Parts.Take(Parts.Count - 1).ToList();
            long common = body.GroupBy(p => p.Size).OrderByDescending(g => g.Count()).First().Key;
            var odd = body.Where(p => p.Size != common).ToList();
            if (Parts[^1].Size > common) odd.Add(Parts[^1]);
            return odd;
        }
    }
    public PartGroup WithOrder(IReadOnlyList<Part> order) => new(OutputName, Kind, order);
}

public static class PartDetector
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex RarVol = new(@"^(.+)\.part(\d+)\.rar$", I);
    static readonly Regex SevenZ = new(@"^(.+\.7z)\.(\d{3,})$", I);
    static readonly Regex ZipRaw = new(@"^(.+\.zip)\.(\d{3,})$", I);
    static readonly Regex Numbered = new(@"^(.+)\.(\d{3,})$");
    static readonly Regex PartN = new(@"^(.+)\.part(\d+)$", I);
    static readonly Regex ZipSpan = new(@"^(.+)\.z(\d{2,})$", I);
    static readonly Regex ExtN = new(@"^(.+\.[^.\d][^.]*)\.(\d{1,2})$");
    static readonly Regex GSplit = new(@"^(.*?)[ ._-]?(\d+)\.gsd$", I);
    static readonly Regex Underscore = new(@"^(.+)_(\d+)(\.[A-Za-z0-9]{1,6})?$");
    static readonly Regex Unix = new(@"^(.+\.[^.]+)\.([a-z]{2})$");

    public static PartName? Parse(string name)
    {
        Match m;
        if ((m = RarVol.Match(name)).Success) return new($"rar:{m.Groups[1].Value.ToLowerInvariant()}", m.Groups[1].Value + ".rar", int.Parse(m.Groups[2].Value), PartKind.RarVolumes);
        if ((m = SevenZ.Match(name)).Success) return Num(m, PartKind.SevenZ);
        if ((m = ZipRaw.Match(name)).Success) return Num(m, PartKind.ZipRaw);
        if ((m = Numbered.Match(name)).Success) return Num(m, PartKind.Numbered);
        if ((m = PartN.Match(name)).Success) return Num(m, PartKind.PartN);
        if ((m = ZipSpan.Match(name)).Success) return new($"zipspan:{m.Groups[1].Value.ToLowerInvariant()}", m.Groups[1].Value + ".zip", int.Parse(m.Groups[2].Value), PartKind.ZipSpan);
        if ((m = ExtN.Match(name)).Success) return Num(m, PartKind.ExtN);
        if ((m = GSplit.Match(name)).Success)
        {
            var b = m.Groups[1].Value.Length == 0 ? "gsplit" : m.Groups[1].Value;
            return new($"gsd:{b.ToLowerInvariant()}", b, int.Parse(m.Groups[2].Value), PartKind.GSplit);
        }
        if ((m = Underscore.Match(name)).Success)
        {
            var o = m.Groups[1].Value + m.Groups[3].Value;
            return new($"us:{o.ToLowerInvariant()}", o, int.Parse(m.Groups[2].Value), PartKind.Underscore);
        }
        if ((m = Unix.Match(name)).Success)
        {
            var s = m.Groups[2].Value; int idx = (s[0] - 'a') * 26 + (s[1] - 'a') + 1;
            return new($"unix:{m.Groups[1].Value.ToLowerInvariant()}", m.Groups[1].Value, idx, PartKind.Unix);
        }
        return null;
    }

    static PartName Num(Match m, PartKind k) => new($"{k}:{m.Groups[1].Value.ToLowerInvariant()}", m.Groups[1].Value, int.Parse(m.Groups[2].Value), k);

    /// <param name="files">(id/path, name, size)</param>
    public static List<PartGroup> Group(IEnumerable<(string Id, string Name, long Size)> files)
    {
        var all = files.ToList();
        var parts = all.Select(f => (f, p: Parse(f.Name))).Where(x => x.p != null).Select(x => new Part(x.f.Id, x.f.Name, x.f.Size, x.p!)).ToList();
        var groups = parts.GroupBy(p => p.Parsed.Group).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (key, list) in groups.ToList())
        {
            if (list[0].Parsed.Kind != PartKind.ZipSpan) continue;
            var zipName = list[0].Parsed.OutputName;
            var last = all.FirstOrDefault(f => string.Equals(f.Name, zipName, StringComparison.OrdinalIgnoreCase));
            if (last.Name == null) continue;
            list.Add(new Part(last.Id, last.Name, last.Size, new PartName(key, zipName, list.Max(p => p.Parsed.Index) + 1, PartKind.ZipSpan)));
        }
        return groups.Values
            .Where(l => l[0].Parsed.Kind is not (PartKind.Underscore or PartKind.Unix) || l.Count >= 2)
            .Select(l => new PartGroup(l[0].Parsed.OutputName, l[0].Parsed.Kind, Sort(l)))
            .OrderBy(g => g.OutputName, NaturalOrder.Instance).ToList();
    }

    public static List<Part> Sort(IEnumerable<Part> list) =>
        list.OrderBy(p => p.Parsed.Index).ThenBy(p => p.Name, NaturalOrder.Instance).ToList();

    /// <summary>All files in the folder of <paramref name="path"/> (for auto-finding siblings).</summary>
    public static List<(string, string, long)> Siblings(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Directory.EnumerateFiles(dir).Select(f => new FileInfo(f)).Select(f => (f.FullName, f.Name, f.Length)).ToList();
    }
}

public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();
    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsAsciiDigit(ca) && char.IsAsciiDigit(cb))
            {
                int ei = i; while (ei < a.Length && char.IsAsciiDigit(a[ei])) ei++;
                int ej = j; while (ej < b.Length && char.IsAsciiDigit(b[ej])) ej++;
                var na = a[i..ei].TrimStart('0'); var nb = b[j..ej].TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int c = string.CompareOrdinal(na, nb); if (c != 0) return c;
                i = ei; j = ej;
            }
            else
            {
                int c = char.ToLowerInvariant(ca).CompareTo(char.ToLowerInvariant(cb)); if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }
}
