using System.Net.Http.Json;
using System.Text.Json;

namespace MarsaLab.Desktop;

public record ClientDto(string ClientId, string ComputerName, string IpAddress, DateTimeOffset LastSeenUtc, string Status);
public record CommandDto(string Id, string ClientId, string Type, string? Message, DateTimeOffset CreatedUtc, string Status, string? Result);
public record ShotDto(DateTimeOffset T, int W, int H, string? Mode, string Jpg);
public record InputEv(string T, double X = 0, double Y = 0, string? B = null, int Vk = 0, string? S = null, string[]? Mods = null, int D = 0);
public record FileDto(string Name, long Size, DateTime ModifiedUtc);
public record SubDto(string Student, string Name, long Size, DateTime ModifiedUtc);
record ErrorDto(string? Error);
record QueuedDto(int Queued);

public class ApiException : Exception
{
    public int Status { get; }
    public ApiException(string message, int status) : base(message) { Status = status; }
}

public sealed class Api
{
    static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    readonly HttpClient http;
    readonly HttpClient big; // untuk unggah/unduh file besar (tanpa batas 8 detik)

    public Api(string baseUrl, string token)
    {
        http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.Add("X-Marsa-Token", token);
        big = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(30) };
        big.DefaultRequestHeaders.Add("X-Marsa-Token", token);
    }

    public async Task<bool> HealthAsync()
    {
        try { using var r = await http.GetAsync("api/health"); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    static async Task Check(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return;
        var msg = "HTTP " + (int)r.StatusCode;
        if ((int)r.StatusCode == 413) msg = "File terlalu besar (melebihi batas unggah server).";
        try { var d = await r.Content.ReadFromJsonAsync<ErrorDto>(J); if (!string.IsNullOrEmpty(d?.Error)) msg = d!.Error!; } catch { }
        throw new ApiException(msg, (int)r.StatusCode);
    }

    async Task<T> GetAsync<T>(string url)
    {
        using var r = await http.GetAsync(url);
        await Check(r);
        return (await r.Content.ReadFromJsonAsync<T>(J))!;
    }

    async Task PostRaw(string url, object body)
    {
        using var r = await http.PostAsJsonAsync(url, body, J);
        await Check(r);
    }

    public Task<List<ClientDto>> ClientsAsync() => GetAsync<List<ClientDto>>("api/clients");
    public Task<List<CommandDto>> RecentAsync() => GetAsync<List<CommandDto>>("api/commands/recent");

    public async Task<int> SendAsync(string type, string[]? ids, string message)
    {
        using var r = await http.PostAsJsonAsync("api/commands", new { type, targetClientId = "*", targetClientIds = ids, message }, J);
        await Check(r);
        return (await r.Content.ReadFromJsonAsync<QueuedDto>(J))?.Queued ?? 0;
    }

    // ===== Berkas & Tugas =====
    public Task<List<FileDto>> FilesAsync() => GetAsync<List<FileDto>>("api/files");
    public Task<List<SubDto>> SubmissionsAsync() => GetAsync<List<SubDto>>("api/submissions");

    public async Task UploadAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(fs);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", System.IO.Path.GetFileName(path));
        using var r = await big.PostAsync("api/files", form);
        await Check(r);
    }

    public async Task DeleteFileAsync(string name)
    {
        using var r = await http.DeleteAsync("api/files/" + Uri.EscapeDataString(name));
        await Check(r);
    }

    async Task DownloadAsync(string url, string destPath)
    {
        using var r = await big.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        await Check(r);
        await using var src = await r.Content.ReadAsStreamAsync();
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst);
    }

    public Task DownloadSubmissionAsync(string student, string name, string dest) =>
        DownloadAsync($"api/submissions/file?student={Uri.EscapeDataString(student)}&name={Uri.EscapeDataString(name)}", dest);
    public Task DownloadZipAsync(string dest) => DownloadAsync("api/submissions/zip", dest);

    public Task WatchAsync(string[] ids, string mode, int seconds) => PostRaw("api/watch", new { clientIds = ids, mode, seconds });
    public Task<Dictionary<string, ShotDto>> ScreensAsync() => GetAsync<Dictionary<string, ShotDto>>("api/screens");
    public Task<ShotDto> ScreenAsync(string id) => GetAsync<ShotDto>("api/screens/" + Uri.EscapeDataString(id));
    public Task ControlAsync(string id, IEnumerable<InputEv> events) => PostRaw("api/control/" + Uri.EscapeDataString(id), events.ToArray());
}
