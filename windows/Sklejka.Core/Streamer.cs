using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sklejka.Core;

public sealed record Hashes(string Crc32, string Md5, string Sha256);

public sealed class MultiHash : IDisposable
{
    readonly System.IO.Hashing.Crc32 crc = new();
    readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    readonly IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public void Update(ReadOnlySpan<byte> b) { crc.Append(b); md5.AppendData(b); sha.AppendData(b); }
    public Hashes Result() => new(crc.GetCurrentHashAsUInt32().ToString("X8"), Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha.GetHashAndReset()));
    public void Dispose() { md5.Dispose(); sha.Dispose(); }
}

public readonly record struct ProgressInfo(long Done, long Total, int PartIndex);

public static class Streamer
{
    public const int Buf = 1 << 20;

    /// <summary>Concatenate parts into <paramref name="output"/>. With <paramref name="skip"/> &gt; 0 the first bytes already
    /// in the output are re-hashed and writing continues after them (resume).</summary>
    public static Hashes Join(IReadOnlyList<string> parts, string output, long skip = 0, Action<ProgressInfo>? progress = null,
        CancellationToken ct = default, Action<long>? checkpoint = null)
    {
        var sizes = parts.Select(p => new FileInfo(p).Length).ToList();
        long total = sizes.Sum();
        var buf = new byte[Buf];
        using var h = new MultiHash();
        long done = 0;
        using var outFs = new FileStream(output, skip > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, Buf);
        if (skip > 0)
        {
            if (outFs.Length < skip) throw new InvalidOperationException("Готовая часть файла короче, чем записано");
            outFs.SetLength(skip);
            outFs.Position = 0;
            long left = skip;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = outFs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) throw new InvalidOperationException("Не удалось прочитать готовую часть");
                h.Update(buf.AsSpan(0, n)); left -= n; done += n;
            }
            progress?.Invoke(new(done, total, -1));
        }
        else if (total > 0) { try { outFs.SetLength(0); } catch { } }
        long offset = 0, sinceCp = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            long end = offset + sizes[i];
            if (end <= skip) { offset = end; continue; }
            using var s = new FileStream(parts[i], FileMode.Open, FileAccess.Read, FileShare.Read, Buf, FileOptions.SequentialScan);
            if (skip > offset) s.Position = skip - offset;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                outFs.Write(buf, 0, n); h.Update(buf.AsSpan(0, n)); done += n; sinceCp += n;
                progress?.Invoke(new(done, total, i));
                if (checkpoint != null && sinceCp >= 64L << 20) { outFs.Flush(true); checkpoint(outFs.Position); sinceCp = 0; }
            }
            offset = end;
        }
        outFs.Flush(true);
        return h.Result();
    }

    public static int PartCount(long total, long partSize) => total <= 0 ? 1 : (int)((total + partSize - 1) / partSize);

    /// <summary>Split into name.001, name.002… in <paramref name="dir"/>. Returns created files and hashes.</summary>
    public static (List<string> Files, Hashes Hash) Split(string input, string dir, long partSize, Action<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (partSize <= 0) throw new ArgumentOutOfRangeException(nameof(partSize));
        long total = new FileInfo(input).Length;
        int count = PartCount(total, partSize), digits = Math.Max(3, count.ToString().Length);
        var name = Path.GetFileName(input);
        var created = new List<string>();
        var buf = new byte[Buf];
        using var h = new MultiHash();
        try
        {
            using var s = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, Buf, FileOptions.SequentialScan);
            long done = 0;
            for (int idx = 1; idx <= count; idx++)
            {
                var path = Path.Combine(dir, name + "." + idx.ToString().PadLeft(digits, '0'));
                created.Add(path);
                using var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, Buf);
                long left = partSize;
                int n;
                while (left > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    o.Write(buf, 0, n); h.Update(buf.AsSpan(0, n)); left -= n; done += n;
                    progress?.Invoke(new(done, total, idx - 1));
                }
            }
            return (created, h.Result());
        }
        catch
        {
            foreach (var f in created) try { File.Delete(f); } catch { }
            throw;
        }
    }
}

public static class ChecksumFiles
{
    public sealed record Expect(string? FileName, long? Size, string? Crc32, string? Md5, string? Sha256);

    public static bool IsChecksumFile(string name)
    {
        var l = name.ToLowerInvariant();
        return l.EndsWith(".crc") || l.EndsWith(".sfv") || l.EndsWith(".md5") || l.EndsWith(".sha256");
    }

    public static Expect? Parse(string name, string text, string target)
    {
        var lower = name.ToLowerInvariant();
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';') && !l.StartsWith('#')).ToList();
        if (lower.EndsWith(".crc"))
        {
            var kv = lines.Select(l => (l, i: l.IndexOf('='))).Where(x => x.i > 0)
                .GroupBy(x => x.l[..x.i].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().l[(g.First().i + 1)..].Trim());
            if (kv.Count == 0) return null;
            return new(kv.GetValueOrDefault("filename"), long.TryParse(kv.GetValueOrDefault("size"), out var sz) ? sz : null,
                kv.GetValueOrDefault("crc32")?.ToUpperInvariant().PadLeft(8, '0'), null, null);
        }
        if (lower.EndsWith(".sfv"))
        {
            var e = lines.Select(l => Regex.Match(l, @"^(.+?)\s+([0-9A-Fa-f]{8})$")).Where(m => m.Success).Select(m => (m.Groups[1].Value.Trim(), m.Groups[2].Value)).ToList();
            var p = Pick(e, target);
            return p == null ? null : new(p.Value.Item1, null, p.Value.Item2.ToUpperInvariant(), null, null);
        }
        int len = lower.EndsWith(".md5") ? 32 : 64;
        var hx = lines.Select(l => Regex.Match(l, $@"^([0-9A-Fa-f]{{{len}}})\s+\*?(.+)$")).Where(m => m.Success)
            .Select(m => (m.Groups[2].Value.Trim(), m.Groups[1].Value.ToLowerInvariant())).ToList();
        if (hx.Count == 0) hx = lines.Where(l => Regex.IsMatch(l, $"^[0-9A-Fa-f]{{{len}}}$")).Select(l => ("", l.ToLowerInvariant())).ToList();
        var q = Pick(hx, target);
        if (q == null) return null;
        return len == 32 ? new(q.Value.Item1, null, null, q.Value.Item2, null) : new(q.Value.Item1, null, null, null, q.Value.Item2);
    }

    static (string, string)? Pick(List<(string, string)> e, string target)
    {
        foreach (var x in e) if (string.Equals(Path.GetFileName(x.Item1.Replace('\\', '/')), target, StringComparison.OrdinalIgnoreCase)) return x;
        return e.Count == 1 ? e[0] : null;
    }

    public static List<(string Label, bool Ok)> Verify(Expect e, Hashes h, long size)
    {
        var r = new List<(string, bool)>();
        if (e.Size is { } s) r.Add(("Размер", s == size));
        if (e.Crc32 != null) r.Add(("CRC32", string.Equals(e.Crc32, h.Crc32, StringComparison.OrdinalIgnoreCase)));
        if (e.Md5 != null) r.Add(("MD5", string.Equals(e.Md5, h.Md5, StringComparison.OrdinalIgnoreCase)));
        if (e.Sha256 != null) r.Add(("SHA-256", string.Equals(e.Sha256, h.Sha256, StringComparison.OrdinalIgnoreCase)));
        return r;
    }

    public static string TcCrc(string fileName, long size, string crc) => $"filename={fileName}\r\nsize={size}\r\ncrc32={crc}\r\n";
}

public static class Fmt
{
    static readonly System.Globalization.CultureInfo Ru = new("ru-RU");
    public static string Size(long b)
    {
        if (b < 1024) return $"{b} Б";
        string[] u = ["КБ", "МБ", "ГБ", "ТБ"]; double v = b / 1024.0; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 ? "0.00" : "0.0", Ru) + " " + u[i];
    }
}

/// <summary>Resume checkpoint stored as JSON.</summary>
public sealed record Checkpoint(List<string> Parts, string Output, long Written)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sklejka", "resume.json");
    public void Save(string? path = null) { path ??= DefaultPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(this)); }
    public static Checkpoint? Load(string? path = null) { try { return JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path ?? DefaultPath)); } catch { return null; } }
    public static void Clear(string? path = null) { try { File.Delete(path ?? DefaultPath); } catch { } }
}
