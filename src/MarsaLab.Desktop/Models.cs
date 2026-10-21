using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.IO;

namespace MarsaLab.Desktop;

public class PcItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    void RaiseThumb() { Raise(nameof(HasThumb)); Raise(nameof(ShowPlaceholder)); Raise(nameof(ShowIcon)); }

    public string ClientId { get; }
    public PcItem(string clientId) { ClientId = clientId; }

    string name = "", ip = "", sub = "", placeholder = "";
    bool online, selected;
    BitmapSource? thumb;
    public DateTimeOffset LastShot { get; set; }

    public string Name { get => name; set { if (name != value) { name = value; Raise(); } } }
    public string Ip { get => ip; set { if (ip != value) { ip = value; Raise(); } } }
    public string SubText { get => sub; set { if (sub != value) { sub = value; Raise(); } } }
    public bool IsOnline { get => online; set { if (online != value) { online = value; Raise(); } } }
    public bool IsSelected { get => selected; set { if (selected != value) { selected = value; Raise(); } } }
    public BitmapSource? Thumb { get => thumb; set { thumb = value; Raise(); RaiseThumb(); } }
    public string Placeholder { get => placeholder; set { if (placeholder != value) { placeholder = value; Raise(); RaiseThumb(); } } }

    public bool HasThumb => thumb != null;
    public bool ShowPlaceholder => thumb == null && placeholder.Length > 0;
    public bool ShowIcon => thumb == null && placeholder.Length == 0;
}

// ===== Berkas & Tugas =====
public class FileRow
{
    public FileDto D { get; }
    public FileRow(FileDto d) { D = d; }
    public string Name => D.Name;
    public string Meta => Util.Size(D.Size) + "  ·  " + D.ModifiedUtc.ToLocalTime().ToString("dd MMM HH:mm");
}

public class SubRow
{
    public SubDto D { get; }
    public SubRow(SubDto d) { D = d; }
    public string Student => D.Student;
    public string Name => D.Name;
    public string Size => Util.Size(D.Size);
    public string Time => D.ModifiedUtc.ToLocalTime().ToString("dd MMM HH:mm");
}

public record GroupItem(string Name, int Count) { public string Label => $"{Name} ({Count})"; }

public class HistRow
{
    public string Time { get; }
    public string Pc { get; }
    public string Type { get; }
    public string Status { get; }
    public string Result { get; }
    public string Kind { get; }
    public HistRow(CommandDto c, string pcName)
    {
        Time = c.CreatedUtc.ToLocalTime().ToString("dd MMM HH:mm:ss");
        Pc = pcName;
        Type = Util.Label(c.Type);
        Status = c.Status;
        Result = string.IsNullOrWhiteSpace(c.Result) ? (string.IsNullOrWhiteSpace(c.Message) ? "—" : c.Message!) : c.Result!;
        var s = c.Status.ToLowerInvariant();
        Kind = s.Contains("succ") || s.Contains("done") || s.Contains("selesai") ? "ok" : s.Contains("fail") || s.Contains("gagal") || s.Contains("error") ? "fail" : "wait";
    }
}

public static class Util
{
    static readonly Dictionary<string, string> Labels = new()
    {
        ["announce"] = "Pengumuman", ["lock"] = "Kunci layar", ["restart"] = "Restart", ["shutdown"] = "Matikan",
        ["cancel-shutdown"] = "Batalkan daya", ["open-share"] = "Buka halaman tugas", ["open-url"] = "Buka website",
    };
    public static string Label(string t) => Labels.TryGetValue(t, out var v) ? v : t;

    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarsaLab");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string Size(long n) =>
        n >= 1048576 ? (n / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, (n + 512) / 1024) + " KB";

    public static string Ago(DateTimeOffset t)
    {
        var s = Math.Max(0, (DateTimeOffset.UtcNow - t).TotalSeconds);
        return s < 60 ? Math.Round(s) + " dtk lalu" : s < 3600 ? Math.Round(s / 60) + " mnt lalu"
             : s < 86400 ? Math.Round(s / 3600) + " jam lalu" : Math.Round(s / 86400) + " hari lalu";
    }

    public static BitmapSource? Decode(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }
}
