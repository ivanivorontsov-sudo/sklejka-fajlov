using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sklejka.Core;

/// <summary>Locations of the bundled tools (tools\jre, tools\APKEditor.jar, tools\apksigner.jar, tools\adb).
/// Environment variables SKLEJKA_JAVA / SKLEJKA_APKEDITOR / SKLEJKA_APKSIGNER / SKLEJKA_ADB override them (tests).</summary>
public sealed record ApkTools(string Java, string ApkEditorJar, string ApkSignerJar, string? Adb)
{
    public string Keytool => Path.Combine(Path.GetDirectoryName(Java)!, OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");

    public static ApkTools Locate(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        var t = Path.Combine(baseDir, "tools");
        string java = Environment.GetEnvironmentVariable("SKLEJKA_JAVA") ?? Path.Combine(t, "jre", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        string ed = Environment.GetEnvironmentVariable("SKLEJKA_APKEDITOR") ?? Path.Combine(t, "APKEditor.jar");
        string sg = Environment.GetEnvironmentVariable("SKLEJKA_APKSIGNER") ?? Path.Combine(t, "apksigner.jar");
        string? adb = Environment.GetEnvironmentVariable("SKLEJKA_ADB") ?? Path.Combine(t, "adb", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        if (!File.Exists(adb)) adb = null;
        return new(java, ed, sg, adb);
    }
    public bool Ready => File.Exists(Java) && File.Exists(ApkEditorJar) && File.Exists(ApkSignerJar);
}

/// <summary>Release key used to sign merged APKs.</summary>
public sealed record SignKey(string Keystore, string StorePass, string? Alias, string? KeyPass)
{
    public static string OwnKeyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sklejka", "key");

    /// <summary>The app's own key, generated on first use (PKCS12 RSA-4096, 100 years).</summary>
    public static SignKey OwnKey(ApkTools tools, string? dir = null)
    {
        dir ??= OwnKeyDir;
        Directory.CreateDirectory(dir);
        var ks = Path.Combine(dir, "sklejka-merge.p12");
        var pwFile = Path.Combine(dir, "password.txt");
        if (File.Exists(ks) && File.Exists(pwFile)) return new(ks, File.ReadAllText(pwFile).Trim(), "sklejka", null);
        var pw = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var r = Proc.Run(tools.Keytool, ["-genkeypair", "-storetype", "PKCS12", "-keystore", ks, "-alias", "sklejka", "-keyalg", "RSA", "-keysize", "4096",
            "-validity", "36500", "-storepass", pw, "-keypass", pw, "-dname", "CN=Sklejka fajlov, O=" + Environment.UserName + ", C=RU"]);
        if (r.Code != 0) throw new InvalidOperationException("Не удалось создать ключ подписи: " + r.Output);
        File.WriteAllText(pwFile, pw);
        return new(ks, pw, "sklejka", null);
    }
}

public sealed record MergeResult(string Output, List<string> UsedSplits, string Verify, string CertSha256, List<string> Notes);

public sealed class ApkMergeException(string message) : Exception(message);

public static class ApkMerger
{
    public static readonly string[] BundleExts = [".apks", ".xapk", ".apkm", ".apk"];
    public static bool IsBundle(string path) =>
        Directory.Exists(path) || BundleExts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Collect the APKs to merge into a temp folder. Handles .apks (SAI/bundletool, incl. variants base-master_2),
    /// .xapk, .apkm, a folder, or a list of .apk files.
    /// </summary>
    public static List<string> Collect(IReadOnlyList<string> inputs, string work, List<string> notes)
    {
        Directory.CreateDirectory(work);
        var apks = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input)) { apks.AddRange(Directory.EnumerateFiles(input, "*.apk", SearchOption.AllDirectories)); continue; }
            if (input.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) { apks.Add(input); continue; }
            ExtractArchive(input, work, apks, notes);
        }
        if (apks.Count == 0) throw new ApkMergeException("Внутри не нашлось ни одного .apk");

        // bundletool .apks: prefer splits/ over standalones/ and universal.apk
        var splits = apks.Where(a => a.Replace('\\', '/').Contains("/splits/")).ToList();
        if (splits.Count > 0) apks = splits;
        // variants: base-master.apk, base-master_2.apk … keep the highest variant of each split
        var re = new Regex(@"^(.*?)(?:_(\d+))?\.apk$", RegexOptions.IgnoreCase);
        (string Stem, int Variant) Key(string a)
        {
            var mm = re.Match(Path.GetFileName(a));
            string stem = mm.Groups[1].Value.ToLowerInvariant(), v = mm.Groups[2].Value;
            if (v == "64" && stem.EndsWith("x86")) return (stem + "_64", 0); // x86_64 is an ABI, not a variant
            return (stem, int.TryParse(v, out var n) ? n : 0);
        }
        var byStem = apks.GroupBy(a => Key(a).Stem);
        var best = byStem.Select(g => g.OrderByDescending(a => Key(a).Variant).First()).ToList();
        if (best.Count < apks.Count) notes.Add($"Взят самый новый вариант сборки ({apks.Count - best.Count} лишних вариантов пропущено).");

        var dir = Path.Combine(work, "in");
        Directory.CreateDirectory(dir);
        var used = new List<string>();
        foreach (var a in best)
        {
            var name = Path.GetFileName(a);
            var dst = Path.Combine(dir, name);
            for (int i = 2; File.Exists(dst); i++) dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "-" + i + ".apk");
            File.Copy(a, dst);
            used.Add(Path.GetFileName(dst));
        }
        return used;
    }

    static void ExtractArchive(string input, string work, List<string> apks, List<string> notes)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(input); }
        catch (InvalidDataException)
        {
            if (input.EndsWith(".apkm", StringComparison.OrdinalIgnoreCase))
                throw new ApkMergeException("Этот .apkm зашифрован APKMirror (новый формат). Его умеет открывать только APKMirror Installer. Скачайте с APKMirror вариант .apk или распакуйте .apkm в APKMirror Installer и перетащите сюда папку с .apk.");
            throw new ApkMergeException("Файл не является zip-архивом: " + Path.GetFileName(input));
        }
        using (zip)
        {
            var dir = Path.Combine(work, "x", Path.GetFileNameWithoutExtension(input));
            bool obb = false;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith(".obb", StringComparison.OrdinalIgnoreCase)) obb = true;
                if (!e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
                var safe = string.Join('/', e.FullName.Replace('\\', '/').Split('/').Where(s => s.Length > 0 && s != "." && s != ".."));
                var dst = Path.Combine(dir, safe.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try { e.ExtractToFile(dst, true); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    throw new ApkMergeException(input.EndsWith(".apkm", StringComparison.OrdinalIgnoreCase)
                        ? "Этот .apkm зашифрован APKMirror. Его открывает только APKMirror Installer; скачайте .apk или папку с распакованными частями."
                        : "Не удалось распаковать " + e.FullName + ": " + ex.Message);
                }
                apks.Add(dst);
            }
            if (obb) notes.Add("В .xapk есть OBB-данные. В APK они не входят: скопируйте папку Android/obb из архива на телефон вручную.");
        }
    }

    /// <summary>Full pipeline: collect → APKEditor merge (sanitizes split attributes) → apksigner v1+v2+v3 → verify.</summary>
    public static MergeResult Merge(IReadOnlyList<string> inputs, string output, SignKey key, ApkTools tools, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!tools.Ready) throw new ApkMergeException("Не найдены встроенные инструменты (Java, APKEditor, apksigner). Переустановите программу.");
        var notes = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "sklejka-apk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            log?.Invoke("Распаковка частей…");
            var used = Collect(inputs, work, notes);
            log?.Invoke("Части: " + string.Join(", ", used));
            if (used.Count == 1) notes.Add("Найден только один APK — это не split, просто переподписываю.");
            var merged = Path.Combine(work, "merged.apk");
            ct.ThrowIfCancellationRequested();
            log?.Invoke("Склейка APKEditor…");
            var m = Proc.Run(tools.Java, ["-Xmx1g", "-jar", tools.ApkEditorJar, "m", "-i", Path.Combine(work, "in"), "-o", merged, "-f", "-clean-meta"], log, ct);
            if (m.Code != 0 || !File.Exists(merged)) throw new ApkMergeException("APKEditor не смог склеить: " + Tail(m.Output));
            ct.ThrowIfCancellationRequested();
            log?.Invoke("Подпись v1 + v2 + v3…");
            var args = new List<string> { "-jar", tools.ApkSignerJar, "sign", "--ks", key.Keystore, "--ks-pass", "pass:" + key.StorePass,
                "--v1-signing-enabled", "true", "--v2-signing-enabled", "true", "--v3-signing-enabled", "true", "--out", output };
            if (!string.IsNullOrEmpty(key.Alias)) { args.Add("--ks-key-alias"); args.Add(key.Alias); }
            args.Add("--key-pass"); args.Add("pass:" + (string.IsNullOrEmpty(key.KeyPass) ? key.StorePass : key.KeyPass));
            args.Add(merged);
            var s = Proc.Run(tools.Java, args, null, ct);
            if (s.Code != 0) throw new ApkMergeException("Не удалось подписать: " + Tail(s.Output.Replace(key.StorePass, "***")));
            log?.Invoke("Проверка подписи…");
            var v = Proc.Run(tools.Java, ["-jar", tools.ApkSignerJar, "verify", "-v", "--print-certs", output], null, ct);
            if (v.Code != 0) throw new ApkMergeException("Проверка подписи не прошла: " + Tail(v.Output));
            var sha = Regex.Match(v.Output, @"SHA-256 digest:\s*([0-9a-f]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            try { File.Delete(output + ".idsig"); } catch { }
            return new(output, used, v.Output, sha, notes);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    static string Tail(string s) { var l = s.Trim().Split('\n'); return string.Join("\n", l.TakeLast(6)); }

    public static List<string> AdbDevices(ApkTools t)
    {
        if (t.Adb == null) return [];
        var r = Proc.Run(t.Adb, ["devices"], timeoutMs: 15000);
        return r.Output.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.EndsWith("\tdevice")).Select(l => l.Split('\t')[0]).ToList();
    }

    public static Proc.Result AdbInstall(ApkTools t, string serial, string apk, Action<string>? log = null) =>
        Proc.Run(t.Adb!, ["-s", serial, "install", "-r", apk], log, timeoutMs: 10 * 60_000);
}

/// <summary>Runs a tool directly (never through cmd), hidden window, captured output.</summary>
public static class Proc
{
    public sealed record Result(int Code, string Output);
    public static Result Run(string exe, IEnumerable<string> args, Action<string>? log = null, CancellationToken ct = default, int timeoutMs = 30 * 60_000)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (sb) sb.AppendLine(e.Data); log?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (sb) sb.AppendLine(e.Data); log?.Invoke(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { return new(-1, $"Не удалось запустить {Path.GetFileName(exe)}: {ex.Message}"); }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return new(-2, sb + "\nПревышено время ожидания"); }
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        return new(p.ExitCode, sb.ToString());
    }
}
