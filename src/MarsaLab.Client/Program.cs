using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(configPath)) { Console.WriteLine($"Konfigurasi tidak ditemukan: {configPath}"); return; }
var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
if (config is null || string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.Token) || config.Token == "GANTI_TOKEN_LAB_INI") { Console.WriteLine("Atur ServerUrl dan Token di appsettings.json."); return; }
var clientId = string.IsNullOrWhiteSpace(config.ClientId) ? Environment.MachineName : config.ClientId.Trim();
using var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
http.DefaultRequestHeaders.Add("X-Marsa-Token", config.Token);
var delay = Math.Clamp(config.HeartbeatSeconds, 3, 30);
Console.WriteLine($"MARSA Lab Client: {Environment.MachineName} ({clientId})"); Console.WriteLine($"Server: {config.ServerUrl}");
Console.WriteLine("Kontrol yang didukung: pengumuman, kunci layar, restart/shutdown dengan jeda 60 detik.");
Console.WriteLine("Jalankan di sesi pengguna Windows agar fitur kunci layar dan pesan tampil.");
using var stop = new CancellationTokenSource();
if (OperatingSystem.IsWindows()) Native.SetProcessDPIAware();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
while (!stop.IsCancellationRequested)
{
    try
    {
        using var heartbeat = await http.PostAsJsonAsync("api/heartbeat", new HeartbeatRequest(clientId, Environment.MachineName), stop.Token);
        if (!heartbeat.IsSuccessStatusCode) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Heartbeat ditolak: {(int)heartbeat.StatusCode}");
		else if (OperatingSystem.IsWindows())
{
    var hb = await heartbeat.Content.ReadFromJsonAsync<HeartbeatReply>(cancellationToken: stop.Token);
    if (hb?.Watch is { } wm && Interlocked.CompareExchange(ref ScreenState.Streaming, 1, 0) == 0)
        _ = Task.Run(() => StreamScreen(http, clientId, wm, stop.Token));
}
        using var response = await http.GetAsync($"api/commands/next?clientId={Uri.EscapeDataString(clientId)}", stop.Token);
        if (response.IsSuccessStatusCode)
        {
            var list = await response.Content.ReadFromJsonAsync<List<LabCommand>>(cancellationToken: stop.Token) ?? [];
            foreach (var command in list) await ExecuteCommand(command, http, clientId, stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Server belum terjangkau: {ex.Message}"); }
    try { await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token); } catch (OperationCanceledException) { break; }
}

static async Task ExecuteCommand(LabCommand command, HttpClient http, string clientId, CancellationToken ct)
{
    var success = false; string result;
    try
    {
        switch (command.Type)
        {
            case "announce":
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
                var title = "MARSA Lab - Pengumuman";
                var safeMessage = command.Message.Replace("\r", " ").Replace("\n", " ");
                // Windows built-in msg.exe displays a visible message to the signed-in user/session.
                var msg = RunProcess("msg.exe", $"* /time:60 {Quote($"{title}: {safeMessage}")}");
                if (msg.ExitCode != 0) throw new InvalidOperationException(msg.Output);
                result = "Pengumuman ditampilkan."; success = true; break;
            case "lock":
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
                if (!TryLockWorkStation()) throw new InvalidOperationException("Windows gagal mengirim perintah kunci layar. Pastikan client berjalan pada sesi pengguna aktif.");
                result = "Perintah kunci layar dikirim."; success = true; break;
            case "restart":
            case "shutdown":
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
                var action = command.Type == "restart" ? "/r" : "/s";
                var note = string.IsNullOrWhiteSpace(command.Message) ? "Perintah admin MARSA Lab" : command.Message.Replace("\r", " ").Replace("\n", " ");
                var shut = RunProcess("shutdown.exe", $"{action} /t 60 /c {Quote(note)}");
                if (shut.ExitCode != 0) throw new InvalidOperationException(shut.Output);
                result = "Dijadwalkan 60 detik lagi; pengguna mendapat peringatan Windows."; success = true; break;
            case "cancel-shutdown":
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
                var cancel = RunProcess("shutdown.exe", "/a");
                if (cancel.ExitCode != 0) throw new InvalidOperationException(cancel.Output);
                result = "Shutdown/restart terjadwal dibatalkan."; success = true; break;
            case "open-share":
   		 if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
		    var shareUrl = new Uri(http.BaseAddress!, "siswa.html").ToString();
 		   Process.Start(new ProcessStartInfo(shareUrl) { UseShellExecute = true });
		    result = "Halaman tugas dibuka di browser."; success = true; break;
case "open-url":
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Fitur ini hanya untuk Windows.");
    var okUrl = Uri.TryCreate(command.Message, UriKind.Absolute, out var openUrl);
    if (!okUrl || (openUrl!.Scheme != Uri.UriSchemeHttp && openUrl.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException("Alamat tidak valid.");
    Process.Start(new ProcessStartInfo(openUrl.AbsoluteUri) { UseShellExecute = true });
    result = "Website dibuka: " + openUrl.Host; success = true; break;    
            default: throw new InvalidOperationException("Perintah tidak diizinkan.");
        }
    }
    catch (Exception ex) { result = ex.Message; }
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {command.Type}: {(success ? "OK" : "GAGAL")} - {result}");
    try { using var ack = await http.PostAsJsonAsync($"api/commands/{command.Id}/result", new CommandResultRequest(clientId, success, result), ct); }
    catch (Exception ex) { Console.WriteLine($"Gagal mengirim hasil perintah: {ex.Message}"); }
}
static (int ExitCode, string Output) RunProcess(string file, string args)
{
    using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    p.Start(); var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(10000); return (p.HasExited ? p.ExitCode : -1, output.Trim());
}
static string Quote(string s) => "\"" + s.Replace("\"", "'") + "\"";

[SupportedOSPlatform("windows")]
static async Task StreamScreen(HttpClient http, string clientId, string mode, CancellationToken ct)
{
    var fails = 0;
    try
    {
        while (!ct.IsCancellationRequested && (mode == "thumb" || mode == "full"))
        {
            var full = mode == "full";
			var gotInput = false;
            try
            {
                var (jpeg, w, h) = CaptureJpeg(full ? 1280 : 320, full ? 70L : 55L);
                using var content = new ByteArrayContent(jpeg);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                using var res = await http.PostAsync($"api/screens/{Uri.EscapeDataString(clientId)}?w={w}&h={h}&m={mode}", content, ct);
                if (!res.IsSuccessStatusCode) break;
                var reply = await res.Content.ReadFromJsonAsync<WatchReply>(cancellationToken: ct);
				mode = reply?.Watch ?? "";
				if (reply?.Input is { Length: > 0 } inputs) { ApplyInput(inputs); gotInput = true; }
                fails = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { if (++fails > 5) break; }
            await Task.Delay(gotInput ? 250 : full ? 700 : 2500, ct);
        }
    }
    catch { }
    finally { Volatile.Write(ref ScreenState.Streaming, 0); }
}
[SupportedOSPlatform("windows")]
static (byte[] Jpeg, int W, int H) CaptureJpeg(int maxWidth, long quality)
{
    int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
    using var shot = new Bitmap(sw, sh);
    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(0, 0, 0, 0, new Size(sw, sh));
    var tw = Math.Min(maxWidth, sw); var th = (int)((long)sh * tw / sw);
    using var small = new Bitmap(tw, th);
    using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(shot, 0, 0, tw, th); }
    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    using var ep = new EncoderParameters(1);
    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
    using var ms = new MemoryStream();
    small.Save(ms, codec, ep);
    return (ms.ToArray(), tw, th);
}

[SupportedOSPlatform("windows")]
static void ApplyInput(InputEvent[] events)
{
    static int Abs(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    foreach (var ev in events.Take(60))
    {
        switch (ev.T)
        {
            case "move":
                Native.Send(Native.M(0x8001, Abs(ev.X), Abs(ev.Y))); break;
            case "down":
            case "up":
            {
                var down = ev.T == "down";
                uint f = ev.B == "right" ? (down ? 0x8u : 0x10u) : ev.B == "middle" ? (down ? 0x20u : 0x40u) : (down ? 0x2u : 0x4u);
                Native.Send(Native.M(0x8001, Abs(ev.X), Abs(ev.Y)), Native.M(f));
                break;
            }
            case "wheel":
                Native.Send(Native.M(0x8001, Abs(ev.X), Abs(ev.Y)), Native.M(0x0800, 0, 0, unchecked((uint)ev.D))); break;
            case "text":
                foreach (var ch in (ev.S ?? "").Take(32)) Native.Send(Native.K(0, ch, 0x4), Native.K(0, ch, 0x6));
                break;
            case "key":
            {
                if (ev.Vk < 0x08 || ev.Vk > 0xFE || ev.Vk is 0x5B or 0x5C or 0x5F) break;
                var mods = new List<ushort>();
                foreach (var m in ev.Mods ?? [])
                {
                    var mv = m == "ctrl" ? (ushort)0x11 : m == "shift" ? (ushort)0x10 : m == "alt" ? (ushort)0x12 : (ushort)0;
                    if (mv != 0 && !mods.Contains(mv)) mods.Add(mv);
                }
                var seq = new List<Native.INPUT>();
                foreach (var mv in mods) seq.Add(Native.K(mv, 0, 0));
                var ext = (ev.Vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E) ? 0x1u : 0u;
                seq.Add(Native.K((ushort)ev.Vk, 0, ext)); seq.Add(Native.K((ushort)ev.Vk, 0, ext | 0x2));
                for (var i = mods.Count - 1; i >= 0; i--) seq.Add(Native.K(mods[i], 0, 0x2));
                Native.Send(seq.ToArray());
                break;
            }
        }
    }
}

static bool TryLockWorkStation()
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = false, CreateNoWindow = true });
        return p is not null;
    }
    catch { return false; }
}
record ClientConfig(string ServerUrl, string Token, string ClientId, int HeartbeatSeconds);
record HeartbeatRequest(string ClientId, string ComputerName);
record LabCommand(string Id, string ClientId, string Type, string Message, DateTimeOffset CreatedUtc, string Status, string Result);
record CommandResultRequest(string ClientId, bool Success, string Result);
record HeartbeatReply(bool Ok, string? Watch);
record InputEvent(string T, double X, double Y, string? B, int Vk, string? S, string[]? Mods, int D);
record WatchReply(string? Watch, InputEvent[]? Input);
static class ScreenState { public static int Streaming; }
static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    public static INPUT M(uint flags, int dx = 0, int dy = 0, uint data = 0) => new() { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };
    public static INPUT K(ushort vk, ushort scan, uint flags) => new() { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };
    public static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
