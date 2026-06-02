using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ----- helpers -----------------------------------------------------------

static string Cfg(IConfiguration c, string k) =>
    c[k] ?? throw new InvalidOperationException($"{k} not set");

static (string baseUrl, string apiKey, string deployment) AoaiCfg(IConfiguration c) =>
    (Cfg(c, "AOAI_ENDPOINT").TrimEnd('/'), Cfg(c, "AOAI_KEY"), c["AOAI_DEPLOYMENT"] ?? "sora-2");

static BlobContainerClient Container(IConfiguration c, TokenCredential cred)
{
    var account = Cfg(c, "STORAGE_ACCOUNT");
    var name = c["STORAGE_CONTAINER"] ?? "videos";
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    return svc.GetBlobContainerClient(name);
}

static async Task<string> UploadAndSign(
    BlobContainerClient container, string blobName, byte[] data, string contentType,
    BlobServiceClient svc, string account, CancellationToken ct)
{
    var blob = container.GetBlobClient(blobName);
    var opts = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
    using (var ms = new MemoryStream(data))
        await blob.UploadAsync(ms, opts, ct);
    var udk = await svc.GetUserDelegationKeyAsync(
        DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), ct);
    var sas = new BlobSasBuilder
    {
        BlobContainerName = container.Name,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(2)
    };
    sas.SetPermissions(BlobSasPermissions.Read);
    var token = sas.ToSasQueryParameters(udk.Value, account).ToString();
    return $"{blob.Uri}?{token}";
}

// ----- routes ------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 1. Submit a video job. Multipart so an optional reference image/video can ride along.
app.MapPost("/api/jobs", async (HttpRequest http, IHttpClientFactory hf, IConfiguration cfg, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });
    var form = await http.ReadFormAsync(ct);

    var prompt = form["prompt"].ToString();
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest(new { error = "prompt is required" });

    var seconds = form["seconds"].ToString();
    if (string.IsNullOrWhiteSpace(seconds)) seconds = "4";
    var size = form["size"].ToString();
    if (string.IsNullOrWhiteSpace(size)) size = "1280x720";

    var (baseUrl, apiKey, deployment) = AoaiCfg(cfg);

    using var multipart = new MultipartFormDataContent();
    multipart.Add(new StringContent(deployment), "model");
    multipart.Add(new StringContent(prompt), "prompt");
    multipart.Add(new StringContent(seconds), "seconds");
    multipart.Add(new StringContent(size), "size");

    var refFile = form.Files["inputReference"];
    if (refFile is { Length: > 0 })
    {
        var ms = new MemoryStream();
        await refFile.CopyToAsync(ms, ct);
        ms.Position = 0;
        var sc = new StreamContent(ms);
        sc.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(refFile.ContentType) ? "application/octet-stream" : refFile.ContentType);
        multipart.Add(sc, "input_reference", refFile.FileName);
    }

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(2);
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/openai/v1/videos");
    req.Headers.Add("api-key", apiKey);
    req.Content = multipart;
    using var resp = await client.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        return Results.Problem($"Submit failed ({(int)resp.StatusCode}): {body}");
    return Results.Content(body, "application/json");
});

// 2. Poll job status. Returns Sora's response verbatim (id, status, progress, ...).
app.MapGet("/api/jobs/{id}", async (string id, IHttpClientFactory hf, IConfiguration cfg, CancellationToken ct) =>
{
    var (baseUrl, apiKey, _) = AoaiCfg(cfg);
    var client = hf.CreateClient();
    client.DefaultRequestHeaders.Add("api-key", apiKey);
    using var resp = await client.GetAsync($"{baseUrl}/openai/v1/videos/{id}", ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
});

// 3. Finalize: download MP4 from Sora, upload to blob, return SAS URL.
app.MapPost("/api/jobs/{id}/finalize", async (
    string id, IHttpClientFactory hf, IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    var (baseUrl, apiKey, _) = AoaiCfg(cfg);
    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.Add("api-key", apiKey);

    using var v = await client.GetAsync($"{baseUrl}/openai/v1/videos/{id}/content?variant=video", ct);
    if (!v.IsSuccessStatusCode)
    {
        var err = await v.Content.ReadAsStringAsync(ct);
        return Results.Problem($"Download failed ({(int)v.StatusCode}): {err}");
    }
    var mp4 = await v.Content.ReadAsByteArrayAsync(ct);

    var account = Cfg(cfg, "STORAGE_ACCOUNT");
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
    var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id}.mp4";
    var url = await UploadAndSign(container, name, mp4, "video/mp4", svc, account, ct);
    return Results.Ok(new { url, blob = name, videoId = id });
});

// 4. Narrate: synthesize speech via Azure Speech, upload MP3, return SAS URL.
app.MapPost("/api/narrate", async (NarrateRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "text is required" });

    var key = Cfg(cfg, "AOAI_KEY"); // multi-service AIServices key works for Speech
    var region = Cfg(cfg, "SPEECH_REGION");
    var voice = string.IsNullOrWhiteSpace(req.Voice) ? "en-US-AvaMultilingualNeural" : req.Voice!;
    // Derive locale from voice short name (e.g. "hi-IN-SwaraNeural" -> "hi-IN")
    var lang = voice.Length >= 5 && voice[2] == '-' ? voice.Substring(0, 5) : "en-US";

    var ssml = $@"<speak version='1.0' xml:lang='{lang}'>
<voice name='{voice}'>{System.Security.SecurityElement.Escape(req.Text)}</voice>
</speak>";

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(2);
    using var msg = new HttpRequestMessage(HttpMethod.Post,
        $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
    msg.Headers.Add("Ocp-Apim-Subscription-Key", key);
    msg.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
    msg.Headers.Add("User-Agent", "videotool");
    msg.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

    using var resp = await client.SendAsync(msg, ct);
    if (!resp.IsSuccessStatusCode)
    {
        var err = await resp.Content.ReadAsStringAsync(ct);
        return Results.Problem($"TTS failed ({(int)resp.StatusCode}): {err}");
    }
    var mp3 = await resp.Content.ReadAsByteArrayAsync(ct);

    var account = Cfg(cfg, "STORAGE_ACCOUNT");
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
    var name = $"narration/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.mp3";
    var url = await UploadAndSign(container, name, mp3, "audio/mpeg", svc, account, ct);
    return Results.Ok(new { url, voice, length = mp3.Length });
});

app.MapGet("/api/voices", async (IHttpClientFactory hf, IConfiguration cfg, CancellationToken ct) =>
{
    var key = Cfg(cfg, "AOAI_KEY");
    var region = Cfg(cfg, "SPEECH_REGION");
    var client = hf.CreateClient();
    using var msg = new HttpRequestMessage(HttpMethod.Get,
        $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
    msg.Headers.Add("Ocp-Apim-Subscription-Key", key);
    using var resp = await client.SendAsync(msg, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
});

app.Run();

public record NarrateRequest(string Text, string? Voice);
