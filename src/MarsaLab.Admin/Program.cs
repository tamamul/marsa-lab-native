using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

var baseDir = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(baseDir, "wwwroot")) ? baseDir : Directory.GetCurrentDirectory()
});
var port = builder.Configuration.GetValue<int>("Lab:Port", 5080);
var token = builder.Configuration["Lab:Token"] ?? "GANTI_TOKEN_LAB_INI";
var clientToken = string.IsNullOrWhiteSpace(builder.Configuration["Lab:ClientToken"]) ? token : builder.Configuration["Lab:ClientToken"]!;
if (clientToken == token) Console.WriteLine("PERINGATAN: Lab:ClientToken kosong/sama dengan Lab:Token. Isi token client yang berbeda.");
var offlineAfterSeconds = Math.Clamp(builder.Configuration.GetValue<int>("Lab:OfflineAfterSeconds", 15), 10, 120);
if (token == "GANTI_TOKEN_LAB_INI" || token.Length < 24)
    Console.WriteLine("PERINGATAN: Ganti Lab:Token dengan token acak minimal 24 karakter sebelum dipakai.");
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var studentCode = builder.Configuration["Lab:StudentCode"] ?? "";
var maxUploadBytes = Math.Clamp(builder.Configuration.GetValue<int>("Lab:MaxUploadMB", 50), 1, 500) * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);
var app = builder.Build();
var clients = new ConcurrentDictionary<string, ClientStatus>(StringComparer.OrdinalIgnoreCase);
var commands = new ConcurrentDictionary<string, LabCommand>(StringComparer.OrdinalIgnoreCase);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarsaLab", "Admin");
var dataFile = Path.Combine(dataDirectory, "clients.json");
var auditFile = Path.Combine(dataDirectory, "commands.jsonl");
var saveLock = new object();
Directory.CreateDirectory(dataDirectory);
var shareDir = Path.Combine(dataDirectory, "Share");
var submitDir = Path.Combine(dataDirectory, "Submissions");
Directory.CreateDirectory(shareDir); Directory.CreateDirectory(submitDir);
try
{
    if (File.Exists(dataFile))
    {
        var saved = JsonSerializer.Deserialize<List<ClientStatus>>(File.ReadAllText(dataFile), jsonOptions) ?? [];
        foreach (var c in saved.Where(c => !string.IsNullOrWhiteSpace(c.ClientId))) clients[c.ClientId] = c with { Status = "Offline" };
        Console.WriteLine($"Memuat {clients.Count} data komputer.");
    }
}
catch (Exception ex) { Console.WriteLine($"Peringatan: data komputer gagal dibaca: {ex.Message}"); }

app.UseDefaultFiles();
app.UseStaticFiles();

var screens = new System.Collections.Concurrent.ConcurrentDictionary<string, ScreenFrame>();
var watching = new System.Collections.Concurrent.ConcurrentDictionary<string, WatchRequest>();
var inputs = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<InputEvent>>();

app.MapPost("/api/heartbeat", (HttpContext ctx, HeartbeatRequest? request) =>
{
    if (!TokenIsValid(ctx, clientToken)) return Results.Unauthorized();
    if (request is null || string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ComputerName)) return Results.BadRequest(new { error = "ClientId dan ComputerName wajib diisi." });
    if (request.ClientId.Length > 100 || request.ComputerName.Length > 100) return Results.BadRequest(new { error = "Identitas maksimal 100 karakter." });
    var id = request.ClientId.Trim(); var name = request.ComputerName.Trim();
    var ip = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown"; var now = DateTimeOffset.UtcNow;
    clients[id] = new ClientStatus(id, name, ip, now, "Online"); SaveClients();
    var watchMode = watching.TryGetValue(id, out var wr) && wr.Until > now ? wr.Mode : null;
return Results.Ok(new { ok = true, serverTime = now, watch = watchMode });
});

app.MapGet("/api/clients", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(clients.Values.Select(c => c with { Status = now - c.LastSeenUtc <= TimeSpan.FromSeconds(offlineAfterSeconds) ? "Online" : "Offline" }).OrderBy(c => c.ComputerName, StringComparer.OrdinalIgnoreCase).ToArray());
});
app.MapGet("/api/summary", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow; var online = clients.Values.Count(c => now - c.LastSeenUtc <= TimeSpan.FromSeconds(offlineAfterSeconds));
    return Results.Ok(new { total = clients.Count, online, offline = clients.Count - online, serverTime = now });
});

// Commands are limited to a small, explicit allow-list; no arbitrary shell command endpoint exists.
app.MapPost("/api/commands", (HttpContext ctx, CreateCommandRequest? request) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    if (request is null || string.IsNullOrWhiteSpace(request.Type)) return Results.BadRequest(new { error = "Jenis perintah wajib diisi." });
var allowed = new[] { "announce", "lock", "restart", "shutdown", "cancel-shutdown", "open-share", "open-url" };
    var type = request.Type.Trim().ToLowerInvariant();
    if (!allowed.Contains(type)) return Results.BadRequest(new { error = "Perintah tidak dikenal." });
    var message = (request.Message ?? "").Trim();
    if (type == "announce" && string.IsNullOrWhiteSpace(message)) return Results.BadRequest(new { error = "Isi pengumuman wajib diisi." });
    if (message.Length > 300) return Results.BadRequest(new { error = "Pesan maksimal 300 karakter." });
    if (type == "open-url" && !(Uri.TryCreate(message, UriKind.Absolute, out var openUri) && (openUri.Scheme == Uri.UriSchemeHttp || openUri.Scheme == Uri.UriSchemeHttps)))
    return Results.BadRequest(new { error = "Alamat harus diawali http:// atau https://" });
    var now = DateTimeOffset.UtcNow;
var target = string.IsNullOrWhiteSpace(request.TargetClientId) ? "*" : request.TargetClientId.Trim();
var picked = request.TargetClientIds?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
var targets = clients.Values.Where(c => now - c.LastSeenUtc <= TimeSpan.FromSeconds(offlineAfterSeconds)
    && (picked is { Count: > 0 } ? picked.Contains(c.ClientId) : (target == "*" || c.ClientId.Equals(target, StringComparison.OrdinalIgnoreCase)))).ToArray();
    var queued = new List<LabCommand>();
    foreach (var client in targets)
    {
        var command = new LabCommand(Guid.NewGuid().ToString("N"), client.ClientId, type, message, now, "Pending", "");
        commands[command.Id] = command; queued.Add(command); WriteAudit(command);
    }
    return Results.Ok(new { ok = true, queued = queued.Count, command = type, targets = targets.Select(t => t.ComputerName).ToArray() });
});

// ===== Berbagi file: guru (pakai token) =====
app.MapGet("/api/files", (HttpContext ctx) => TokenIsValid(ctx, token) ? Results.Ok(ListShared()) : Results.Unauthorized());
app.MapPost("/api/files", async (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "File belum dipilih." });
    var name = SafeName(file.FileName);
    if (name.Length == 0) return Results.BadRequest(new { error = "Nama file tidak valid." });
    await using (var fs = File.Create(Path.Combine(shareDir, name))) await file.CopyToAsync(fs);
    return Results.Ok(new { ok = true, name });
});
app.MapDelete("/api/files/{name}", (HttpContext ctx, string name) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var path = Path.Combine(shareDir, SafeName(name));
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path); return Results.Ok(new { ok = true });
});
app.MapGet("/api/submissions", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    return Results.Ok(new DirectoryInfo(submitDir).GetFiles("*", SearchOption.AllDirectories)
        .OrderByDescending(f => f.LastWriteTimeUtc)
        .Select(f => new SubmissionItem(f.Directory!.Name, f.Name, f.Length, f.LastWriteTimeUtc)).ToArray());
});
app.MapGet("/api/submissions/file", (HttpContext ctx, string student, string name) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var path = Path.Combine(submitDir, SafeName(student), SafeName(name));
    return File.Exists(path) ? Results.File(path, "application/octet-stream", SafeName(name)) : Results.NotFound();
});

// ===== Berbagi file: siswa (tanpa token admin; opsional kode kelas) =====
app.MapGet("/api/student/files", (HttpContext ctx) => StudentOk(ctx) ? Results.Ok(ListShared()) : Results.Unauthorized());
app.MapGet("/api/student/files/{name}", (HttpContext ctx, string name) =>
{
    if (!StudentOk(ctx)) return Results.Unauthorized();
    var safe = SafeName(name); var path = Path.Combine(shareDir, safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream", safe) : Results.NotFound();
});
app.MapPost("/api/student/submit", async (HttpContext ctx) =>
{
    if (!StudentOk(ctx)) return Results.Unauthorized();
    var form = await ctx.Request.ReadFormAsync();
    var student = SafeName(form["student"].ToString(), 40);
    var file = form.Files.FirstOrDefault();
    if (student.Length < 2) return Results.BadRequest(new { error = "Isi nama (minimal 2 huruf)." });
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "File belum dipilih." });
    var name = SafeName(file.FileName);
    if (name.Length == 0) return Results.BadRequest(new { error = "Nama file tidak valid." });
    var dir = Path.Combine(submitDir, student); Directory.CreateDirectory(dir);
    var saved = $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}";
    await using (var fs = File.Create(Path.Combine(dir, saved))) await file.CopyToAsync(fs);
    return Results.Ok(new { ok = true, saved });
});


app.MapGet("/api/commands/next", (HttpContext ctx, string clientId) =>
{
    if (!TokenIsValid(ctx, clientToken)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100) return Results.BadRequest();
    var pending = commands.Values.Where(c => c.ClientId.Equals(clientId, StringComparison.OrdinalIgnoreCase) && c.Status == "Pending").OrderBy(c => c.CreatedUtc).Take(5).ToArray();
    foreach (var c in pending) commands.TryUpdate(c.Id, c with { Status = "Delivered" }, c);
    return Results.Ok(pending);
});
app.MapPost("/api/commands/{id}/result", (HttpContext ctx, string id, CommandResultRequest? request) =>
{
    if (!TokenIsValid(ctx, clientToken)) return Results.Unauthorized();
    if (request is null || string.IsNullOrWhiteSpace(request.ClientId)) return Results.BadRequest();
    if (!commands.TryGetValue(id, out var existing) || !existing.ClientId.Equals(request.ClientId, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    var updated = existing with { Status = request.Success ? "Berhasil" : "Gagal", Result = (request.Result ?? "").Trim()[..Math.Min((request.Result ?? "").Trim().Length, 500)] };
    commands[id] = updated; WriteAudit(updated);
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/commands/recent", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    return Results.Ok(commands.Values.OrderByDescending(c => c.CreatedUtc).Take(30).ToArray());
});
app.MapGet("/api/submissions/zip", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token))
        return Results.Unauthorized();

    var files = new DirectoryInfo(submitDir)
        .GetFiles("*", SearchOption.AllDirectories);

    if (files.Length == 0)
        return Results.NotFound(new { error = "Belum ada tugas." });

    var tmp = new FileStream(
        Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + ".zip"
        ),
        FileMode.Create,
        FileAccess.ReadWrite,
        FileShare.None,
        81920,
        FileOptions.DeleteOnClose
    );

    try
    {
        using (var zip = new System.IO.Compression.ZipArchive(
            tmp,
            System.IO.Compression.ZipArchiveMode.Create,
            leaveOpen: true))
        {
            foreach (var f in files)
            {
                var entryName = Path.Combine(
                    f.Directory!.Name,
                    f.Name
                ).Replace('\\', '/');

                var entry = zip.CreateEntry(
                    entryName,
                    System.IO.Compression.CompressionLevel.Fastest
                );

                using (var entryStream = entry.Open())
                using (var fileStream = f.OpenRead())
                {
                    fileStream.CopyTo(entryStream);
                }
            }
        }

        tmp.Position = 0;

        return Results.File(
            tmp,
            "application/zip",
            $"tugas-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        );
    }
    catch
    {
        tmp.Dispose();
        throw;
    }
});

// ===== Pantau layar =====
app.MapPost("/api/watch", (HttpContext ctx, WatchBody? body) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    if (body is null) return Results.BadRequest(new { error = "Data kosong." });
    var mode = body.Mode == "full" ? "full" : "thumb";
    var until = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(body.Seconds, 5, 30));
    var ids = body.ClientIds is { Length: > 0 } ? body.ClientIds : clients.Keys.ToArray();
foreach (var id in ids.Take(200))
{
    if (!clients.ContainsKey(id)) continue;
    var sameMode = watching.TryGetValue(id, out var cur) && cur.Mode == mode && cur.Until > DateTimeOffset.UtcNow;
    if (!sameMode) inputs.TryRemove(id, out _);
    watching[id] = new WatchRequest(mode, until);
}
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/screens/{clientId}", async (HttpContext ctx, string clientId) =>
{
    if (!TokenIsValid(ctx, clientToken)) return Results.Unauthorized();
    if (!clients.ContainsKey(clientId)) return Results.NotFound();
    if (ctx.Request.ContentLength is null or > 3_000_000) return Results.BadRequest(new { error = "Ukuran gambar tidak valid." });
    using var ms = new MemoryStream((int)ctx.Request.ContentLength.Value);
    await ctx.Request.Body.CopyToAsync(ms);
    int.TryParse(ctx.Request.Query["w"].ToString(), out var wd); int.TryParse(ctx.Request.Query["h"].ToString(), out var ht);
    var m = ctx.Request.Query["m"].ToString() == "full" ? "full" : "thumb";
    screens[clientId] = new ScreenFrame(ms.ToArray(), DateTimeOffset.UtcNow, wd, ht, m);
    var stillWatched = watching.TryGetValue(clientId, out var wr) && wr.Until > DateTimeOffset.UtcNow ? wr.Mode : null;
	return Results.Ok(new { watch = stillWatched, input = DrainInput(clientId) });
});
app.MapGet("/api/screens", (HttpContext ctx) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
    return Results.Ok(screens.Where(kv => kv.Value.Mode == "thumb" && kv.Value.Utc > cutoff)
        .ToDictionary(kv => kv.Key, kv => new { t = kv.Value.Utc, w = kv.Value.Width, h = kv.Value.Height, jpg = Convert.ToBase64String(kv.Value.Jpeg) }));
});
app.MapGet("/api/screens/{clientId}", (HttpContext ctx, string clientId) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    return screens.TryGetValue(clientId, out var f) && f.Utc > DateTimeOffset.UtcNow.AddSeconds(-30)
        ? Results.Ok(new { t = f.Utc, w = f.Width, h = f.Height, mode = f.Mode, jpg = Convert.ToBase64String(f.Jpeg) })
        : Results.NotFound();
});

app.MapPost("/api/control/{clientId}", (HttpContext ctx, string clientId, InputEvent[]? events) =>
{
    if (!TokenIsValid(ctx, token)) return Results.Unauthorized();
    if (events is null || events.Length == 0 || events.Length > 60) return Results.BadRequest(new { error = "Event tidak valid." });
    if (!(watching.TryGetValue(clientId, out var w) && w.Mode == "full" && w.Until > DateTimeOffset.UtcNow))
        return Results.BadRequest(new { error = "Buka tampilan besar dulu." });
    var q = inputs.GetOrAdd(clientId, _ => new System.Collections.Concurrent.ConcurrentQueue<InputEvent>());
    if (q.Count > 200) return Results.StatusCode(429);
    foreach (var ev in events) q.Enqueue(ev);
    return Results.Ok(new { queued = events.Length });
});

app.MapGet("/api/health", () => Results.Ok(new { ok = true, app = "MarsaLab.Admin", version = "1.2.0" }));
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://localhost:{port}";
    Console.WriteLine($"MARSA Lab server berjalan: {url}"); Console.WriteLine($"Status offline setelah {offlineAfterSeconds} detik.");
    Console.WriteLine($"Data: {dataFile}"); Console.WriteLine("Kontrol tersedia: pengumuman, kunci layar, restart, shutdown terjadwal, batalkan shutdown.");
if (!args.Contains("--no-browser"))
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
});
app.Run();

void SaveClients()
{
    lock (saveLock) try { var tmp = dataFile + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(clients.Values.OrderBy(c => c.ComputerName).ToArray(), jsonOptions)); File.Move(tmp, dataFile, true); }
    catch (Exception ex) { Console.WriteLine($"Peringatan: gagal menyimpan data: {ex.Message}"); }
}
void WriteAudit(LabCommand command)
{
    lock (saveLock) try { File.AppendAllText(auditFile, JsonSerializer.Serialize(command) + Environment.NewLine); }
    catch (Exception ex) { Console.WriteLine($"Peringatan: gagal menulis audit: {ex.Message}"); }
}
static bool TokenIsValid(HttpContext ctx, string expected)
{
    if (string.IsNullOrWhiteSpace(expected) || expected == "GANTI_TOKEN_LAB_INI") return false;
    if (!ctx.Request.Headers.TryGetValue("X-Marsa-Token", out var supplied)) return false;
    var a = Encoding.UTF8.GetBytes(supplied.ToString()); var b = Encoding.UTF8.GetBytes(expected);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static string SafeName(string? s, int max = 80)
{
    var n = Path.GetFileName((s ?? "").Trim());
    foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
    n = n.Trim('.', ' ');
    return n.Length > max ? n[..max] : n;
}
InputEvent[] DrainInput(string id)
{
    var list = new List<InputEvent>();
    if (inputs.TryGetValue(id, out var q)) while (list.Count < 50 && q.TryDequeue(out var ev)) list.Add(ev);
    return list.ToArray();
}

bool StudentOk(HttpContext ctx) => string.IsNullOrEmpty(studentCode)
    || ctx.Request.Query["code"].ToString() == studentCode
    || ctx.Request.Headers["X-Marsa-Code"].ToString() == studentCode;
FileItem[] ListShared() => new DirectoryInfo(shareDir).GetFiles()
    .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => new FileItem(f.Name, f.Length, f.LastWriteTimeUtc)).ToArray();

record HeartbeatRequest(string ClientId, string ComputerName);
record ClientStatus(string ClientId, string ComputerName, string IpAddress, DateTimeOffset LastSeenUtc, string Status);
record CreateCommandRequest(string Type, string? TargetClientId, string? Message, string[]? TargetClientIds = null);
record FileItem(string Name, long Size, DateTime ModifiedUtc);
record SubmissionItem(string Student, string Name, long Size, DateTime ModifiedUtc);
record CommandResultRequest(string ClientId, bool Success, string? Result);
record LabCommand(string Id, string ClientId, string Type, string Message, DateTimeOffset CreatedUtc, string Status, string Result);
record ScreenFrame(byte[] Jpeg, DateTimeOffset Utc, int Width, int Height, string Mode);
record WatchRequest(string Mode, DateTimeOffset Until);
record WatchBody(string[]? ClientIds, string? Mode, int Seconds = 15);
record InputEvent(string T, double X, double Y, string? B, int Vk, string? S, string[]? Mods, int D);