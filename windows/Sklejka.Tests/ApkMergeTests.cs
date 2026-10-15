using System.IO.Compression;
using Sklejka.Core;
using Xunit;

namespace Sklejka.Tests;

/// <summary>Merges fixtures/sample.apks (bundletool output of the Android «Склейка» app: base-master, base-master_2, abi and ru splits).
/// Needs SKLEJKA_JAVA / SKLEJKA_APKEDITOR / SKLEJKA_APKSIGNER (+ SKLEJKA_AAPT2 for the dump check); set by CI.</summary>
public class ApkMergeTests
{
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "sample.apks");

    [Fact]
    public void CollectPicksNewestVariantAndKeepsAbis()
    {
        var work = Directory.CreateTempSubdirectory("sk-col").FullName;
        try
        {
            var used = ApkMerger.Collect([Fixture], work, []);
            Assert.Contains("base-master_2.apk", used);
            Assert.DoesNotContain("base-master.apk", used);
            Assert.Contains("base-x86.apk", used); Assert.Contains("base-x86_64.apk", used);
            Assert.Contains("base-ru.apk", used);
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public void EncryptedApkmGivesClearMessage()
    {
        var f = Path.Combine(Path.GetTempPath(), "enc-" + Guid.NewGuid() + ".apkm");
        File.WriteAllBytes(f, [1, 2, 3, 4, 5, 6, 7, 8]);
        try { var ex = Assert.Throws<ApkMergeException>(() => ApkMerger.Collect([f], Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), [])); Assert.Contains("зашифрован", ex.Message); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void MergeSignVerify()
    {
        var tools = ApkTools.Locate();
        if (!tools.Ready) { Assert.Fail("tools missing: set SKLEJKA_JAVA/SKLEJKA_APKEDITOR/SKLEJKA_APKSIGNER"); }
        var dir = Directory.CreateTempSubdirectory("sk-apk").FullName;
        try
        {
            var key = SignKey.OwnKey(tools, Path.Combine(dir, "key"));
            var outApk = Path.Combine(dir, "merged.apk");
            var r = ApkMerger.Merge([Fixture], outApk, key, tools);
            Assert.True(File.Exists(outApk));
            Assert.Contains("Verified using v2 scheme (APK Signature Scheme v2): true", r.Verify);
            Assert.Contains("Verified using v3 scheme (APK Signature Scheme v3): true", r.Verify);
            Assert.Matches("^[0-9a-f]{64}$", r.CertSha256);
            using (var z = ZipFile.OpenRead(outApk)) Assert.Single(z.Entries, e => e.FullName == "AndroidManifest.xml");
            var aapt2 = Environment.GetEnvironmentVariable("SKLEJKA_AAPT2");
            if (aapt2 != null)
            {
                var b = Proc.Run(aapt2, ["dump", "badging", outApk]);
                Assert.Equal(0, b.Code);
                Assert.Contains("package: name='ru.ivanvorontsov.sklejka'", b.Output);
                Assert.Contains("'ru'", b.Output);
                var x = Proc.Run(aapt2, ["dump", "xmltree", "--file", "AndroidManifest.xml", outApk]);
                Assert.DoesNotContain("requiredSplitTypes", x.Output);
                Assert.DoesNotContain("isSplitRequired", x.Output);
                Assert.DoesNotContain("com.android.vending.splits", x.Output);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
