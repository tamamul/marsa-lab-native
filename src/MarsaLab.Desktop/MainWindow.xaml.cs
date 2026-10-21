using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;

namespace MarsaLab.Desktop;

public partial class MainWindow : Window
{
    Api api = null!;
    int port = 5080;
    Process? server;
    bool startedServer;

    readonly ObservableCollection<PcItem> pcs = new();
    readonly Dictionary<string, PcItem> byId = new();
    readonly ObservableCollection<HistRow> hist = new();
    readonly ObservableCollection<GroupItem> groupItems = new();
    Dictionary<string, string[]> groups = new();
    ICollectionView? pcView;

    DispatcherTimer? tClients, tHist, tMon, tToast;
    bool monitor, dark = true, busyClients, busyHist, busyMon;
    DateTime monStart;
    string filter = "all";
    ViewWindow? view;

    // Berkas & Tugas
    readonly ObservableCollection<FileRow> shareRows = new();
    readonly ObservableCollection<SubRow> subRows = new();
    ICollectionView? subView;
    List<FileDto> lastFiles = new();
    List<SubDto> lastSubs = new();
    bool busyFiles;

    string GroupsFile => System.IO.Path.Combine(Util.DataDir, "groups.json");
    string ThemeFile => System.IO.Path.Combine(Util.DataDir, "theme.txt");

    public MainWindow()
    {
        InitializeComponent();
    }

    // ===================== Mulai & berhenti =====================
    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { dark = !(File.Exists(ThemeFile) && File.ReadAllText(ThemeFile).Trim() == "light"); } catch { }
        App.ApplyTheme(dark);
        ThemeBtn.Content = dark ? "☀   Tema terang" : "🌙   Tema gelap";

        PcList.ItemsSource = pcs;
        pcView = CollectionViewSource.GetDefaultView(pcs);
        pcView.Filter = o => Match((PcItem)o);
        HistList.ItemsSource = hist;
        GroupList.ItemsSource = groupItems;
        LoadGroups();
        ShareList.ItemsSource = shareRows;
        SubList.ItemsSource = subRows;
        subView = CollectionViewSource.GetDefaultView(subRows);
        subView.Filter = o => MatchSub((SubRow)o);

        var cfg = ReadConfig();
        if (cfg == null)
        {
            MessageBox.Show("File appsettings.json (bagian Lab → Token) tidak ditemukan di folder aplikasi.\nPasang lewat installer Guru atau letakkan file itu di folder yang sama dengan MarsaLab.Desktop.exe.",
                "MARSA Lab", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        port = cfg.Value.Port;
        var code = ReadLab("StudentCode") ?? "";
        StudentUrlText.Text = $"http://{LocalIp()}:{port}/siswa.html";
        StudentCodeText.Text = code.Length > 0 ? $"Kode kelas: {code}" : "Tanpa kode kelas — siapa pun di jaringan bisa mengunggah. Isi StudentCode di appsettings.json untuk membatasi.";
        api = new Api($"http://localhost:{port}/", cfg.Value.Token);

        SetConn(false, "Menghubungkan ke server...");
        if (!await api.HealthAsync())
        {
            StartServer();
            for (var i = 0; i < 30 && !await api.HealthAsync(); i++) await Task.Delay(500);
        }
        if (!await api.HealthAsync())
        {
            SetConn(false, "Server tidak aktif");
            MessageBox.Show("Server MARSA Lab tidak dapat dijalankan.\nPastikan MarsaLab.Admin.exe ada di folder yang sama.", "MARSA Lab", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        tClients = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        tClients.Tick += async (_, _) => await RefreshClients();
        tClients.Start();
        tHist = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        tHist.Tick += async (_, _) => { if (HistPanel.Visibility == Visibility.Visible) await RefreshHist(); if (FilesPanel.Visibility == Visibility.Visible) await RefreshFiles(); };
        tHist.Start();
        tMon = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        tMon.Tick += async (_, _) => await MonitorTick();
        tMon.Start();

        await RefreshClients();
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        tClients?.Stop(); tHist?.Stop(); tMon?.Stop();
        view?.Close();
        if (startedServer) { try { server?.Kill(true); } catch { } }
    }

    (string Token, int Port)? ReadConfig()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var lab = doc.RootElement.GetProperty("Lab");
            var token = lab.GetProperty("Token").GetString() ?? "";
            var p = lab.TryGetProperty("Port", out var pe) && pe.TryGetInt32(out var n) ? n : 5080;
            return token.Length == 0 ? null : (token, p);
        }
        catch { return null; }
    }

    void StartServer()
    {
        try
        {
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "MarsaLab.Admin.exe");
            if (!File.Exists(exe)) return;
            server = Process.Start(new ProcessStartInfo(exe, "--no-browser") { WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true, UseShellExecute = false });
            startedServer = true;
        }
        catch { }
    }

    void SetConn(bool ok, string text)
    {
        ConnDot.Fill = (Brush)FindResource(ok ? "Ok" : "Danger");
        ConnText.Text = text;
    }

    void Toast(string msg)
    {
        ToastText.Text = msg;
        ToastBox.Visibility = Visibility.Visible;
        tToast?.Stop();
        tToast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        tToast.Tick += (_, _) => { ToastBox.Visibility = Visibility.Collapsed; tToast?.Stop(); };
        tToast.Start();
    }

    // ===================== Navigasi & tema =====================
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (DashPanel == null || HistPanel == null || FilesPanel == null) return;
        DashPanel.Visibility = NavDash.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistPanel.Visibility = NavHist.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = NavFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavHist.IsChecked == true) _ = RefreshHist();
        if (NavFiles.IsChecked == true) _ = RefreshFiles();
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        dark = !dark;
        App.ApplyTheme(dark);
        ThemeBtn.Content = dark ? "☀   Tema terang" : "🌙   Tema gelap";
        try { File.WriteAllText(ThemeFile, dark ? "dark" : "light"); } catch { }
    }

    // ===================== Data komputer =====================
    async Task RefreshClients()
    {
        if (busyClients || api == null) return;
        busyClients = true;
        try
        {
            var list = await api.ClientsAsync();
            SetConn(true, "Server terhubung");
            Merge(list);
        }
        catch (ApiException ex) when (ex.Status == 401) { SetConn(false, "Token tidak cocok"); }
        catch { SetConn(false, "Server tidak terhubung"); }
        finally { busyClients = false; }
    }

    void Merge(List<ClientDto> list)
    {
        var seen = new HashSet<string>();
        foreach (var c in list)
        {
            seen.Add(c.ClientId);
            if (!byId.TryGetValue(c.ClientId, out var p))
            {
                p = new PcItem(c.ClientId);
                p.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(PcItem.IsSelected)) UpdateSel(); };
                byId[c.ClientId] = p;
                pcs.Add(p);
            }
            p.Name = c.ComputerName;
            p.Ip = c.IpAddress;
            p.IsOnline = c.Status == "Online";
            p.SubText = p.IsOnline ? "Online" : "Terakhir " + Util.Ago(c.LastSeenUtc);
            if (!p.IsOnline) { p.IsSelected = false; if (p.Thumb != null) p.Thumb = null; p.Placeholder = ""; }
        }
        foreach (var gone in byId.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            pcs.Remove(byId[gone]);
            byId.Remove(gone);
        }
        var on = pcs.Count(p => p.IsOnline);
        TotalText.Text = pcs.Count.ToString();
        OnlineText.Text = on.ToString();
        OfflineText.Text = (pcs.Count - on).ToString();
        pcView?.Refresh();
        EmptyText.Visibility = pcs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSel();
        UpdateMonText();
    }

    bool Match(PcItem p)
    {
        if (filter == "online" && !p.IsOnline) return false;
        if (filter == "offline" && p.IsOnline) return false;
        var q = SearchBox?.Text.Trim() ?? "";
        return q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Ip.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (pcView == null) return;
        filter = (string)((FrameworkElement)sender).Tag;
        pcView.Refresh();
    }

    void Search_Changed(object sender, TextChangedEventArgs e) => pcView?.Refresh();

    void UpdateSel()
    {
        var sel = pcs.Count(p => p.IsSelected && p.IsOnline);
        var on = pcs.Count(p => p.IsOnline);
        SelText.Text = sel > 0 ? $"{sel} komputer dipilih" : $"Semua komputer online ({on})";
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PcItem p && p.IsOnline) p.IsSelected = !p.IsSelected;
    }

    void Lihat_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PcItem p) return;
        if (view != null) { view.Close(); }
        view = new ViewWindow(api, p.ClientId, p.Name) { Owner = this };
        view.Closed += (_, _) => view = null;
        view.Show();
    }

    void SelectOnline_Click(object sender, RoutedEventArgs e) { foreach (var p in pcs) p.IsSelected = p.IsOnline; }
    void ClearSel_Click(object sender, RoutedEventArgs e) { foreach (var p in pcs) p.IsSelected = false; }

    // ===================== Kelompok =====================
    void LoadGroups()
    {
        try { if (File.Exists(GroupsFile)) groups = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(GroupsFile)) ?? new(); } catch { groups = new(); }
        RebuildGroups();
    }

    void RebuildGroups()
    {
        groupItems.Clear();
        foreach (var kv in groups) groupItems.Add(new GroupItem(kv.Key, kv.Value.Length));
        GroupHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        try { File.WriteAllText(GroupsFile, JsonSerializer.Serialize(groups)); } catch { }
    }

    void SaveGroup_Click(object sender, RoutedEventArgs e)
    {
        var ids = pcs.Where(p => p.IsSelected).Select(p => p.ClientId).ToArray();
        if (ids.Length == 0) { Toast("Klik komputer dulu untuk dipilih."); return; }
        var name = PromptWindow.Ask(this, "Simpan kelompok", "Nama kelompok (contoh: Baris A):");
        if (name == null) return;
        groups[name] = ids;
        RebuildGroups();
        Toast($"Kelompok \"{name}\" disimpan.");
    }

    void Group_Click(object sender, RoutedEventArgs e)
    {
        var name = (string)((FrameworkElement)sender).Tag;
        if (!groups.TryGetValue(name, out var ids)) return;
        foreach (var p in pcs) p.IsSelected = p.IsOnline && ids.Contains(p.ClientId);
    }

    void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        var g = ((FrameworkElement)sender).DataContext as GroupItem;
        if (g == null && sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement t) g = t.DataContext as GroupItem;
        if (g == null) return;
        if (MessageBox.Show($"Hapus kelompok \"{g.Name}\"?", "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        groups.Remove(g.Name);
        RebuildGroups();
    }

    // ===================== Perintah =====================
    void Template_Click(object sender, RoutedEventArgs e) => MsgBox.Text = (string)((Button)sender).Content;

    async void Cmd_Click(object sender, RoutedEventArgs e) => await SendCmd((string)((FrameworkElement)sender).Tag);

    async Task SendCmd(string type)
    {
        var msg = MsgBox.Text.Trim();
        if (type == "announce" && msg.Length == 0) { Toast("Isi pesan pengumuman dahulu."); return; }
        if (type == "open-url" && !(Uri.TryCreate(msg, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        { Toast("Isi alamat website (http:// atau https://) di kotak pesan."); return; }

        var picked = pcs.Where(p => p.IsSelected).ToList();
        var ids = picked.Where(p => p.IsOnline).Select(p => p.ClientId).ToArray();
        if (picked.Count > 0 && ids.Length == 0) { Toast("Komputer yang dipilih sedang offline."); return; }
        var online = pcs.Count(p => p.IsOnline);
        var scope = ids.Length > 0 ? $"{ids.Length} komputer terpilih" : $"SEMUA komputer online ({online})";
        if (MessageBox.Show($"{Util.Label(type)} → {scope}?", "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            var n = await api.SendAsync(type, ids.Length > 0 ? ids : null, msg);
            Toast($"Perintah {Util.Label(type)} masuk antrean untuk {n} komputer.");
            if (HistPanel.Visibility == Visibility.Visible) await RefreshHist();
        }
        catch (Exception ex) { Toast("Gagal: " + ex.Message); }
    }

    async Task RefreshHist()
    {
        if (busyHist || api == null) return;
        busyHist = true;
        try
        {
            var list = await api.RecentAsync();
            hist.Clear();
            foreach (var c in list) hist.Add(new HistRow(c, byId.TryGetValue(c.ClientId, out var p) ? p.Name : c.ClientId));
        }
        catch { }
        finally { busyHist = false; }
    }

    // ===================== Pantau layar =====================
    void Mon_Changed(object sender, RoutedEventArgs e)
    {
        if (MonBar == null) return;
        monitor = MonBtn.IsChecked == true;
        monStart = DateTime.Now;
        MonBar.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        if (monitor) Toast("Memantau dimulai. Gambar pertama muncul dalam ±5 detik, mohon tunggu.");
        else foreach (var p in pcs) { p.Thumb = null; p.Placeholder = ""; }
        UpdateMonText();
        _ = MonitorTick();
    }

    async Task MonitorTick()
    {
        if (!monitor || busyMon || api == null) return;
        busyMon = true;
        try
        {
            var viewing = view?.ClientId;
            var ids = pcs.Where(p => p.IsOnline && p.ClientId != viewing).Select(p => p.ClientId).ToArray();
            if (ids.Length > 0) await api.WatchAsync(ids, "thumb", 15);
            ApplyShots(await api.ScreensAsync());
        }
        catch { }
        finally { busyMon = false; }
    }

    void ApplyShots(Dictionary<string, ShotDto> shots)
    {
        var waiting = (DateTime.Now - monStart).TotalSeconds < 15;
        foreach (var p in pcs)
        {
            if (!monitor || !p.IsOnline) continue;
            if (shots.TryGetValue(p.ClientId, out var s) && (DateTimeOffset.UtcNow - s.T).TotalSeconds < 20)
            {
                if (s.T != p.LastShot) { p.Thumb = Util.Decode(s.Jpg); p.LastShot = s.T; }
                p.Placeholder = "";
            }
            else
            {
                p.Thumb = null;
                p.Placeholder = waiting ? "Menunggu gambar..." : "Tidak ada gambar (layar terkunci / client belum versi baru)";
            }
        }
        UpdateMonText();
    }

    void UpdateMonText()
    {
        if (!monitor || MonText == null) return;
        var on = pcs.Count(p => p.IsOnline);
        var fresh = pcs.Count(p => p.IsOnline && p.Thumb != null);
        MonProg.Visibility = fresh < on && (DateTime.Now - monStart).TotalSeconds < 15 ? Visibility.Visible : Visibility.Collapsed;
        MonText.Text = $"Memantau {on} PC online · {fresh} sudah menampilkan gambar · diperbarui tiap ±3 detik";
    }

    // ===================== Berkas & Tugas =====================
    string? ReadLab(string key)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.GetProperty("Lab").TryGetProperty(key, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        catch { return null; }
    }

    static string LocalIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address.ToString();
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !ip.StartsWith("169.254")) return ip;
                }
            }
        }
        catch { }
        return "localhost";
    }

    void SetBusy(bool on, string text)
    {
        FilesBusy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FilesBusyText.Text = text;
    }

    async Task RefreshFiles()
    {
        if (busyFiles || api == null) return;
        busyFiles = true;
        try
        {
            var files = await api.FilesAsync();
            var subs = await api.SubmissionsAsync();
            if (!files.SequenceEqual(lastFiles))
            {
                lastFiles = files;
                shareRows.Clear();
                foreach (var f in files) shareRows.Add(new FileRow(f));
                ShareEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (!subs.SequenceEqual(lastSubs))
            {
                lastSubs = subs;
                subRows.Clear();
                foreach (var s in subs) subRows.Add(new SubRow(s));
                SubEmpty.Visibility = subs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SubInfo.Text = subs.Count == 0 ? "" : $"{subs.Count} file dari {subs.Select(s => s.Student).Distinct().Count()} siswa";
                subView?.Refresh();
            }
        }
        catch { }
        finally { busyFiles = false; }
    }

    bool MatchSub(SubRow r)
    {
        var q = SubSearch?.Text.Trim() ?? "";
        return q.Length == 0 || r.Student.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void SubSearch_Changed(object sender, TextChangedEventArgs e) => subView?.Refresh();

    void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(StudentUrlText.Text); Toast("Alamat disalin. Bagikan ke siswa."); }
        catch { Toast("Gagal menyalin alamat."); }
    }

    async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Pilih file untuk dibagikan" };
        if (dlg.ShowDialog(this) == true) await UploadFiles(dlg.FileNames);
    }

    async void Share_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await UploadFiles(paths);
    }

    async Task UploadFiles(string[] paths)
    {
        paths = paths.Where(File.Exists).ToArray();
        if (paths.Length == 0) return;
        var maxMb = int.TryParse(ReadLab("MaxUploadMB"), out var m) ? m : 50;
        var ok = 0;
        string? err = null;
        for (var i = 0; i < paths.Length; i++)
        {
            var name = System.IO.Path.GetFileName(paths[i]);
            if (new FileInfo(paths[i]).Length > maxMb * 1024L * 1024L) { err = $"{name} lebih dari {maxMb} MB"; continue; }
            SetBusy(true, $"Mengunggah {name} ({i + 1}/{paths.Length})...");
            try { await api.UploadAsync(paths[i]); ok++; }
            catch (Exception ex) { err = $"{name}: {ex.Message}"; }
        }
        SetBusy(false, "");
        Toast(err == null ? $"{ok} file dibagikan." : $"{ok} file berhasil. Gagal: {err}");
        await RefreshFiles();
    }

    async void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FileRow r) return;
        if (MessageBox.Show($"Hapus \"{r.Name}\" dari daftar berbagi?", "Konfirmasi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await api.DeleteFileAsync(r.Name); Toast("File dihapus."); await RefreshFiles(); }
        catch (Exception ex) { Toast("Gagal: " + ex.Message); }
    }

    async void DownloadSub_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SubRow r) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = r.Name, Title = "Simpan tugas " + r.Student };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Mengunduh " + r.Name + "...");
            await api.DownloadSubmissionAsync(r.Student, r.Name, dlg.FileName);
            Toast("Tersimpan: " + dlg.FileName);
        }
        catch (Exception ex) { Toast("Gagal: " + ex.Message); }
        finally { SetBusy(false, ""); }
    }

    async void Zip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"tugas-{DateTime.Now:yyyyMMdd-HHmm}.zip", Filter = "File ZIP|*.zip", Title = "Simpan semua tugas" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Menyiapkan ZIP semua tugas...");
            await api.DownloadZipAsync(dlg.FileName);
            Toast("ZIP tersimpan: " + dlg.FileName);
        }
        catch (Exception ex) { Toast("Gagal: " + ex.Message); }
        finally { SetBusy(false, ""); }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarsaLab", "Admin", "Submissions");
        if (!Directory.Exists(dir)) { Toast("Folder tugas belum ada di PC ini."); return; }
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
    }
}
