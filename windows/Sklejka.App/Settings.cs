using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Sklejka.App;

public static class Settings
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sklejka", "settings.json");
    public static string Theme { get; set; } = "System";
    public static bool AllowDelete { get; set; }
    public static string? Keystore { get; set; }
    public static string? KeyAlias { get; set; }

    record Data(string Theme, bool AllowDelete, string? Keystore, string? KeyAlias);

    public static void Load()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            if (d != null) { Theme = d.Theme; AllowDelete = d.AllowDelete; Keystore = d.Keystore; KeyAlias = d.KeyAlias; }
        }
        catch { }
    }
    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Data(Theme, AllowDelete, Keystore, KeyAlias)));
    }
}

/// <summary>Explorer integration in HKCU only, through the .NET registry API (never reg.exe).</summary>
public static class ShellIntegration
{
    public static readonly string[] Exts = [".001", ".apks", ".xapk", ".apkm", ".7z.001", ".zip.001"];
    const string Verb = "Sklejka";

    public static bool IsRegistered
    {
        get { using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\SystemFileAssociations\.001\shell\{Verb}"); return k != null; }
    }

    public static void Register(string exe)
    {
        foreach (var ext in new[] { ".001", ".apks", ".xapk", ".apkm" })
        {
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}"))
            {
                k.SetValue("MUIVerb", "Склеить");
                k.SetValue("Icon", $"\"{exe}\",0");
            }
            using (var c = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}\command"))
                c.SetValue("", $"\"{exe}\" \"%1\"");
            using (var o = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                o.SetValue("Sklejka.File", Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var p = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Sklejka.File"))
        {
            p.SetValue("", "Части для склейки");
            using var i = p.CreateSubKey("DefaultIcon"); i.SetValue("", $"\"{exe}\",0");
            using var c = p.CreateSubKey(@"shell\open\command"); c.SetValue("", $"\"{exe}\" \"%1\"");
        }
        Notify();
    }

    public static void Unregister()
    {
        foreach (var ext in new[] { ".001", ".apks", ".xapk", ".apkm" })
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}", false);
            using var o = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
            o?.DeleteValue("Sklejka.File", false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Sklejka.File", false);
        Notify();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, int flags, IntPtr a, IntPtr b);
    static void Notify() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }
}
