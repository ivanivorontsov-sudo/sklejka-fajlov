using System.Security.Cryptography;
using Sklejka.Core;
using Xunit;

namespace Sklejka.Tests;

public class PartTests
{
    static List<PartGroup> G(params string[] n) => PartDetector.Group(n.Select(x => (x, x, 10L)));

    [Fact] public void Numbered() { var p = PartDetector.Parse("Фильм.mkv.001")!; Assert.Equal("Фильм.mkv", p.OutputName); Assert.Equal(1, p.Index); Assert.Equal(PartKind.Numbered, p.Kind); }
    [Fact] public void ApkRawSplit() { var p = PartDetector.Parse("game.apk.002")!; Assert.Equal("game.apk", p.OutputName); Assert.Equal(2, p.Index); }
    [Fact] public void Archives()
    {
        Assert.Equal(PartKind.SevenZ, PartDetector.Parse("a.7z.002")!.Kind);
        Assert.Equal(PartKind.ZipRaw, PartDetector.Parse("a.ZIP.010")!.Kind);
        var r = PartDetector.Parse("backup.part03.rar")!;
        Assert.Equal(PartKind.RarVolumes, r.Kind); Assert.Equal(3, r.Index); Assert.False(r.Kind.Joinable());
    }
    [Fact] public void Others()
    {
        Assert.Equal(12, PartDetector.Parse("x.iso.part12")!.Index);
        Assert.Equal("doc.pdf", PartDetector.Parse("doc.pdf.2")!.OutputName);
        Assert.Equal("song.mp3", PartDetector.Parse("song_3.mp3")!.OutputName);
        Assert.Equal(2, PartDetector.Parse("Disk2.gsd")!.Index);
        Assert.Equal(2, PartDetector.Parse("big.tar.ab")!.Index);
        Assert.Null(PartDetector.Parse("photo.jpg"));
    }
    [Fact] public void NaturalSort()
    {
        var g = G(Enumerable.Range(1, 12).Select(i => $"v.mp4.part{i}").OrderBy(_ => Guid.NewGuid()).ToArray()).Single();
        Assert.Equal(Enumerable.Range(1, 12), g.Indices); Assert.Empty(g.Missing);
        Assert.Equal(["A1", "a2", "a02b", "a10"], new[] { "a10", "a2", "A1", "a02b" }.OrderBy(x => x, NaturalOrder.Instance));
    }
    [Fact] public void MissingDuplicatesOdd()
    {
        var g = PartDetector.Group([("1", "f.bin.001", 5), ("2", "f.bin.003", 5), ("3", "f.bin.003", 5), ("4", "f.bin.005", 2)]).Single();
        Assert.Equal([2, 4], g.Missing); Assert.Equal([3], g.Duplicates);
        var o = PartDetector.Group([("1", "f.001", 10), ("2", "f.002", 7), ("3", "f.003", 10), ("4", "f.004", 3)]).Single();
        Assert.Equal(["f.002"], o.OddSized.Select(p => p.Name));
    }
    [Fact] public void GroupsAndSpan()
    {
        Assert.Equal(["a", "b.7z"], G("a.001", "a.002", "b.7z.001", "b.7z.002", "readme.txt", "a.crc").Select(x => x.OutputName));
        Assert.Equal(["arc.z01", "arc.z02", "arc.zip"], G("arc.z01", "arc.z02", "arc.zip").Single().Parts.Select(p => p.Name));
        Assert.Empty(G("holiday_2024.jpg"));
    }
    [Fact] public void ChecksumFilesParse()
    {
        var e = ChecksumFiles.Parse("f.crc", "filename=f.bin\r\nsize=12\r\ncrc32=0ab\r\n", "f.bin")!;
        Assert.Equal(12L, e.Size); Assert.Equal("000000AB", e.Crc32);
        Assert.Equal("DEADBEEF", ChecksumFiles.Parse("x.sfv", "; c\nf.bin DEADBEEF\nother 00000000", "f.bin")!.Crc32);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", ChecksumFiles.Parse("x.md5", "D41D8CD98F00B204E9800998ECF8427E *f.bin", "f.bin")!.Md5);
    }
}

public class StreamTests
{
    [Fact]
    public void SplitJoinByteExactAndResume()
    {
        var dir = Directory.CreateTempSubdirectory("sklejka").FullName;
        try
        {
            var data = new byte[5 * 1024 * 1024 + 12345]; new Random(42).NextBytes(data);
            var src = Path.Combine(dir, "big.bin"); File.WriteAllBytes(src, data);
            var parts = Path.Combine(dir, "p"); Directory.CreateDirectory(parts);
            var (files, hs) = Streamer.Split(src, parts, 1_300_000);
            Assert.Equal(Streamer.PartCount(data.Length, 1_300_000), files.Count);
            var g = PartDetector.Group(Directory.GetFiles(parts).Select(f => (f, Path.GetFileName(f), new FileInfo(f).Length))).Single();
            Assert.Empty(g.Missing);
            var outp = Path.Combine(dir, "joined.bin");
            var h = Streamer.Join(g.Parts.Select(p => p.Id).ToList(), outp);
            var expect = Convert.ToHexStringLower(SHA256.HashData(data));
            Assert.Equal(expect, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outp))));
            Assert.Equal(expect, h.Sha256); Assert.Equal(hs, h);
            // resume: cut the output and continue
            using (var fs = new FileStream(outp, FileMode.Open)) fs.SetLength(3_000_001);
            var h2 = Streamer.Join(g.Parts.Select(p => p.Id).ToList(), outp, skip: 3_000_001);
            Assert.Equal(expect, h2.Sha256);
            Assert.Equal(expect, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outp))));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cancel()
    {
        var dir = Directory.CreateTempSubdirectory("sklejka").FullName;
        try
        {
            var f = Path.Combine(dir, "a.001"); File.WriteAllBytes(f, new byte[3_000_000]);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Streamer.Join([f], Path.Combine(dir, "a"), ct: cts.Token));
        }
        finally { Directory.Delete(dir, true); }
    }
}
