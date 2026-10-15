using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Sklejka.Core;

namespace Sklejka.App;

public sealed class PartRow(Part p, int n)
{
    public Part Part { get; } = p;
    public int Number { get; set; } = n;
    public string Name => Part.Name;
    public string SizeText => Fmt.Size(Part.Size);
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<PartRow> rows = [];
    List<PartGroup> groups = [];
    List<(string Id, string Name, long Size)> docs = [];
    CancellationTokenSource? cts;
    string? lastOutput; List<string> lastParts = [];
    List<string> apkInputs = [];
    string? lastApk;
    string? splitPath;
    readonly ApkTools tools = ApkTools.Locate();
    readonly string? screenshotPath;

    static readonly (string Title, long Size)[] Presets =
        [("10 МБ", 10L << 20), ("100 МБ", 100L << 20), ("700 МБ (CD)", 700L << 20), ("1 ГБ", 1L << 30), ("2 ГБ", 2L << 30), ("4 ГБ − 1 (FAT32)", (4L << 30) - 1), ("Свой", -1)];

    public MainWindow(string[] args)
    {
        InitializeComponent();
        PartsList.ItemsSource = rows;
        foreach (var p in Presets) SizeBox.Items.Add(p.Title);
        SizeBox.SelectedIndex = 1;
        ThemeBox.SelectedIndex = Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ShellCheck.IsChecked = ShellIntegration.IsRegistered;
        DeleteCheck.IsChecked = Settings.AllowDelete;
        if (Settings.Keystore != null) { KsPath.Text = Settings.Keystore; KsAlias.Text = Settings.KeyAlias ?? ""; }
        AboutText.Text = $"Склейка файлов {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · APKEditor (Apache-2.0), apksigner (Apache-2.0), OpenJDK (GPLv2+CE)";
        RefreshResume();
        OnRefreshAdb(null, null);

        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--screenshot" && i + 1 < args.Length) { screenshotPath = args[++i]; continue; }
            if (args[i] == "--tab" && i + 1 < args.Length) { Tabs.SelectedIndex = int.Parse(args[++i]); continue; }
            files.Add(args[i]);
        }
        if (files.Count > 0) Loaded += (_, _) => Accept(files);
        if (screenshotPath != null) ContentRendered += async (_, _) => { await Task.Delay(1500); Snap(screenshotPath); Close(); };
    }

    // ---------- input ----------
    void OnDragOver(object s, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    void OnDrop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) Accept(f.ToList()); }

    void Accept(List<string> paths)
    {
        bool apkish = paths.All(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.apk").Any() : ApkMerger.IsBundle(p) && PartDetector.Parse(Path.GetFileName(p)) == null);
        if (apkish && paths.Any(p => !p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || paths.Count > 1 || Directory.Exists(p)))
        {
            SetApkInputs(paths); Tabs.SelectedIndex = 1; return;
        }
        Tabs.SelectedIndex = 0;
        if (paths.Count == 1 && Directory.Exists(paths[0])) { LoadDocs(Directory.EnumerateFiles(paths[0]).ToList()); return; }
        if (paths.Count == 1 && File.Exists(paths[0])) { LoadDocs(PartDetector.Siblings(paths[0]).Select(x => x.Item1).ToList(), Path.GetFileName(paths[0])); return; }
        LoadDocs(paths.Where(File.Exists).ToList());
    }

    void LoadDocs(List<string> files, string? focusName = null)
    {
        docs = files.Select(f => new FileInfo(f)).Select(f => (f.FullName, f.Name, f.Length)).ToList();
        groups = PartDetector.Group(docs);
        GroupBox.Items.Clear();
        foreach (var g in groups) GroupBox.Items.Add($"{g.OutputName}  ({g.Parts.Count} ч.)");
        GroupBox.Visibility = groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (groups.Count == 0)
        {
            GroupCard.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "Это не похоже на части разрезанного файла.\nПоддерживаются: .001/.002, .part1, файл.ext.1, файл_1.ext, .gsd, .aa/.ab, .7z.001, .zip.001, .apk.001, .z01, .part1.rar", Title);
            return;
        }
        int idx = 0;
        if (focusName != null)
        {
            var key = PartDetector.Parse(focusName)?.Group;
            idx = Math.Max(0, groups.FindIndex(g => g.Parts[0].Parsed.Group == key));
        }
        GroupBox.SelectedIndex = idx;
        ShowGroup(idx);
    }

    void OnGroupChanged(object s, SelectionChangedEventArgs e) { if (GroupBox.SelectedIndex >= 0) ShowGroup(GroupBox.SelectedIndex); }

    PartGroup? Current => GroupBox.SelectedIndex >= 0 && GroupBox.SelectedIndex < groups.Count
        ? groups[GroupBox.SelectedIndex].WithOrder(rows.Select(r => r.Part).ToList()) : null;

    void ShowGroup(int i)
    {
        if (i < 0 || i >= groups.Count) return;
        rows.Clear();
        foreach (var p in groups[i].Parts) rows.Add(new PartRow(p, rows.Count + 1));
        UpdateGroupInfo();
        GroupCard.Visibility = Visibility.Visible;
    }

    void Renumber() { for (int i = 0; i < rows.Count; i++) rows[i].Number = i + 1; PartsList.Items.Refresh(); UpdateGroupInfo(); }

    void UpdateGroupInfo()
    {
        var g = Current; if (g == null) return;
        GroupTitle.Text = g.OutputName;
        GroupKind.Text = g.Kind.Title();
        GroupInfo.Text = $"{g.Parts.Count} {Plural(g.Parts.Count)} · {Fmt.Size(g.TotalSize)}";
        var w = new List<string>();
        if (g.Missing.Count > 0) w.Add("Не хватает частей: " + string.Join(", ", g.Missing.Select(x => "№" + x)));
        if (g.Duplicates.Count > 0) w.Add("Лишние повторы: " + string.Join(", ", g.Duplicates.Select(x => "№" + x)));
        if (g.OddSized.Count > 0) w.Add("Размер отличается у: " + string.Join(", ", g.OddSized.Select(p => p.Name)));
        if (g.Kind == PartKind.RarVolumes) w.Add("Это тома RAR: у каждого свой заголовок, простое соединение даст битый архив. Откройте первый том в WinRAR или 7-Zip — он сам прочтёт остальные.");
        if (g.Kind == PartKind.ZipSpan) w.Add("Это многотомный zip (.z01 … .zip): склеивать его нельзя, откройте .zip в 7-Zip или WinRAR, тома должны лежать рядом.");
        if (g.Kind == PartKind.GSplit) w.Add("GSplit по умолчанию добавляет в части свой заголовок. Склейка верна, только если части сделаны без заголовка.");
        WarnText.Text = string.Join("\n", w.Select(x => "• " + x));
        WarnBox.Visibility = w.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        JoinBtn.IsEnabled = g.Kind.Joinable() && cts == null;
        JoinBtn.Content = g.Kind.Joinable() ? $"Соединить → {g.OutputName}" : "Эти тома не склеивают";
        OpenFirstBtn.Visibility = g.Kind.Joinable() ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Plural(int n) => n % 10 == 1 && n % 100 != 11 ? "часть" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "части" : "частей";

    void OnPickParts(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Title = "Части файла" };
        if (d.ShowDialog(this) == true) Accept(d.FileNames.ToList());
    }
    void OnPickFolder(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Папка с частями" };
        if (d.ShowDialog(this) == true) LoadDocs(Directory.EnumerateFiles(d.FolderName).ToList());
    }
    void OnSortByNumber(object s, RoutedEventArgs e)
    {
        var sorted = PartDetector.Sort(rows.Select(r => r.Part)); rows.Clear();
        foreach (var p in sorted) rows.Add(new PartRow(p, rows.Count + 1));
        Renumber();
    }
    void OnOpenFirst(object s, RoutedEventArgs e) { if (rows.Count > 0) Open(rows[0].Part.Id); }

    // ---------- drag reorder ----------
    Point dragStart; PartRow? dragRow;
    void OnListMouseDown(object s, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(PartsList);
        dragRow = (e.OriginalSource as FrameworkElement)?.DataContext as PartRow;
    }
    void OnListMouseMove(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragRow == null) return;
        var p = e.GetPosition(PartsList);
        if (Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(PartsList, new DataObject(typeof(PartRow), dragRow), DragDropEffects.Move);
        dragRow = null;
    }
    void OnListDragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PartRow))) { e.Effects = DragDropEffects.Move; e.Handled = true; }
    }
    void OnListDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PartRow)) is not PartRow src) return;
        e.Handled = true;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as PartRow;
        int from = rows.IndexOf(src), to = target != null ? rows.IndexOf(target) : rows.Count - 1;
        if (from < 0 || to < 0 || from == to) return;
        rows.Move(from, to);
        Renumber();
    }

    // ---------- join ----------
    async void OnJoin(object s, RoutedEventArgs e)
    {
        var g = Current; if (g == null) return;
        var d = new SaveFileDialog { FileName = g.OutputName, InitialDirectory = Path.GetDirectoryName(g.Parts[0].Id), Title = "Куда сохранить результат" };
        if (d.ShowDialog(this) != true) return;
        if (g.Parts.Any(p => string.Equals(Path.GetFullPath(p.Id), Path.GetFullPath(d.FileName), StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "Нельзя сохранить поверх одной из частей.", Title); return; }
        if (!EnoughSpace(d.FileName, g.TotalSize)) return;
        await RunJoin(g.Parts.Select(p => p.Id).ToList(), d.FileName, 0);
    }

    bool EnoughSpace(string target, long need)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace;
            if (free < need) { MessageBox.Show(this, $"Не хватает места: нужно {Fmt.Size(need)}, свободно {Fmt.Size(free)}.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        }
        catch { }
        return true;
    }

    async Task RunJoin(List<string> parts, string output, long skip)
    {
        var cp = new Checkpoint(parts, output, skip); cp.Save();
        ResultCard.Visibility = Visibility.Collapsed;
        var name = Path.GetFileName(output);
        var sw = Stopwatch.StartNew();
        StartProgress((skip > 0 ? "Продолжаю: " : "Соединяю: ") + name);
        try
        {
            var token = cts!.Token;
            var h = await Task.Run(() => Streamer.Join(parts, output, skip, ProgressSink(sw, parts.Count), token, w => (cp with { Written = w }).Save()));
            Checkpoint.Clear();
            long size = new FileInfo(output).Length;
            lastOutput = output; lastParts = parts;
            ShowResult(true, $"{name} — {Fmt.Size(size)} за {sw.Elapsed:mm\\:ss}", h, size);
        }
        catch (OperationCanceledException) { ShowResult(false, "Отменено. Можно продолжить с места остановки.", null, 0); }
        catch (Exception ex) { ShowResult(false, "Ошибка: " + ex.Message, null, 0); }
        finally { StopProgress(); RefreshResume(); }
    }

    Action<ProgressInfo> ProgressSink(Stopwatch sw, int count)
    {
        long last = 0;
        return p =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now - last < 50 && p.Done != p.Total) return;
            last = now;
            Dispatcher.BeginInvoke(() =>
            {
                Prog.Value = p.Total > 0 ? p.Done * 1000.0 / p.Total : 0;
                double speed = now > 0 ? p.Done * 1000.0 / now : 0;
                var eta = speed > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / speed) : TimeSpan.Zero;
                ProgText.Text = $"{Fmt.Size(p.Done)} из {Fmt.Size(p.Total)} · {(p.Total > 0 ? p.Done * 100 / p.Total : 0)}% · часть {Math.Max(1, p.PartIndex + 1)} из {count} · {Fmt.Size((long)speed)}/с · осталось {eta:hh\\:mm\\:ss}";
            });
        };
    }

    void StartProgress(string title)
    {
        cts = new CancellationTokenSource();
        ProgTitle.Text = title; Prog.Value = 0; ProgText.Text = "";
        ProgressCard.Visibility = Visibility.Visible;
        JoinBtn.IsEnabled = SplitBtn.IsEnabled = MergeBtn.IsEnabled = false;
    }
    void StopProgress()
    {
        cts?.Dispose(); cts = null;
        ProgressCard.Visibility = Visibility.Collapsed;
        SplitBtn.IsEnabled = MergeBtn.IsEnabled = true;
        UpdateGroupInfo();
        if (Current == null) JoinBtn.IsEnabled = true;
    }
    void OnCancel(object s, RoutedEventArgs e) => cts?.Cancel();

    void ShowResult(bool ok, string msg, Hashes? h, long size)
    {
        ResultCard.Visibility = Visibility.Visible;
        ResultTitle.Text = ok ? "✓ Готово" : "Не получилось";
        ResultText.Text = msg;
        HashText.Text = h == null ? "" : $"CRC32    {h.Crc32}\nMD5      {h.Md5}\nSHA-256  {h.Sha256}";
        HashText.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;
        var v = new StringBuilder();
        if (ok && h != null && lastOutput != null)
        {
            var target = Path.GetFileName(lastOutput);
            foreach (var d in docs.Where(d => ChecksumFiles.IsChecksumFile(d.Name)))
            {
                string text; try { text = File.ReadAllText(d.Id); } catch { continue; }
                var exp = ChecksumFiles.Parse(d.Name, text, target) ?? ChecksumFiles.Parse(d.Name, text, Current?.OutputName ?? target);
                if (exp == null) continue;
                var checks = ChecksumFiles.Verify(exp, h, size);
                bool good = checks.Count > 0 && checks.All(c => c.Ok);
                v.AppendLine(good ? $"✓ Совпадает с {d.Name}" : $"✗ Не совпадает с {d.Name}: " + string.Join(", ", checks.Where(c => !c.Ok).Select(c => c.Label)));
            }
        }
        VerifyText.Text = v.ToString().Trim();
        bool hasOut = ok && lastOutput != null;
        foreach (var b in new UIElement[] { ExtractBtn, ToApkBtn, DeletePartsBtn }) b.Visibility = Visibility.Collapsed;
        if (hasOut)
        {
            if (lastOutput!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ExtractBtn.Visibility = Visibility.Visible;
            if (ApkMerger.IsBundle(lastOutput) && !lastOutput.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) ToApkBtn.Visibility = Visibility.Visible;
            if (Settings.AllowDelete) DeletePartsBtn.Visibility = Visibility.Visible;
        }
    }

    void OnOpenResult(object s, RoutedEventArgs e) { if (lastOutput != null) Open(lastOutput); }
    void OnShowResult(object s, RoutedEventArgs e) { if (lastOutput != null) ShowInFolder(lastOutput); }
    void OnResultToApk(object s, RoutedEventArgs e) { if (lastOutput != null) { SetApkInputs([lastOutput]); Tabs.SelectedIndex = 1; } }

    async void OnExtract(object s, RoutedEventArgs e)
    {
        if (lastOutput == null) return;
        var d = new OpenFolderDialog { Title = "Куда распаковать" };
        if (d.ShowDialog(this) != true) return;
        StartProgress("Распаковка " + Path.GetFileName(lastOutput));
        try
        {
            var src = lastOutput; var dst = d.FolderName;
            await Task.Run(() => ZipFile.ExtractToDirectory(src, dst, overwriteFiles: false));
            ShowInFolder(dst);
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось распаковать: " + ex.Message, Title); }
        finally { StopProgress(); }
    }

    void OnDeleteParts(object s, RoutedEventArgs e)
    {
        if (lastParts.Count == 0) return;
        if (MessageBox.Show(this, $"Удалить {lastParts.Count} {Plural(lastParts.Count)} в корзину?\nРезультат {Path.GetFileName(lastOutput)} останется.", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int ok = 0;
        foreach (var p in lastParts)
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); ok++; } catch { }
        MessageBox.Show(this, $"Удалено частей: {ok} из {lastParts.Count}", Title);
        DeletePartsBtn.Visibility = Visibility.Collapsed;
    }

    void RefreshResume()
    {
        var c = Checkpoint.Load();
        if (c == null || !c.Parts.All(File.Exists)) { ResumeCard.Visibility = Visibility.Collapsed; return; }
        long total = c.Parts.Sum(p => new FileInfo(p).Length);
        ResumeText.Text = $"{Path.GetFileName(c.Output)}: записано {Fmt.Size(c.Written)} из {Fmt.Size(total)}";
        ResumeCard.Visibility = Visibility.Visible;
    }
    async void OnResume(object s, RoutedEventArgs e) { var c = Checkpoint.Load(); if (c != null && cts == null) await RunJoin(c.Parts, c.Output, File.Exists(c.Output) ? Math.Min(c.Written, new FileInfo(c.Output).Length) : 0); }
    void OnForgetResume(object s, RoutedEventArgs e) { Checkpoint.Clear(); RefreshResume(); }

    // ---------- APK ----------
    void SetApkInputs(List<string> inputs)
    {
        apkInputs = inputs;
        ApkInput.Text = inputs.Count == 1 ? inputs[0] : $"{inputs.Count} файлов: " + string.Join(", ", inputs.Select(Path.GetFileName));
        ApkResultButtons.Visibility = Visibility.Collapsed;
    }
    void OnPickBundle(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Split-APK (*.apks;*.xapk;*.apkm)|*.apks;*.xapk;*.apkm|Все файлы|*.*" };
        if (d.ShowDialog(this) == true) SetApkInputs([d.FileName]);
    }
    void OnPickApks(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Filter = "APK (*.apk)|*.apk" };
        if (d.ShowDialog(this) == true) SetApkInputs(d.FileNames.ToList());
    }
    void OnPickApkFolder(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) SetApkInputs([d.FolderName]);
    }
    void OnPickKeystore(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Хранилище ключей (*.jks;*.p12;*.pfx;*.keystore)|*.jks;*.p12;*.pfx;*.keystore|Все файлы|*.*" };
        if (d.ShowDialog(this) == true) KsPath.Text = d.FileName;
    }
    void OnRefreshAdb(object? s, RoutedEventArgs? e)
    {
        if (tools.Adb == null) { AdbText.Text = "adb не найден: установка по USB недоступна."; InstallCheck.IsEnabled = false; return; }
        var dev = ApkMerger.AdbDevices(tools);
        AdbText.Text = dev.Count == 0 ? "Телефон не подключён (включите «Отладку по USB» и подтвердите запрос на телефоне)." : "Подключено: " + string.Join(", ", dev);
        InstallCheck.IsEnabled = dev.Count > 0;
        if (dev.Count == 0) InstallCheck.IsChecked = false;
    }

    void Log(string line) => Dispatcher.BeginInvoke(() => { ApkLog.AppendText(line + "\n"); ApkLog.ScrollToEnd(); });

    async void OnMergeApk(object s, RoutedEventArgs e)
    {
        if (apkInputs.Count == 0) { MessageBox.Show(this, "Сначала выберите .apks, .xapk, .apkm, папку или несколько .apk.", Title); return; }
        var baseName = Path.GetFileNameWithoutExtension(apkInputs[0].TrimEnd('\\', '/'));
        var d = new SaveFileDialog { FileName = baseName + "-merged.apk", Filter = "APK (*.apk)|*.apk" };
        if (d.ShowDialog(this) != true) return;
        bool own = OwnKeyRadio.IsChecked == true;
        string ks = KsPath.Text, pass = KsPass.Password, alias = KsAlias.Text, kpass = KeyPass.Password;
        bool install = InstallCheck.IsChecked == true;
        if (!own)
        {
            if (!File.Exists(ks) || pass.Length == 0) { MessageBox.Show(this, "Укажите файл ключа и пароль.", Title); return; }
            Settings.Keystore = ks; Settings.KeyAlias = alias; Settings.Save();
        }
        ApkLog.Clear();
        StartProgress("Склейка APK: " + Path.GetFileName(d.FileName));
        Prog.IsIndeterminate = true;
        var output = d.FileName; var inputs = apkInputs.ToList(); var token = cts!.Token;
        try
        {
            var r = await Task.Run(() =>
            {
                var key = own ? SignKey.OwnKey(tools) : new SignKey(ks, pass, string.IsNullOrWhiteSpace(alias) ? null : alias, string.IsNullOrEmpty(kpass) ? null : kpass);
                return ApkMerger.Merge(inputs, output, key, tools, Log, token);
            });
            lastApk = r.Output;
            Log("");
            Log("✓ Готово: " + r.Output + " (" + Fmt.Size(new FileInfo(r.Output).Length) + ")");
            Log("Подпись v1+v2+v3, SHA-256 сертификата: " + r.CertSha256);
            foreach (var line in r.Verify.Split('\n').Where(l => l.StartsWith("Verified using"))) Log("  " + line.Trim());
            foreach (var n in r.Notes) Log("• " + n);
            ApkResultButtons.Visibility = Visibility.Visible;
            if (install) await InstallAsync(r.Output);
        }
        catch (OperationCanceledException) { Log("Отменено."); }
        catch (ApkMergeException ex) { Log("✗ " + ex.Message); MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { Log("✗ Ошибка: " + ex.Message); }
        finally { Prog.IsIndeterminate = false; StopProgress(); }
    }

    async Task InstallAsync(string apk)
    {
        var dev = ApkMerger.AdbDevices(tools);
        if (dev.Count == 0) { Log("Телефон не подключён."); return; }
        foreach (var serial in dev)
        {
            Log($"Установка на {serial}…");
            var r = await Task.Run(() => ApkMerger.AdbInstall(tools, serial, apk, Log));
            if (r.Output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
                Log("На телефоне стоит версия с другой подписью. Удалите приложение на телефоне и повторите.");
            Log(r.Code == 0 && r.Output.Contains("Success") ? "✓ Установлено" : "✗ Не установилось");
        }
    }
    void OnShowApk(object s, RoutedEventArgs e) { if (lastApk != null) ShowInFolder(lastApk); }
    async void OnInstallNow(object s, RoutedEventArgs e) { if (lastApk != null) await InstallAsync(lastApk); }

    // ---------- split ----------
    long SplitSize()
    {
        var i = SizeBox.SelectedIndex; if (i < 0) return 0;
        if (Presets[i].Size > 0) return Presets[i].Size;
        return double.TryParse(CustomMb.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? (long)(mb * (1 << 20)) : 0;
    }
    void OnSplitParamsChanged(object s, EventArgs e)
    {
        if (SplitInfo == null) return;
        CustomMb.IsEnabled = SizeBox.SelectedIndex == Presets.Length - 1;
        long ps = SplitSize();
        if (splitPath == null || ps <= 0) { SplitInfo.Text = ""; return; }
        int n = Streamer.PartCount(new FileInfo(splitPath).Length, ps);
        SplitInfo.Text = $"Получится {n} {Plural(n)}: {Path.GetFileName(splitPath)}.001 …";
    }
    void OnPickSplit(object s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog(); if (d.ShowDialog(this) != true) return;
        splitPath = d.FileName; SplitFile.Text = $"{Path.GetFileName(splitPath)} · {Fmt.Size(new FileInfo(splitPath).Length)}";
        OnSplitParamsChanged(this, EventArgs.Empty);
    }
    async void OnSplit(object s, RoutedEventArgs e)
    {
        long ps = SplitSize();
        if (splitPath == null || ps <= 0) { MessageBox.Show(this, "Выберите файл и размер части.", Title); return; }
        var d = new OpenFolderDialog { Title = "Куда положить части", InitialDirectory = Path.GetDirectoryName(splitPath) };
        if (d.ShowDialog(this) != true) return;
        long size = new FileInfo(splitPath).Length;
        if (!EnoughSpace(Path.Combine(d.FolderName, "x"), size)) return;
        var src = splitPath; var dir = d.FolderName; bool crc = CrcCheck.IsChecked == true;
        var sw = Stopwatch.StartNew();
        StartProgress("Разрезаю: " + Path.GetFileName(src));
        try
        {
            var token = cts!.Token;
            var (files, h) = await Task.Run(() => Streamer.Split(src, dir, ps, ProgressSink(sw, Streamer.PartCount(size, ps)), token));
            if (crc) File.WriteAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + ".crc"), ChecksumFiles.TcCrc(Path.GetFileName(src), size, h.Crc32));
            MessageBox.Show(this, $"Готово: {files.Count} {Plural(files.Count)} по {Fmt.Size(ps)}\nCRC32 {h.Crc32}\nSHA-256 {h.Sha256}", Title);
            ShowInFolder(files[0]);
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "Отменено, созданные части удалены.", Title); }
        catch (Exception ex) { MessageBox.Show(this, "Ошибка: " + ex.Message, Title); }
        finally { StopProgress(); }
    }

    // ---------- settings ----------
    void OnThemeChanged(object s, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem it) return;
        Settings.Theme = (string)it.Tag; Settings.Save();
#pragma warning disable WPF0001
        Application.Current.ThemeMode = Settings.Theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
    }
    void OnShellToggle(object s, RoutedEventArgs e)
    {
        try
        {
            if (ShellCheck.IsChecked == true) ShellIntegration.Register(Environment.ProcessPath!); else ShellIntegration.Unregister();
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось изменить меню проводника: " + ex.Message, Title); ShellCheck.IsChecked = ShellIntegration.IsRegistered; }
    }
    void OnDeleteToggle(object s, RoutedEventArgs e) { Settings.AllowDelete = DeleteCheck.IsChecked == true; Settings.Save(); }

    // ---------- helpers ----------
    void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть: " + ex.Message, Title); }
    }
    static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { ArgumentList = { "/select," + path } }); } catch { }
    }

    void Snap(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            var brush = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(brush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path); enc.Save(f);
    }
}
