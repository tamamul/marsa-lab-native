using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;


/// <summary>
/// Menyambung KELUAR ke gerbang (mis. di server Lubuntu) lewat WebSocket, lalu meneruskan
/// permintaan monitor/kontrol dari gerbang ke server ini (localhost). Tidak butuh port masuk.
/// Aktif hanya jika Relay:Url, Relay:LabId, dan Relay:Key diisi di appsettings.json.
/// </summary>
public sealed class RelayConnector : BackgroundService
{
    readonly string url, labId, key, adminToken;
    readonly HttpClient local;

    // Daftar putih jalur: hanya monitor dan kontrol. Berkas/tugas TIDAK pernah diteruskan.
    // Segmen id wajib diawali huruf/angka/_/- (menolak ".." dan "%2e%2e").
    const string Id = @"[A-Za-z0-9_-][A-Za-z0-9._%~-]{0,79}";
    static readonly Regex GetOk = new(@"^/api/(health|clients|summary|commands/recent|screens)\z|^/api/screens/" + Id + @"\z", RegexOptions.Compiled);
    static readonly Regex PostOk = new(@"^/api/(commands|watch)\z|^/api/control/" + Id + @"\z", RegexOptions.Compiled);

    public RelayConnector(IConfiguration cfg)
    {
        url = (cfg["Relay:Url"] ?? "").Trim().TrimEnd('/');
        labId = (cfg["Relay:LabId"] ?? "").Trim();
        key = (cfg["Relay:Key"] ?? "").Trim();
        adminToken = cfg["Lab:Token"] ?? "";
        var port = cfg.GetValue("Lab:Port", 5080);
        local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (url.Length == 0 || labId.Length == 0 || key.Length == 0) return;
        var wsBase = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + url[8..]
                   : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + url[7..] : url;
        var wsUrl = new Uri(wsBase + "/agent/ws");
        var delay = 2;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("X-Lab-Id", labId);
                ws.Options.SetRequestHeader("X-Lab-Key", key);
                await ws.ConnectAsync(wsUrl, stop);
                Console.WriteLine($"[Relay] Tersambung ke gerbang {wsUrl.Host} sebagai '{labId}'.");
                delay = 2;
                await Pump(ws, stop);
                Console.WriteLine("[Relay] Koneksi ditutup gerbang.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine($"[Relay] Tidak tersambung: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), stop); } catch { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }

    async Task Pump(ClientWebSocket ws, CancellationToken stop)
    {
        var sendLock = new SemaphoreSlim(1, 1);
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), stop);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 2_000_000) return; // permintaan normal jauh lebih kecil
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Binary) continue;

            var data = ms.ToArray();
            _ = Task.Run(async () =>
            {
                var reply = await Handle(data);
                await sendLock.WaitAsync();
                try { if (ws.State == WebSocketState.Open) await ws.SendAsync(new ArraySegment<byte>(reply), WebSocketMessageType.Binary, true, CancellationToken.None); }
                catch { }
                finally { sendLock.Release(); }
            });
        }
    }

    async Task<byte[]> Handle(byte[] data)
    {
        var id = "";
        try
        {
            var (h, body) = Unpack(data);
            id = h.GetProperty("id").GetString() ?? "";
            var method = h.GetProperty("method").GetString() ?? "GET";
            var path = h.GetProperty("path").GetString() ?? "";
            var query = h.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var ct = h.TryGetProperty("ct", out var c) ? c.GetString() : null;

            var allowed = method == "GET" ? GetOk.IsMatch(path) : method == "POST" && PostOk.IsMatch(path);
            if (!allowed || query.Any(ch => ch < 32)) return Pack(id, 403, "{\"error\":\"Jalur tidak diizinkan.\"}");

            using var req = new HttpRequestMessage(new HttpMethod(method), path + query);
            req.Headers.Add("X-Marsa-Token", adminToken);
            if (method == "POST")
            {
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct ?? "application/json");
            }
            using var res = await local.SendAsync(req);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            return Pack(id, (int)res.StatusCode, bytes, res.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return Pack(id, 502, JsonSerializer.Serialize(new { error = "Server guru gagal memproses: " + ex.Message }));
        }
    }

    static byte[] Pack(string id, int status, string body) => Pack(id, status, System.Text.Encoding.UTF8.GetBytes(body));
    static byte[] Pack(string id, int status, byte[] body, string ct = "application/json")
    {
        var h = JsonSerializer.SerializeToUtf8Bytes(new { id, status, ct });
        var buf = new byte[4 + h.Length + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, h.Length);
        h.CopyTo(buf, 4);
        body.CopyTo(buf, 4 + h.Length);
        return buf;
    }

    static (JsonElement Header, byte[] Body) Unpack(byte[] data)
    {
        var hl = BinaryPrimitives.ReadInt32BigEndian(data);
        if (hl < 0 || hl > data.Length - 4) throw new InvalidDataException("frame rusak");
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, 4, hl));
        return (doc.RootElement.Clone(), data[(4 + hl)..]);
    }
}
