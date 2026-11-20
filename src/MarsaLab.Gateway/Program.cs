using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ===== Perintah bantu =====
//   MarsaLab.Gateway newkey          -> membuat kunci lab acak
//   MarsaLab.Gateway hash "sandi"    -> membuat hash sandi untuk gateway.json
if (args.Length >= 1 && args[0] == "newkey")
{
    var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace("+", "").Replace("/", "").Replace("=", "");
    Console.WriteLine(raw[..40]);
    return;
}
if (args.Length >= 2 && args[0] == "hash")
{
    if (args[1].Length < 10) Console.Error.WriteLine("PERINGATAN: sandi sebaiknya minimal 10 karakter.");
    Console.WriteLine(Auth.MakeHash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "gateway.json"), optional: false, reloadOnChange: true);
builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration.GetValue("Port", 5090)}");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var agents = new ConcurrentDictionary<string, Agent>();
var sessions = new ConcurrentDictionary<string, Session>();
var fails = new ConcurrentDictionary<string, Fail>();
var lastControlLog = new ConcurrentDictionary<string, DateTime>();
var auditLock = new object();
var labIdRx = new Regex(@"^[a-z0-9-]{1,32}\z");
var segRx = new Regex(@"^[A-Za-z0-9_-][A-Za-z0-9._%~ -]{0,79}\z");
var dummyHash = Auth.MakeHash("dummy");

// ===== Halaman uji sederhana =====
app.MapGet("/", () => Results.Content(Pages.Index, "text/html; charset=utf-8"));
app.MapGet("/healthz", () => Results.Text("ok"));

// ===== Login =====
app.MapPost("/auth/login", (HttpContext ctx, LoginReq? req) =>
{
    var ip = Ip(ctx);
    if (fails.TryGetValue(ip, out var f) && f.Until > DateTime.UtcNow)
        return Results.Json(new { error = "Terlalu banyak percobaan. Coba lagi beberapa menit lagi." }, statusCode: 429);

    var user = Users().FirstOrDefault(u => string.Equals(u.Name, req?.Username, StringComparison.OrdinalIgnoreCase));
    var passOk = Auth.Verify(req?.Password ?? "", user?.Hash ?? dummyHash) && user != null;
    if (!passOk)
    {
        fails.AddOrUpdate(ip, new Fail(1, DateTime.MinValue),
            (_, o) => o.Count + 1 >= 5 ? new Fail(0, DateTime.UtcNow.AddMinutes(5)) : new Fail(o.Count + 1, o.Until));
        Audit(req?.Username ?? "?", "-", "-", "LOGIN", "gagal dari " + ip, 401, null);
        return Results.Json(new { error = "Nama pengguna atau kata sandi salah." }, statusCode: 401);
    }
    fails.TryRemove(ip, out _);
    foreach (var kv in sessions.Where(kv => kv.Value.Expires < DateTime.UtcNow).ToList()) sessions.TryRemove(kv.Key, out _);
    var role = user!.Role == "controller" ? "controller" : "viewer";
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    sessions[token] = new Session(user.Name, role, DateTime.UtcNow.AddHours(8));
    Audit(user.Name, role, "-", "LOGIN", "berhasil dari " + ip, 200, null);
    return Results.Json(new { token, role, user = user.Name, hours = 8 });
});

app.MapPost("/auth/logout", (HttpContext ctx) =>
{
    var t = Token(ctx);
    if (t.Length > 0) sessions.TryRemove(t, out _);
    return Results.Json(new { ok = true });
});

app.MapGet("/auth/labs", (HttpContext ctx) =>
{
    if (Auth.Get(ctx, sessions) is null) return Results.Json(new { error = "Belum login." }, statusCode: 401);
    var labs = app.Configuration.GetSection("Labs").GetChildren()
        .Select(c => new { id = c.Key, name = c["Name"] ?? c.Key, online = agents.ContainsKey(c.Key) }).ToArray();
    return Results.Json(labs);
});

// ===== Koneksi dari server guru (konektor) =====
app.Map("/agent/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var labId = ctx.Request.Headers["X-Lab-Id"].ToString();
    var key = ctx.Request.Headers["X-Lab-Key"].ToString();
    var expected = labIdRx.IsMatch(labId) ? app.Configuration[$"Labs:{labId}:Key"] : null;
    if (string.IsNullOrEmpty(expected) || !Auth.FixedEq(key, expected)) { ctx.Response.StatusCode = 401; return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var agent = new Agent(ws);
    if (agents.TryGetValue(labId, out var old)) await old.CloseAsync();
    agents[labId] = agent;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lab '{labId}' tersambung.");
    Audit("-", "-", labId, "AGENT", "tersambung dari " + Ip(ctx), 101, null);
    try { await agent.ReadLoop(); }
    finally
    {
        agents.TryRemove(new KeyValuePair<string, Agent>(labId, agent));
        agent.FailAll();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Lab '{labId}' terputus.");
    }
});

// ===== API jarak jauh: /l/{lab}/api/... =====
app.Map("/l/{lab}/{**rest}", async (HttpContext ctx, string lab, string rest) =>
{
    var s = Auth.Get(ctx, sessions);
    if (s is null) { await Err(ctx, 401, "Sesi tidak valid. Silakan login lagi."); return; }
    if (!labIdRx.IsMatch(lab) || string.IsNullOrEmpty(app.Configuration[$"Labs:{lab}:Key"])) { await Err(ctx, 404, "Lab tidak ditemukan."); return; }

    var segs = (rest ?? "").Split('/');
    var path = "/" + string.Join('/', segs);
    var kind = (segs.Length is 2 or 3 && segs[0] == "api" && segs.All(x => x.Length > 0)) ? Classify(ctx.Request.Method, path) : null;
    if (kind is null || (segs.Length == 3 && !segRx.IsMatch(segs[2])))
    { await Err(ctx, 404, "Tidak tersedia lewat jarak jauh."); return; }
    if (kind == "control" && s.Role != "controller")
    { await Err(ctx, 403, "Peran Anda hanya boleh memantau."); return; }

    agents.TryGetValue(lab, out var agent);
    if (path == "/api/health")
    {
        ctx.Response.StatusCode = agent is null ? 503 : 200;
        await Json(ctx, agent is null ? "{\"error\":\"Lab tidak terhubung.\"}" : "{\"ok\":true}");
        return;
    }
    if (agent is null) { await Err(ctx, 503, "Lab tidak terhubung."); return; }

    var body = await ReadLimited(ctx.Request.Body, 256 * 1024);
    if (body is null) { await Err(ctx, 413, "Permintaan terlalu besar."); return; }

    var encPath = "/" + string.Join('/', segs.Select(Uri.EscapeDataString));
    var reply = await agent.ForwardAsync(ctx.Request.Method, encPath, ctx.Request.QueryString.Value ?? "",
        ctx.Request.ContentType ?? "application/json", body);

    if (kind == "control")
    {
        if (path.StartsWith("/api/control/"))
        {
            var k = s.User + "|" + path;
            if (!lastControlLog.TryGetValue(k, out var t) || (DateTime.UtcNow - t).TotalSeconds > 60)
            { lastControlLog[k] = DateTime.UtcNow; Audit(s.User, s.Role, lab, "KONTROL", path, reply.Status, null); }
        }
        else Audit(s.User, s.Role, lab, ctx.Request.Method, path, reply.Status, Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300)));
    }

    ctx.Response.StatusCode = reply.Status;
    ctx.Response.ContentType = reply.ContentType;
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await ctx.Response.Body.WriteAsync(reply.Body);
});

app.Run();

// ===== Fungsi bantu =====
List<UserCfg> Users() => app.Configuration.GetSection("Users").Get<List<UserCfg>>() ?? new();
string Ip(HttpContext c) => c.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? c.Connection.RemoteIpAddress?.ToString() ?? "?";
string Token(HttpContext c) => Auth.TokenOf(c);

void Audit(string user, string role, string lab, string action, string detail, int status, string? extra)
{
    var line = JsonSerializer.Serialize(new { t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), user, role, lab, action, detail, status, extra });
    lock (auditLock) { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "audit.log"), line + Environment.NewLine); } catch { } }
}

static string? Classify(string method, string path)
{
    var depth = path.Count(c => c == '/');
    if (method == "GET")
    {
        if (path is "/api/health" or "/api/clients" or "/api/summary" or "/api/commands/recent" or "/api/screens") return "view";
        if (path.StartsWith("/api/screens/") && depth == 3) return "view";
    }
    else if (method == "POST")
    {
        if (path == "/api/watch") return "view";
        if (path == "/api/commands") return "control";
        if (path.StartsWith("/api/control/") && depth == 3) return "control";
    }
    return null;
}

static async Task<byte[]?> ReadLimited(Stream s, int max)
{
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int n;
    while ((n = await s.ReadAsync(buf)) > 0)
    {
        ms.Write(buf, 0, n);
        if (ms.Length > max) return null;
    }
    return ms.ToArray();
}

static Task Json(HttpContext c, string json) { c.Response.ContentType = "application/json"; return c.Response.WriteAsync(json); }
static Task Err(HttpContext c, int code, string msg) { c.Response.StatusCode = code; return Json(c, JsonSerializer.Serialize(new { error = msg })); }

// ===== Tipe =====
record LoginReq(string? Username, string? Password);
record Session(string User, string Role, DateTime Expires);
record Fail(int Count, DateTime Until);
record Reply(int Status, string ContentType, byte[] Body);
class UserCfg { public string Name { get; set; } = ""; public string Role { get; set; } = "viewer"; public string Hash { get; set; } = ""; }

static class Auth
{
    public static string MakeHash(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 200_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$200000${Convert.ToBase64String(salt)}${Convert.ToBase64String(h)}";
    }
    public static bool Verify(string pw, string stored)
    {
        try
        {
            var p = stored.Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;
            var want = Convert.FromBase64String(p[3]);
            var got = Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromBase64String(p[2]), int.Parse(p[1]), HashAlgorithmName.SHA256, want.Length);
            return CryptographicOperations.FixedTimeEquals(got, want);
        }
        catch { return false; }
    }
    public static bool FixedEq(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static string TokenOf(HttpContext c)
    {
        var t = c.Request.Headers["X-Marsa-Token"].ToString();
        if (t.Length == 0)
        {
            var a = c.Request.Headers.Authorization.ToString();
            if (a.StartsWith("Bearer ")) t = a[7..];
        }
        return t;
    }
    public static Session? Get(HttpContext c, ConcurrentDictionary<string, Session> sessions)
    {
        var t = TokenOf(c);
        return t.Length > 0 && sessions.TryGetValue(t, out var s) && s.Expires > DateTime.UtcNow ? s : null;
    }
}

static class Frame
{
    public static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
    public static byte[] Pack(object header, byte[] body)
    {
        var h = JsonSerializer.SerializeToUtf8Bytes(header);
        var buf = new byte[4 + h.Length + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, h.Length);
        h.CopyTo(buf, 4);
        body.CopyTo(buf, 4 + h.Length);
        return buf;
    }
    public static (JsonElement Header, byte[] Body) Unpack(byte[] data)
    {
        var hl = BinaryPrimitives.ReadInt32BigEndian(data);
        if (hl < 0 || hl > data.Length - 4) throw new InvalidDataException("frame rusak");
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, 4, hl));
        return (doc.RootElement.Clone(), data[(4 + hl)..]);
    }
}

sealed class Agent
{
    const int MaxMessage = 8 * 1024 * 1024;
    public WebSocket Socket { get; }
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<Reply>> pending = new();
    public Agent(WebSocket socket) { Socket = socket; }

    public async Task SendAsync(byte[] data)
    {
        await sendLock.WaitAsync();
        try { await Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }

    public async Task<Reply> ForwardAsync(string method, string path, string query, string ct, byte[] body)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            await SendAsync(Frame.Pack(new { id, method, path, query, ct }, body));
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
        catch (TimeoutException) { return new Reply(504, "application/json", Frame.Json("{\"error\":\"Lab tidak merespons.\"}")); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        { return new Reply(503, "application/json", Frame.Json("{\"error\":\"Lab tidak terhubung.\"}")); }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task ReadLoop()
    {
        var buf = new byte[64 * 1024];
        while (Socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await Socket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > MaxMessage) return;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Binary) continue;
            try
            {
                var (h, body) = Frame.Unpack(ms.ToArray());
                var id = h.GetProperty("id").GetString() ?? "";
                if (pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(new Reply(h.GetProperty("status").GetInt32(),
                        h.TryGetProperty("ct", out var c) ? c.GetString() ?? "application/json" : "application/json", body));
            }
            catch { }
        }
    }

    public void FailAll()
    {
        foreach (var t in pending.Values) t.TrySetResult(new Reply(503, "application/json", Frame.Json("{\"error\":\"Koneksi lab terputus.\"}")));
        pending.Clear();
    }

    public async Task CloseAsync()
    {
        try { await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "diganti koneksi baru", CancellationToken.None); } catch { }
    }
}

static class Pages
{
    public const string Index = """
<!doctype html><html lang="id"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>MARSA Lab · Gerbang</title>
<style>
:root{color-scheme:dark}body{margin:0;background:#0b141a;color:#eef8fa;font:15px system-ui,sans-serif}
.w{max-width:760px;margin:auto;padding:18px 14px}.c{background:#13232b;border:1px solid #29424c;border-radius:14px;padding:16px;margin-bottom:14px}
input,button{font:inherit;border-radius:10px;border:1px solid #29424c;padding:10px;background:#1a2f39;color:#eef8fa;width:100%;box-sizing:border-box;margin:6px 0}
button{background:#00c2d1;color:#00272b;font-weight:700;border:0;cursor:pointer}
table{width:100%;border-collapse:collapse}td,th{padding:8px 6px;border-bottom:1px solid #29424c;text-align:left;font-size:14px}th{color:#9bb1ba;font-size:11px;text-transform:uppercase}
.on{color:#2ecc71}.off{color:#9bb1ba}.m{color:#9bb1ba;font-size:13px}
</style></head><body><div class="w">
<h2><span style="color:#00c2d1">MARSA</span> Lab · Gerbang</h2>
<div class="c" id="login"><input id="u" placeholder="Nama pengguna" autocomplete="username"><input id="p" type="password" placeholder="Kata sandi" autocomplete="current-password"><button onclick="login()">Masuk</button><div id="err" class="m"></div></div>
<div id="main" hidden><div class="m" id="who" style="margin-bottom:10px"></div><div id="labs"></div></div>
</div><script>
let T=sessionStorage.getItem('t')||'';
const $=id=>document.getElementById(id);
const H=()=>({'X-Marsa-Token':T});
async function login(){
 const r=await fetch('/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:$('u').value,password:$('p').value})});
 const d=await r.json().catch(()=>({}));
 if(!r.ok){$('err').textContent=d.error||('Gagal ('+r.status+')');return}
 T=d.token;sessionStorage.setItem('t',T);$('who').textContent='Masuk sebagai '+d.user+' ('+d.role+')';start()}
async function start(){
 const r=await fetch('/auth/labs',{headers:H()});
 if(!r.ok){sessionStorage.removeItem('t');T='';return}
 $('login').hidden=true;$('main').hidden=false;
 for(const lab of await r.json()){
  const box=document.createElement('div');box.className='c';
  const h=document.createElement('h3');h.textContent=lab.name;box.append(h);
  const st=document.createElement('div');st.className='m';box.append(st);
  const tb=document.createElement('table');box.append(tb);$('labs').append(box);
  const tick=async()=>{
   try{const r=await fetch('/l/'+lab.id+'/api/clients',{headers:H()});
    if(r.status===401){sessionStorage.removeItem('t');location.reload();return}
    if(!r.ok){st.textContent=r.status===503?'Lab tidak terhubung ke gerbang.':'Gagal ('+r.status+')';tb.replaceChildren();return}
    const list=await r.json();st.textContent=list.filter(c=>c.status==='Online').length+' online dari '+list.length+' komputer';
    tb.replaceChildren();
    for(const c of list){const tr=document.createElement('tr');
     for(const v of [c.computerName,c.ipAddress,c.status]){const td=document.createElement('td');td.textContent=v;if(v===c.status)td.className=v==='Online'?'on':'off';tr.append(td)}
     tb.append(tr)}
   }catch{st.textContent='Tidak dapat menghubungi gerbang.'}};
  tick();setInterval(tick,5000)}}
if(T)start();
</script></body></html>
""";
}
