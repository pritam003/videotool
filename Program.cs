using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using VideoTool;
using WebPush;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
// In-process job store for PromptCraft progress. F1 is single-instance so a
// process-local dictionary is sufficient; jobs auto-evict after 10 minutes.
builder.Services.AddSingleton<PromptCraftJobStore>();
// Web Push: VAPID keys + subscription store + watcher registry. Auto-generates
// keys on first start (persisted to /home/data/vapid.json on App Service Linux,
// or ./vapid.json locally) so the operator doesn't have to set anything manually.
builder.Services.AddSingleton<VapidConfig>(sp => VapidConfig.LoadOrCreate(sp.GetRequiredService<ILogger<VapidConfig>>()));
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<SoraJobWatcherRegistry>();
// Wan2.2 (ComfyUI) client. Replaces the AOAI Sora-2 video path; the rest of the
// app (frontend, push watcher, blob upload, stitch) keeps working unchanged.
builder.Services.AddSingleton<WanClient>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// ----- auth (App Service Easy Auth, Microsoft IdP, federated to Google) --
// When AUTH_REQUIRED=1 every non-public request must carry the
// X-MS-CLIENT-PRINCIPAL-* headers injected by the Easy Auth sidecar.
// Optionally also enforce membership in ALLOWED_GROUP_ID via the `groups`
// claim in X-MS-CLIENT-PRINCIPAL (defense-in-depth on top of Entra's
// "Assignment required" gate). When AUTH_REQUIRED is unset/0 the middleware
// is a no-op so the app keeps working through the portal-config window.
var _authRequired = string.Equals(app.Configuration["AUTH_REQUIRED"], "1", StringComparison.Ordinal)
                  || string.Equals(app.Configuration["AUTH_REQUIRED"], "true", StringComparison.OrdinalIgnoreCase);
var _allowedGroupId = app.Configuration["ALLOWED_GROUP_ID"];
static bool IsPublicAuthPath(PathString p) =>
    p.StartsWithSegments("/health") ||
    p.StartsWithSegments("/.auth") ||
    p.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) ||
    p.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
    p.Equals("/api/push/vapid-public-key", StringComparison.OrdinalIgnoreCase);
if (_authRequired)
{
    app.Use(async (ctx, next) =>
    {
        if (IsPublicAuthPath(ctx.Request.Path)) { await next(); return; }
        var oid = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].ToString();
        if (string.IsNullOrEmpty(oid))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
            await ctx.Response.WriteAsJsonAsync(new { error = "auth required" });
            return;
        }
        if (!string.IsNullOrEmpty(_allowedGroupId))
        {
            var b64 = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();
            var ok = false;
            if (!string.IsNullOrEmpty(b64))
            {
                try
                {
                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("claims", out var cl))
                    {
                        foreach (var c in cl.EnumerateArray())
                        {
                            if (c.TryGetProperty("typ", out var t) && c.TryGetProperty("val", out var v))
                            {
                                var typ = t.GetString();
                                if ((typ == "groups" || typ == "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid")
                                    && string.Equals(v.GetString(), _allowedGroupId, StringComparison.OrdinalIgnoreCase))
                                { ok = true; break; }
                            }
                        }
                    }
                }
                catch { /* malformed -> reject */ }
            }
            if (!ok)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "not in allowed group" });
                return;
            }
        }
        await next();
    });
}

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

// Streaming upload: avoids buffering large payloads (e.g. Sora MP4 downloads) in memory.
// Critical on App Service F1 (1GB RAM) where a few concurrent finalize calls + a byte[]
// MP4 was OOM-recycling the container mid-request. byte[] callers wrap with MemoryStream
// inline (cheap; no extra copy).
static async Task<string> UploadAndSign(
    BlobContainerClient container, string blobName, Stream data, string contentType,
    BlobServiceClient svc, string account, CancellationToken ct)
{
    var blob = container.GetBlobClient(blobName);
    var opts = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
    await blob.UploadAsync(data, opts, ct);
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

// /api/me — identity probe for the frontend. When AUTH_REQUIRED=1 and the
// user isn't signed in, the auth middleware short-circuits with 401 before
// reaching this handler (which is exactly what the frontend uses to trigger
// its login redirect). When AUTH_REQUIRED is off, returns devMode=true so
// the UI hides the sign-in chrome.
app.MapGet("/api/me", (HttpRequest http) =>
{
    var oid = http.Headers["X-MS-CLIENT-PRINCIPAL-ID"].ToString();
    if (string.IsNullOrEmpty(oid))
        return Results.Ok(new { authenticated = false, devMode = !_authRequired });
    var name = http.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
    var idp = http.Headers["X-MS-CLIENT-PRINCIPAL-IDP"].ToString();
    return Results.Ok(new { authenticated = true, objectId = oid, email = name, idp });
});

// 1. Submit a video job. Multipart so an optional start image can ride along.
//    Routing:
//      • startImage form field (a ComfyUI input filename from /api/extract-last-frame
//        or /api/comfy-image) → Wan2.2 IMAGE-to-video, conditioned on that frame.
//      • inputReference file upload → uploaded to ComfyUI, then image-to-video.
//      • neither → text-to-video (original behaviour).
//    This is what makes the story-chain continuation real: every clip after the first
//    is conditioned on the previous clip's last frame, so character + scene carry over.
app.MapPost("/api/jobs", async (HttpRequest http, WanClient wan, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });
    var form = await http.ReadFormAsync(ct);

    var prompt = form["prompt"].ToString();
    if (string.IsNullOrWhiteSpace(prompt))
        return Results.BadRequest(new { error = "prompt is required" });

    if (!int.TryParse(form["seconds"].ToString(), out var seconds) || seconds < 1) seconds = 4;
    var size = form["size"].ToString();
    if (string.IsNullOrWhiteSpace(size)) size = "1280x720";
    int width = 1280, height = 720;
    var parts = size.Split('x', 2);
    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
    { width = w; height = h; }

    // Determine the start-image source (if any) for image-to-video.
    var startImage = form["startImage"].ToString();
    var refUpload = form.Files["inputReference"] ?? form.Files["startImageFile"];

    try
    {
        string id;
        if (!string.IsNullOrWhiteSpace(startImage))
        {
            id = await wan.SubmitI2VAsync(prompt, startImage, seconds, width, height, ct);
        }
        else if (refUpload is not null && refUpload.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await using var s = refUpload.OpenReadStream();
            var name = await wan.UploadImageAsync(s, $"ref-{Guid.NewGuid():N}.png", ct);
            id = await wan.SubmitI2VAsync(prompt, name, seconds, width, height, ct);
        }
        else
        {
            id = await wan.SubmitAsync(prompt, seconds, width, height, ct);
        }
        // Sora-shaped { id, status } so the frontend's existing polling code keeps working.
        return Results.Json(new { id, status = "queued" });
    }
    catch (Exception ex) when (!ct.IsCancellationRequested && IsWarmingError(ex))
    {
        // GPU container is scaling from zero / activating: connection refused, client
        // timeout, or the ingress is briefly returning 5xx during activation. Answer
        // 503 {warming:true} so the frontend's story-chain retry loop backs off and
        // retries (~45s cold start) instead of failing the whole chain on clip 1.
        return Results.Json(
            new { warming = true, error = "GPU is warming up (scale-from-zero). Retry shortly." },
            statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Wan submit failed: {ex.Message}");
    }
});

// 2. Poll job status. Returns a Sora-shaped {id, status, progress, ...} JSON
//    object so the frontend's existing polling loop stays unchanged. ComfyUI's
//    /history is the source of truth for terminal states; /queue distinguishes
//    queued vs running while the job is still in flight.
app.MapGet("/api/jobs/{id}", async (string id, WanClient wan, CancellationToken ct) =>
{
    // Retry transient errors with exponential backoff, mirroring the old Sora path.
    var delays = new[] { 1000, 2000, 4000, 8000 };
    Exception? last = null;
    for (int attempt = 0; attempt <= delays.Length; attempt++)
    {
        try
        {
            var json = await wan.GetStatusJsonAsync(id, ct);
            return Results.Content(json, "application/json");
        }
        catch (Exception ex)
        {
            last = ex;
            if (attempt < delays.Length) await Task.Delay(delays[attempt], ct);
        }
    }
    return Results.Problem($"Status fetch failed after retries: {last?.Message}");
});

// 2b. Cancel an in-flight job. ComfyUI doesn't bill per-second the way Sora did,
//     but cancelling frees the GPU for queued items.
app.MapPost("/api/jobs/{id}/cancel", async (string id, WanClient wan, CancellationToken ct) =>
{
    var (sc, body) = await wan.CancelAsync(id, ct);
    return Results.Content(body, "application/json", statusCode: sc);
});

// 3. Finalize: download MP4 from ComfyUI's /view, stream-upload to blob, return SAS URL.
app.MapPost("/api/jobs/{id}/finalize", async (
    string id, WanClient wan, IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    // Re-fetch status to obtain the output filename + subfolder ComfyUI assigned.
    var statusJson = await wan.GetStatusJsonAsync(id, ct);
    using var sdoc = JsonDocument.Parse(statusJson);
    var root = sdoc.RootElement;
    if (!root.TryGetProperty("outputFilename", out var fnEl) || fnEl.ValueKind != JsonValueKind.String)
        return Results.Problem("Job is not finished yet (no output filename available).");
    var filename = fnEl.GetString()!;
    var subfolder = root.TryGetProperty("outputSubfolder", out var sfEl) && sfEl.ValueKind == JsonValueKind.String
        ? sfEl.GetString() : null;

    using var v = await wan.DownloadAsync(filename, subfolder, ct);
    if (!v.IsSuccessStatusCode)
    {
        var err = await v.Content.ReadAsStringAsync(ct);
        return Results.Problem($"Download failed ({(int)v.StatusCode}): {err}");
    }

    var account = Cfg(cfg, "STORAGE_ACCOUNT");
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
    var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id}.mp4";
    // Stream ComfyUI -> Blob directly. F1's 1GB RAM was OOM-recycling the container
    // when we buffered the full MP4 into a byte[] before upload.
    await using var src = await v.Content.ReadAsStreamAsync(ct);
    var url = await UploadAndSign(container, name, src, "video/mp4", svc, account, ct);
    return Results.Ok(new { url, blob = name, videoId = id });
});

// ─── Story-chain (I2V continuation) image handoff ────────────────────────────
//
// Two endpoints feed start-images into the Wan2.2 image-to-video workflow:
//   • /api/comfy-image      — the user's character image for clip 1.
//   • /api/extract-last-frame — the previous clip's final frame for clips 2..N.
// Both normalise to the exact clip W×H (scale-to-cover + centre-crop, no bars,
// no distortion), push the PNG into ComfyUI's input dir (returning the filename
// the I2V LoadImage node references), AND upload a blob copy so the UI can SHOW
// the user the exact frame that hands off to the next clip.

// Shared: normalise a local image to WxH PNG, upload to ComfyUI + blob, return both refs.
static async Task<IResult> PublishFrameAsync(string srcPath, int W, int H, string tag,
    WanClient wan, IConfiguration cfg, TokenCredential cred, string ffmpeg, CancellationToken ct)
{
    var outPng = Path.Combine(Path.GetDirectoryName(srcPath)!, $"frame-{Guid.NewGuid():N}.png");
    // Scale to COVER WxH then centre-crop to exact WxH: keeps the subject centred,
    // fills the frame (no letterbox bars for the model to animate), no aspect distortion.
    var vf = $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H}";
    var (ec, err) = await RunProcessAsync(ffmpeg, $"-y -i \"{srcPath}\" -vf \"{vf}\" -frames:v 1 \"{outPng}\"", ct);
    if (ec != 0 || !File.Exists(outPng))
        return Results.Problem($"ffmpeg frame normalise failed (exit {ec}): {err[..Math.Min(err.Length, 800)]}");

    // Push into ComfyUI input dir (unique name avoids LoadImage cache serving a stale frame).
    string comfyName;
    await using (var fs = File.OpenRead(outPng))
        comfyName = await wan.UploadImageAsync(fs, $"{tag}-{Guid.NewGuid():N}.png", ct);

    // Blob copy for the UI handoff preview.
    var account = Cfg(cfg, "STORAGE_ACCOUNT");
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
    var blobName = $"frames/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{tag}-{Guid.NewGuid():N}.png";
    string previewUrl;
    await using (var fs = File.OpenRead(outPng))
        previewUrl = await UploadAndSign(container, blobName, fs, "image/png", svc, account, ct);

    return Results.Ok(new { name = comfyName, previewUrl });
}

static (int W, int H) ParseSize(string? size)
{
    int W = 832, H = 480; // default landscape 480p — fast, good for I2V chaining
    if (!string.IsNullOrWhiteSpace(size))
    {
        var m = System.Text.RegularExpressions.Regex.Match(size!, @"^(\d+)x(\d+)$");
        if (m.Success) { W = int.Parse(m.Groups[1].Value); H = int.Parse(m.Groups[2].Value); }
    }
    // Wan dims must be multiples of 16.
    W = Math.Max(256, (W / 16) * 16);
    H = Math.Max(256, (H / 16) * 16);
    return (W, H);
}

// True when a submit exception looks like a scale-from-zero cold start (container not
// up yet / ingress activating) rather than a genuine failure. Lets /api/jobs answer
// 503 {warming:true} so the frontend retries the chain instead of aborting on clip 1.
static bool IsWarmingError(Exception ex)
{
    if (ex is HttpRequestException) return true;   // connection refused / DNS / socket reset
    if (ex is TaskCanceledException) return true;   // HttpClient timeout while the GPU activates
    var m = ex.Message ?? string.Empty;
    return m.Contains("(502)") || m.Contains("(503)") || m.Contains("(504)")
        || m.Contains("ActivationFailed", StringComparison.OrdinalIgnoreCase)
        || m.Contains("no capacity", StringComparison.OrdinalIgnoreCase);
}

// Upload + normalise the user's character image → ComfyUI input + blob preview.
app.MapPost("/api/comfy-image", async (HttpRequest http, WanClient wan,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    if (!http.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });
    var form = await http.ReadFormAsync(ct);
    var file = form.Files["image"];
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "image file is required" });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null) return Results.Problem("ffmpeg binary not found.");
    var (W, H) = ParseSize(form["size"].ToString());

    var work = Path.Combine(Path.GetTempPath(), $"charimg-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var inPath = Path.Combine(work, "in" + Path.GetExtension(file.FileName));
        await using (var fs = File.Create(inPath))
            await file.CopyToAsync(fs, ct);
        return await PublishFrameAsync(inPath, W, H, "char", wan, cfg, cred, ffmpeg, ct);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested && IsWarmingError(ex))
    {
        // First GPU touch of a chain — tolerate scale-from-zero so the UI can retry.
        return Results.Json(
            new { warming = true, error = "GPU is warming up (scale-from-zero). Retry shortly." },
            statusCode: 503);
    }
    finally { try { Directory.Delete(work, recursive: true); } catch { } }
});

// Extract the last frame of a finalized clip → ComfyUI input + blob preview.
// Body: { videoUrl, size? }.
app.MapPost("/api/extract-last-frame", async (ExtractFrameRequest req, IHttpClientFactory hf,
    WanClient wan, IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.VideoUrl))
        return Results.BadRequest(new { error = "videoUrl is required" });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null) return Results.Problem("ffmpeg binary not found.");
    var (W, H) = ParseSize(req.Size);

    var work = Path.Combine(Path.GetTempPath(), $"lastframe-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var local = Path.Combine(work, "in.mp4");
        var client = hf.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);
        using (var s = await client.GetStreamAsync(req.VideoUrl, ct))
        await using (var f = File.Create(local))
            await s.CopyToAsync(f, ct);

        // Grab the FINAL frame: decode the last ~1s and -update 1 overwrites so the
        // last written frame is the true EOF frame. Robust even when N is unknown.
        var lastFrame = Path.Combine(work, "last.png");
        var (ec, err) = await RunProcessAsync(ffmpeg,
            $"-y -sseof -1 -i \"{local}\" -update 1 -q:v 1 \"{lastFrame}\"", ct);
        if (ec != 0 || !File.Exists(lastFrame))
        {
            // Fallback: take the very first frame if tail seek failed (tiny/odd clip).
            (ec, err) = await RunProcessAsync(ffmpeg, $"-y -i \"{local}\" -frames:v 1 \"{lastFrame}\"", ct);
            if (ec != 0 || !File.Exists(lastFrame))
                return Results.Problem($"ffmpeg last-frame extract failed (exit {ec}): {err[..Math.Min(err.Length, 800)]}");
        }
        return await PublishFrameAsync(lastFrame, W, H, "lf", wan, cfg, cred, ffmpeg, ct);
    }
    finally { try { Directory.Delete(work, recursive: true); } catch { } }
});

// 3a. List previously finalized videos (mp4 blobs in the container) with fresh SAS URLs.
app.MapGet("/api/videos", async (IConfiguration cfg, TokenCredential cred, CancellationToken ct,
    int? limit) =>
{
    var account = Cfg(cfg, "STORAGE_ACCOUNT");
    var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
    var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
    var max = Math.Clamp(limit ?? 50, 1, 200);

    var udk = await svc.GetUserDelegationKeyAsync(
        DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2), ct);

    var items = new List<object>();
    await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, cancellationToken: ct))
    {
        if (!b.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = b.Name,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(2)
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        var token = sas.ToSasQueryParameters(udk.Value, account).ToString();
        var url = $"https://{account}.blob.core.windows.net/{container.Name}/{Uri.EscapeDataString(b.Name).Replace("%2F", "/")}?{token}";
        items.Add(new
        {
            name = b.Name,
            url,
            size = b.Properties.ContentLength,
            createdOn = b.Properties.CreatedOn,
            lastModified = b.Properties.LastModified
        });
    }
    var sorted = items
        .OrderByDescending(x => ((dynamic)x).createdOn ?? ((dynamic)x).lastModified)
        .Take(max)
        .ToList();
    return Results.Ok(new { count = sorted.Count, videos = sorted });
});

// 3b. Stitch: concatenate N MP4 clips into a single combined MP4 with optional crossfade.
//     Body: { urls: string[], crossfade?: number /* seconds, 0 = hard cut, 0.3-0.5 typical */ }
//     Strategy:
//       - crossfade<=0: lossless concat-demuxer (fast, no re-encode). Hard cut, but our
//         last-frame anchor + plan-based continuity make these near-invisible.
//       - crossfade>0 : ffmpeg xfade for video + acrossfade for audio. Re-encode required.
app.MapPost("/api/stitch", async (StitchRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    if (req?.Urls is null || req.Urls.Length < 2)
        return Results.BadRequest(new { error = "Provide at least 2 URLs to stitch." });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null)
        return Results.Problem("ffmpeg binary not found. Expected at ./bin/ffmpeg (bundled by CI).");

    var crossfade = Math.Clamp(req.Crossfade ?? 0.0, 0.0, 1.0);
    var work = Path.Combine(Path.GetTempPath(), $"stitch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        // 1. Download each clip to local temp.
        var http = hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var locals = new List<string>();
        for (int i = 0; i < req.Urls.Length; i++)
        {
            var local = Path.Combine(work, $"in{i:D3}.mp4");
            using var s = await http.GetStreamAsync(req.Urls[i], ct);
            await using var f = File.Create(local);
            await s.CopyToAsync(f, ct);
            locals.Add(local);
        }

        var outPath = Path.Combine(work, "out.mp4");
        string args;
        if (crossfade <= 0.0)
        {
            // Lossless concat-demuxer. All inputs must share codec/resolution/timebase
            // (true for clips from the same Sora deployment with the same size param).
            var listPath = Path.Combine(work, "list.txt");
            await File.WriteAllLinesAsync(listPath,
                locals.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            args = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy -movflags +faststart \"{outPath}\"";
        }
        else
        {
            // Probe each input duration so xfade offsets align.
            var durations = new List<double>();
            foreach (var p in locals) durations.Add(await ProbeDurationAsync(ffmpeg, p, ct));

            var sb = new StringBuilder();
            sb.Append("-y ");
            for (int i = 0; i < locals.Count; i++) sb.Append($"-i \"{locals[i]}\" ");

            // Build filter_complex: chain xfade and acrossfade through inputs.
            //   v0,v1 -> xfade=offset=d0-cf : vx1 ; vx1,v2 -> xfade=offset=(d0+d1-2cf)-cf : vx2 ...
            var fc = new StringBuilder();
            double cumulative = 0;
            string prevV = "[0:v]"; string prevA = "[0:a]";
            for (int i = 1; i < locals.Count; i++)
            {
                var offset = cumulative + durations[i - 1] - crossfade;
                var vTag = $"[vx{i}]";
                var aTag = $"[ax{i}]";
                fc.Append($"{prevV}[{i}:v]xfade=transition=fade:duration={crossfade.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}:offset={offset.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}{vTag};");
                fc.Append($"{prevA}[{i}:a]acrossfade=d={crossfade.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}{aTag};");
                prevV = vTag; prevA = aTag;
                cumulative += durations[i - 1] - crossfade;
            }
            // Trim trailing semicolon.
            var fcStr = fc.ToString().TrimEnd(';');
            sb.Append($"-filter_complex \"{fcStr}\" ");
            sb.Append($"-map \"{prevV}\" -map \"{prevA}\" ");
            sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p ");
            sb.Append("-c:a aac -b:a 160k -movflags +faststart ");
            sb.Append($"\"{outPath}\"");
            args = sb.ToString();
        }

        var (ec, stderr) = await RunProcessAsync(ffmpeg, args, ct);
        if (ec != 0 || !File.Exists(outPath))
            return Results.Problem($"ffmpeg failed (exit {ec}): {stderr.Substring(0, Math.Min(stderr.Length, 1500))}");

        var mp4 = await File.ReadAllBytesAsync(outPath, ct);
        var account = Cfg(cfg, "STORAGE_ACCOUNT");
        var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
        var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-stitched-{req.Urls.Length}clips.mp4";
        using var ms = new MemoryStream(mp4);
        var url = await UploadAndSign(container, name, ms, "video/mp4", svc, account, ct);
        return Results.Ok(new { url, blob = name, clips = req.Urls.Length, crossfade, bytes = mp4.Length });
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

// Lay a narration audio track over a finished film. Used by Story Chain to mux
// the AI/user voiceover onto the stitched video. Default "replace" maps the
// narration as the only audio stream (the silent Wan2.2 clips have none), so it
// is safe even when the video carries no audio track. -shortest clamps to the
// shorter of film/narration.
app.MapPost("/api/mux-audio", async (MuxAudioRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req?.VideoUrl) || string.IsNullOrWhiteSpace(req?.AudioUrl))
        return Results.BadRequest(new { error = "videoUrl and audioUrl are required" });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null)
        return Results.Problem("ffmpeg binary not found. Expected at ./bin/ffmpeg (bundled by CI).");

    var work = Path.Combine(Path.GetTempPath(), $"mux-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var http = hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var vIn = Path.Combine(work, "in.mp4");
        var aIn = Path.Combine(work, "in.mp3");
        using (var s = await http.GetStreamAsync(req.VideoUrl, ct))
        await using (var f = File.Create(vIn)) await s.CopyToAsync(f, ct);
        using (var s = await http.GetStreamAsync(req.AudioUrl, ct))
        await using (var f = File.Create(aIn)) await s.CopyToAsync(f, ct);

        var outPath = Path.Combine(work, "out.mp4");
        var mode = (req.Mode ?? "replace").ToLowerInvariant();
        string args = mode == "mix"
            ? $"-y -i \"{vIn}\" -i \"{aIn}\" -filter_complex \"[0:a][1:a]amix=inputs=2:duration=longest[a]\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -movflags +faststart \"{outPath}\""
            : $"-y -i \"{vIn}\" -i \"{aIn}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest -movflags +faststart \"{outPath}\"";

        var (ec, stderr) = await RunProcessAsync(ffmpeg, args, ct);
        if (ec != 0 || !File.Exists(outPath))
            return Results.Problem($"ffmpeg mux failed (exit {ec}): {stderr.Substring(0, Math.Min(stderr.Length, 1500))}");

        var mp4 = await File.ReadAllBytesAsync(outPath, ct);
        var account = Cfg(cfg, "STORAGE_ACCOUNT");
        var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
        var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-narrated.mp4";
        using var ms = new MemoryStream(mp4);
        var url = await UploadAndSign(container, name, ms, "video/mp4", svc, account, ct);
        return Results.Ok(new { url, blob = name, bytes = mp4.Length });
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

// Concatenate several narration/dialogue MP3s into ONE audio track (dialogue first, then
// narration) with a short gap between segments, so a single clip can carry BOTH a spoken
// dialogue line and a narrator voiceover. Returns a WAV url (pcm — always encodable by the
// bundled static ffmpeg); /api/mux-audio re-encodes it to AAC when laying it over the video.
app.MapPost("/api/concat-audio", async (ConcatAudioRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    var urls = (req?.AudioUrls ?? Array.Empty<string>())
        .Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
    if (urls.Length == 0)
        return Results.BadRequest(new { error = "audioUrls is required" });
    if (urls.Length == 1)
        return Results.Ok(new { url = urls[0], single = true });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null)
        return Results.Problem("ffmpeg binary not found. Expected at ./bin/ffmpeg (bundled by CI).");

    var gap = Math.Clamp(req?.GapMs ?? 300, 0, 2000) / 1000.0;
    var gapStr = gap.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    var work = Path.Combine(Path.GetTempPath(), $"concat-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var http = hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        var inputs = new StringBuilder();
        var filter = new StringBuilder();
        for (int i = 0; i < urls.Length; i++)
        {
            var f = Path.Combine(work, $"in{i}.mp3");
            using (var s = await http.GetStreamAsync(urls[i], ct))
            await using (var fs = File.Create(f)) await s.CopyToAsync(fs, ct);
            inputs.Append($"-i \"{f}\" ");
            var pad = i < urls.Length - 1 ? $",apad=pad_dur={gapStr}" : "";
            filter.Append($"[{i}:a]aresample=24000,aformat=sample_fmts=s16:channel_layouts=mono{pad}[a{i}];");
        }
        for (int i = 0; i < urls.Length; i++) filter.Append($"[a{i}]");
        filter.Append($"concat=n={urls.Length}:v=0:a=1[a]");

        var outPath = Path.Combine(work, "out.wav");
        var args = $"-y {inputs}-filter_complex \"{filter}\" -map \"[a]\" -c:a pcm_s16le \"{outPath}\"";
        var (ec, stderr) = await RunProcessAsync(ffmpeg, args, ct);
        if (ec != 0 || !File.Exists(outPath))
            return Results.Problem($"ffmpeg concat failed (exit {ec}): {stderr.Substring(0, Math.Min(stderr.Length, 1500))}");

        var wav = await File.ReadAllBytesAsync(outPath, ct);
        var account = Cfg(cfg, "STORAGE_ACCOUNT");
        var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
        var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
        var name = $"narration/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.wav";
        using var ms = new MemoryStream(wav);
        var url = await UploadAndSign(container, name, ms, "audio/wav", svc, account, ct);
        return Results.Ok(new { url, blob = name, bytes = wav.Length });
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

static string? LocateFfmpeg()
{
    // 1. Bundled ./bin/ffmpeg next to the app DLL.
    var local = Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg");
    if (File.Exists(local)) return local;
    // 2. PATH (handy for local dev with apt-installed ffmpeg).
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in path.Split(Path.PathSeparator))
    {
        try
        {
            var cand = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(cand)) return cand;
        }
        catch { }
    }
    return null;
}

static async Task<(int exitCode, string stderr)> RunProcessAsync(string exe, string args, CancellationToken ct)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = exe,
        Arguments = args,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    var errTask = p.StandardError.ReadToEndAsync(ct);
    var outTask = p.StandardOutput.ReadToEndAsync(ct);
    await p.WaitForExitAsync(ct);
    return (p.ExitCode, await errTask + "\n" + await outTask);
}

static async Task<double> ProbeDurationAsync(string ffmpeg, string file, CancellationToken ct)
{
    // Use ffmpeg itself (we don't ship ffprobe). Parse "Duration: HH:MM:SS.xx" from stderr.
    var (_, err) = await RunProcessAsync(ffmpeg, $"-i \"{file}\" -hide_banner", ct);
    var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
    if (!m.Success) return 12.0; // sane fallback
    return int.Parse(m.Groups[1].Value) * 3600
         + int.Parse(m.Groups[2].Value) * 60
         + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
}

// 3b. Identity bible: extract 3 stills from a finalized clip, send to gpt-4o(-mini) vision,
//     and return a hyper-detailed character/scene descriptor that replaces the imagined
//     anchors for subsequent segments. This locks identity to what Sora *actually* drew.
app.MapPost("/api/identity", async (IdentityRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.VideoUrl))
        return Results.BadRequest(new { error = "videoUrl is required" });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null)
        return Results.Problem("ffmpeg binary not found.");

    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["VISION_DEPLOYMENT"] ?? cfg["CHAT_DEPLOYMENT"] ?? "gpt-4o-mini";

    var work = Path.Combine(Path.GetTempPath(), $"id-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var http = hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var local = Path.Combine(work, "in.mp4");
        using (var s = await http.GetStreamAsync(req.VideoUrl, ct))
        await using (var f = File.Create(local))
            await s.CopyToAsync(f, ct);

        var dur = await ProbeDurationAsync(ffmpeg, local, ct);
        var stamps = new[] { dur * 0.10, dur * 0.50, dur * 0.90 };
        var jpgs = new List<string>();
        for (int i = 0; i < stamps.Length; i++)
        {
            var jp = Path.Combine(work, $"f{i}.jpg");
            // -ss before -i = fast keyframe seek; -frames:v 1 = single still; -q:v 2 = high quality JPEG.
            var (ec, err) = await RunProcessAsync(ffmpeg,
                $"-y -ss {stamps[i].ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} -i \"{local}\" -frames:v 1 -q:v 2 \"{jp}\"", ct);
            if (ec == 0 && File.Exists(jp)) jpgs.Add(jp);
        }
        if (jpgs.Count == 0)
            return Results.Problem("Failed to extract any frames from video.");

        // Build chat-completions payload with inline base64 image_url parts.
        var contentParts = new List<object>
        {
            new { type = "text", text = "Three stills from the same generated video clip (10%, 50%, 90% of duration). Describe the SINGLE on-screen character and their environment as a locked identity bible to be reused verbatim in later video generation prompts." }
        };
        foreach (var jp in jpgs)
        {
            var bytes = await File.ReadAllBytesAsync(jp, ct);
            var b64 = Convert.ToBase64String(bytes);
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "high" }
            });
        }

        var sys = @"You are a continuity supervisor for a film. From the three reference stills you receive, produce a strictly-factual identity bible to lock the character and setting across follow-up shots.

Return ONE JSON object, no prose, no markdown, exactly this shape:
{
  ""characterDescriptor"": ""<2-3 sentences. Concrete nouns only. Pin: apparent gender, age range, ethnicity if visually evident, face shape, eye color, eyebrow shape, hair (length, texture, color, parting/style), skin tone, build, height impression, any visible marks/jewellery. NO names, NO emotion, NO action verbs.>"",
  ""wardrobe"": ""<1-2 sentences listing every visible garment and accessory with colour and texture, top-to-bottom. e.g. 'Faded indigo denim shorts mid-thigh; tan ribbed cotton crop top; bare feet; thin gold ankle bracelet on left ankle.'>"",
  ""distinctiveTraits"": ""<3-5 NON-FACE visual signatures that let a viewer spot identity drift in later clips. Comma-separated. Each must be a concrete redrawable element: a high-contrast saturated colour item, a unique accessory, a prop, a scar/birthmark/tattoo with location, a hair ornament. e.g. 'bright cobalt-blue bandana wrapped on right wrist; small crescent scar above left eyebrow; faded denim jacket with embroidered yellow sun on left chest pocket.' Avoid traits that depend on face geometry alone.>"",
  ""sceneDescriptor"": ""<2-3 sentences. Environment, time of day, weather, geometry, prominent props. Concrete nouns only.>"",
  ""palette"": [""<hex or named anchor color 1>"", ""<...>"", ""<...3-5 total>""],
  ""lensLightingNotes"": ""<one sentence: lens look (focal length feel, DOF), key light direction and quality, color grade.>""
}

Rules:
- Use only what is visibly present in the stills. Do NOT invent details.
- If a detail is ambiguous across the three stills (e.g. hair length partially obscured), pick the clearest still and note nothing about the others.
- distinctiveTraits is the CONSISTENCY LIFELINE: pick traits that survive small drift and are easy to verify visually.
- Output JSON only. No markdown, no commentary.";

        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = contentParts.ToArray() }
            },
            // gpt-5-mini doesn't support `max_tokens` or non-default `temperature`.
            max_completion_tokens = 700,
            response_format = new { type = "json_object" }
        };

        var client = hf.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
        msg.Headers.Add("api-key", key);
        msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return Results.Problem($"Vision call failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "{}";
        object? bible;
        try { bible = JsonSerializer.Deserialize<object>(raw); }
        catch { return Results.Problem($"Vision returned non-JSON: {raw}"); }

        return Results.Ok(new { bible, stillsExtracted = jpgs.Count, durationSec = dur });
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch { }
    }
});

// 3c. Tail: cut last N seconds (default 4) of a finalized clip — special case of /api/slice
//     wired to the EOF. Kept as a thin wrapper for clarity at call sites.
app.MapPost("/api/tail", async (TailRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
{
    var seconds = Math.Clamp(req.Seconds ?? 4.0, 1.0, 5.0);
    var slice = new SliceRequest(req.VideoUrl, null, seconds, req.Size, true);
    return await SliceImpl(slice, hf, cfg, cred, ct);
});

// 3c2. Slice: cut an arbitrary [startSec, startSec+durationSec) window from a finalized
//      clip, re-encoded at the requested size, and upload to blob. Used for the
//      master-reel approach (#2): generate one establishing 20s reel of the character,
//      then slice multiple 4s windows as input_reference anchors for each follow-up clip.
//      Sora-2 caps input_reference video at 5s, so durationSec is clamped to [1, 5].
app.MapPost("/api/slice", async (SliceRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct) =>
    await SliceImpl(req, hf, cfg, cred, ct));

static async Task<IResult> SliceImpl(SliceRequest req, IHttpClientFactory hf,
    IConfiguration cfg, TokenCredential cred, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.VideoUrl))
        return Results.BadRequest(new { error = "videoUrl is required" });

    var ffmpeg = LocateFfmpeg();
    if (ffmpeg is null) return Results.Problem("ffmpeg binary not found.");

    var dur = Math.Clamp(req.DurationSec ?? 4.0, 1.0, 5.0);
    var size = string.IsNullOrWhiteSpace(req.Size) ? "1280x720" : req.Size!;
    var sizeMatch = System.Text.RegularExpressions.Regex.Match(size, @"^(\d+)x(\d+)$");
    if (!sizeMatch.Success) return Results.BadRequest(new { error = "size must be WxH" });
    var W = int.Parse(sizeMatch.Groups[1].Value);
    var H = int.Parse(sizeMatch.Groups[2].Value);

    var work = Path.Combine(Path.GetTempPath(), $"slice-{Guid.NewGuid():N}");
    Directory.CreateDirectory(work);
    try
    {
        var http = hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        var local = Path.Combine(work, "in.mp4");
        using (var s = await http.GetStreamAsync(req.VideoUrl, ct))
        await using (var f = File.Create(local))
            await s.CopyToAsync(f, ct);

        var outPath = Path.Combine(work, "slice.mp4");
        var d2 = dur.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        string seekArgs;
        if (req.FromEnd == true || req.StartSec is null)
        {
            // Seek N seconds before EOF.
            seekArgs = $"-sseof -{d2}";
        }
        else
        {
            var s2 = Math.Max(0, req.StartSec.Value).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            seekArgs = $"-ss {s2}";
        }
        var args =
            $"-y {seekArgs} -i \"{local}\" -t {d2} " +
            $"-vf \"scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2\" " +
            $"-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p " +
            $"-c:a aac -b:a 128k -movflags +faststart \"{outPath}\"";
        var (ec, err) = await RunProcessAsync(ffmpeg, args, ct);
        if (ec != 0 || !File.Exists(outPath))
            return Results.Problem($"ffmpeg slice failed (exit {ec}): {err.Substring(0, Math.Min(err.Length, 1500))}");

        var mp4 = await File.ReadAllBytesAsync(outPath, ct);
        var account = Cfg(cfg, "STORAGE_ACCOUNT");
        var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), cred);
        var container = svc.GetBlobContainerClient(cfg["STORAGE_CONTAINER"] ?? "videos");
        var tag = req.FromEnd == true ? "tail" : "slice";
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{tag}-{Guid.NewGuid():N}.mp4";
        using var ms = new MemoryStream(mp4);
        var url = await UploadAndSign(container, name, ms, "video/mp4", svc, account, ct);
        return Results.Ok(new { url, blob = name, seconds = dur, startSec = req.StartSec, fromEnd = req.FromEnd ?? (req.StartSec is null), bytes = mp4.Length });
    }
    finally
    {
        try { Directory.Delete(work, recursive: true); } catch { }
    }
}

// 3d. Remix: NOT SUPPORTED on Wan2.2 (no native remix endpoint). Returns 410 Gone
//     so the frontend can show a friendly "not available" message. A future
//     implementation could fake remix via image-to-video using the source clip's
//     last frame, but that needs a separate I2V workflow and is out of scope here.
app.MapPost("/api/remix", (RemixRequest req) =>
    Results.Json(new
    {
        error = "remix is not supported on Wan2.2-TI2V-5B",
        hint = "submit a new prompt via /api/jobs; image-to-video remix may land later"
    }, statusCode: 410));

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
    var langPrefix = lang.Substring(0, 2).ToLowerInvariant();

    // Auto-translate to target language if voice is non-English (Bengali, Hindi, etc.)
    var text = req.Text!;
    if (langPrefix != "en")
    {
        var client0 = hf.CreateClient();
        client0.Timeout = TimeSpan.FromSeconds(30);
        using var trMsg = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={langPrefix}");
        trMsg.Headers.Add("Ocp-Apim-Subscription-Key", key);
        trMsg.Headers.Add("Ocp-Apim-Subscription-Region", region);
        trMsg.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8, "application/json");
        using var trResp = await client0.SendAsync(trMsg, ct);
        if (trResp.IsSuccessStatusCode)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await trResp.Content.ReadAsStringAsync(ct));
            var translated = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
            if (!string.IsNullOrWhiteSpace(translated)) text = translated!;
        }
    }

    var ssml = $@"<speak version='1.0' xml:lang='{lang}'>
<voice name='{voice}'>{System.Security.SecurityElement.Escape(text)}</voice>
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
    using var ms = new MemoryStream(mp3);
    var url = await UploadAndSign(container, name, ms, "audio/mpeg", svc, account, ct);
    return Results.Ok(new { url, voice, lang, translated = (text != req.Text!) ? text : null, length = mp3.Length });
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

// Translate arbitrary text to a target language code (e.g. "bn", "hi").
app.MapPost("/api/translate", async (TranslateRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.To))
        return Results.BadRequest(new { error = "text and to are required" });
    var key = Cfg(cfg, "AOAI_KEY");
    var region = Cfg(cfg, "SPEECH_REGION");
    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(30);
    using var msg = new HttpRequestMessage(HttpMethod.Post,
        $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(req.To!)}");
    msg.Headers.Add("Ocp-Apim-Subscription-Key", key);
    msg.Headers.Add("Ocp-Apim-Subscription-Region", region);
    msg.Content = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(new[] { new { Text = req.Text } }),
        Encoding.UTF8, "application/json");
    using var resp = await client.SendAsync(msg, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        return Results.Problem($"Translate failed ({(int)resp.StatusCode}): {body}");
    using var doc = System.Text.Json.JsonDocument.Parse(body);
    var translated = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
    return Results.Ok(new { translated, to = req.To });
});

// Enhance: take user idea + narration + target voice/language and produce
// a Sora-2-optimized prompt with the narration embedded in the right language.
app.MapPost("/api/enhance", async (EnhanceRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Prompt))
        return Results.BadRequest(new { error = "prompt is required" });

    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["CHAT_DEPLOYMENT"] ?? "gpt-4o-mini";
    var voice = req.Voice ?? "en-US-AvaMultilingualNeural";
    var localeMatch = System.Text.RegularExpressions.Regex.Match(voice, @"^([a-z]{2})-([A-Z]{2})");
    var langPrefix = localeMatch.Success ? localeMatch.Groups[1].Value : "en";
    var langName = langPrefix switch { "hi" => "Hindi", "bn" => "Bengali", "en" => "English", _ => langPrefix };

    var totalSeconds = req.Seconds ?? 8;
    var size = req.Size ?? "1280x720";

    // Sora-2 renders in 4/8/12s chunks; longer durations are stitched. The prompt
    // describes ONE chunk (so beat timestamps line up with what Sora actually renders),
    // while the LLM is told the total duration + segment count so it can plan the arc.
    var chunkSeconds = Math.Min(totalSeconds, 12);
    var segmentCount = (int)Math.Ceiling(totalSeconds / (double)chunkSeconds);
    // Beat count: ~1 beat per 1.5s within the chunk, scaled up a bit when the overall
    // story is longer so each segment carries more visible action density.
    var baseBeats = Math.Clamp(chunkSeconds / 2, 3, 6);
    var bonusBeats = totalSeconds >= 36 ? 2 : (totalSeconds >= 24 ? 1 : 0);
    var beatCount = Math.Min(8, baseBeats + bonusBeats);
    var seconds = chunkSeconds;

    var translatedNarration = req.Narration ?? "";
    if (!string.IsNullOrWhiteSpace(req.Narration) && langPrefix != "en")
    {
        // Translate narration first
        var region = Cfg(cfg, "SPEECH_REGION");
        var trClient = hf.CreateClient();
        trClient.Timeout = TimeSpan.FromSeconds(20);
        using var trMsg = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={langPrefix}");
        trMsg.Headers.Add("Ocp-Apim-Subscription-Key", key);
        trMsg.Headers.Add("Ocp-Apim-Subscription-Region", region);
        trMsg.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new[] { new { Text = req.Narration } }),
            Encoding.UTF8, "application/json");
        using var trResp = await trClient.SendAsync(trMsg, ct);
        if (trResp.IsSuccessStatusCode)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await trResp.Content.ReadAsStringAsync(ct));
            translatedNarration = doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? req.Narration!;
        }
    }

    var voiceShort = voice.Split('-').Length >= 3 ? voice.Split('-')[2].Replace("Neural", "") : "";
    var gender = System.Text.RegularExpressions.Regex.IsMatch(voice,
        @"(male|kunal|aarav|prabhat|madhur|rehaan|arjun|adam|andrew|alloy|echo|bashkar|niranjan|gagan|midhun|manohar|ojas|valluvar|mohan|salman)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "male" : "female";

    var sys = $@"You are a senior film director planning a short film for OpenAI's Sora-2 (text-to-video with synchronized native audio, dialogue and lip-sync).
The film is {totalSeconds}s long, rendered as {segmentCount} sequential segment(s) of up to {chunkSeconds}s each that will be stitched together. Plan the WHOLE arc, then describe each segment so it FLOWS into the next with identical character, wardrobe, environment, lens, lighting, color grade, voice timbre and ambient sound.

Return a SINGLE valid JSON object — no prose, no markdown — matching this schema EXACTLY:

{{
  ""style"": ""<one line, film stock + grade + aesthetic. e.g. 'Photorealistic, Arri Alexa, 35mm anamorphic, warm naturalistic grade, shallow DOF, subtle film grain.'>"",
  ""characterAnchor"": ""<3-5 distinctive repeatable details in one sentence: gender/age/hair/wardrobe/build. This exact string will be PASTED VERBATIM into every segment, so make it concrete.>"",
  ""distinctiveTraits"": ""<3-5 NON-FACE identifiers that survive AI drift, comma-separated. Each must be a CONCRETE visible signature: distinctive clothing item with colour and texture, accessory, prop carried, scar/birthmark/tattoo with location, hair ornament, footwear with detail, and at least ONE high-contrast saturated colour element (e.g. 'bright cobalt-blue bandana on left wrist'). These are what locks identity across clips when faces drift; pick traits easy to redraw and easy to spot. Avoid anything face-shape or skin-tone dependent.>"",
  ""sceneAnchor"": ""<2-3 sentences: environment with concrete nouns, time of day, weather, palette of 3-5 anchor colors, 1-2 small ambient details. Pasted verbatim into every segment.>"",
  ""cinematography"": ""<multi-line block: 'Camera shot: ...\nCamera motion: ...\nLens: 50mm spherical, f/2.0, shallow DOF\nLighting: ...\nMood: ...' — keep the lens/lighting/grade IDENTICAL across all segments by stating them once here.>"",
  ""voiceDescription"": ""<one sentence describing the speaker's vocal character: gender ({gender}), age range, timbre, pace, accent ({langName}). Pasted verbatim into every segment so Sora produces a consistent voice.>"",
  ""backgroundSound"": ""<2-4 concrete diegetic sounds matching the environment, comma-separated. Pasted verbatim into every segment so the ambient bed continues.>"",
  ""negative"": ""no on-screen text, no captions, no logos, no watermarks, no extra speakers, no duplicated limbs"",
  ""segments"": [
    {{
      ""index"": 1,
      ""startSec"": 0,
      ""endSec"": {chunkSeconds},
      ""summary"": ""<one short sentence describing what happens in this segment, written in past tense from the perspective of a later segment looking back. e.g. 'She entered the kitchen and reached for the pan.'>"",
      ""beats"": [
        ""Beat 1 (0–Xs): <ONE concrete physical action with counted movement>"",
        ""Beat 2 (Xs–Ys): <next beat>"",
        ""... 4-6 beats total covering the segment's duration in order""
      ],
      ""dialogue"": ""<the speaker label + verbatim narration line if it is spoken in THIS segment, else null. Distribute the supplied narration across segments so the natural reading pace fits — short clips get one phrase, long clips can carry the whole line. NEVER invent dialogue beyond what the user supplied.>""
    }}
    // ... exactly {segmentCount} segments, each with its OWN distinct beats so the story progresses. Do NOT repeat beats across segments. Segment 1 establishes; later segments develop and resolve.
  ]
}}

Hard rules:
- Output ONLY the JSON object. No markdown fences. No prose.
- The five anchors (style/characterAnchor/distinctiveTraits/sceneAnchor/cinematography/voiceDescription/backgroundSound) MUST stay constant across segments — they describe the unchanging world. The segments array carries the changing action.
- distinctiveTraits is a CONSISTENCY LIFELINE: when a viewer looks at clip 5 they should still see the same wrist-bandana, same scar location, same prop. Be specific.
- Every segment's beats describe DISTINCT progression. Segment N must logically continue from segment N-1's last beat.
- Dialogue must be embedded verbatim in the supplied script; never translate it; never invent extra lines.
- {beatCount} beats per segment is the target.";

    var userMsg = $@"User idea: {req.Prompt}
Total story duration: {totalSeconds} seconds = {segmentCount} segment(s) of up to {chunkSeconds}s each.
Aspect: {size}
Narration language: {langName}
Speaker gender: {gender}
Voice persona name: {voiceShort} (informs vocal character — never mention on-screen).
Narration line to embed verbatim (already in {langName}, distribute across segments naturally): {translatedNarration}

Plan the {segmentCount}-segment arc as JSON now.";

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(90);
    using var chatMsg = new HttpRequestMessage(HttpMethod.Post,
        $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
    chatMsg.Headers.Add("api-key", key);
    var payload = new
    {
        messages = new object[] {
            new { role = "system", content = sys },
            new { role = "user",   content = userMsg }
        },
        // gpt-5-mini doesn't support `max_tokens` or non-default `temperature`.
        max_completion_tokens = 2400,
        response_format = new { type = "json_object" }
    };
    chatMsg.Content = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(payload),
        Encoding.UTF8, "application/json");
    using var chatResp = await client.SendAsync(chatMsg, ct);
    var chatBody = await chatResp.Content.ReadAsStringAsync(ct);
    if (!chatResp.IsSuccessStatusCode)
        return Results.Problem($"Enhance failed ({(int)chatResp.StatusCode}): {chatBody}");

    using var chatDoc = System.Text.Json.JsonDocument.Parse(chatBody);
    var rawContent = chatDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "{}";

    // Parse the structured plan. If parsing fails, fall back to returning rawContent as the flat prompt.
    System.Text.Json.JsonElement planJson;
    try { planJson = System.Text.Json.JsonDocument.Parse(rawContent).RootElement.Clone(); }
    catch
    {
        return Results.Ok(new { enhanced = rawContent, plan = (object?)null, translatedNarration, lang = langPrefix, langName });
    }

    string J(System.Text.Json.JsonElement e, string k, string fb = "") =>
        e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? fb) : fb;

    // Server-side render of a human-readable preview (for the "Final prompt" textarea).
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Style: {J(planJson, "style")}");
    sb.AppendLine();
    sb.AppendLine($"Scene: {J(planJson, "sceneAnchor")}");
    sb.AppendLine();
    sb.AppendLine($"Character: {J(planJson, "characterAnchor")}");
    sb.AppendLine();
    sb.AppendLine("Cinematography:");
    sb.AppendLine(J(planJson, "cinematography"));
    sb.AppendLine();
    sb.AppendLine($"Voice: {J(planJson, "voiceDescription")}");
    sb.AppendLine();
    if (planJson.TryGetProperty("segments", out var segArr) && segArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        int i = 0;
        foreach (var seg in segArr.EnumerateArray())
        {
            i++;
            var startSec = seg.TryGetProperty("startSec", out var ss) && ss.ValueKind == System.Text.Json.JsonValueKind.Number ? ss.GetInt32() : 0;
            var endSec = seg.TryGetProperty("endSec", out var es) && es.ValueKind == System.Text.Json.JsonValueKind.Number ? es.GetInt32() : 0;
            sb.AppendLine($"Segment {i} ({startSec}–{endSec}s) — {J(seg, "summary")}");
            if (seg.TryGetProperty("beats", out var bts) && bts.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var b in bts.EnumerateArray())
                    sb.AppendLine($"  - {b.GetString()}");
            var dlg = J(seg, "dialogue");
            if (!string.IsNullOrWhiteSpace(dlg) && dlg != "null") sb.AppendLine($"  Dialogue: {dlg}");
            sb.AppendLine();
        }
    }
    sb.AppendLine($"Background sound: {J(planJson, "backgroundSound")}");
    sb.AppendLine($"Negative: {J(planJson, "negative")}");

    return Results.Ok(new {
        enhanced = sb.ToString().Trim(),
        plan = System.Text.Json.JsonSerializer.Deserialize<object>(rawContent),
        translatedNarration, lang = langPrefix, langName,
        totalSeconds, chunkSeconds, segmentCount
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// Storyboard: story-driven per-clip prompts for the I2V continuation chain.
//
// The chain renders N = ceil(total / clipSeconds) clips of `clipSeconds` each
// (5s default → 12 clips for a 1-minute film). The whole user idea is spread
// across those clips as ONE evolving story with a beginning, development and
// resolution — never N copies of the same shot. Each clip carries a `motionPrompt`
// tuned for image-to-video: the FIRST frame is supplied (character image for clip
// 1, previous clip's last frame thereafter), so the prompt describes MOTION,
// action and camera — what CHANGES — while identity/wardrobe/scene stay locked by
// the incoming frame. `endState` documents what the clip's final frame should show
// so it hands off cleanly into the next clip.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/storyboard", async (StoryboardRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Prompt))
        return Results.BadRequest(new { error = "prompt is required" });

    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["CHAT_DEPLOYMENT"] ?? "gpt-4o-mini";

    var clipSeconds = Math.Clamp(req.ClipSeconds ?? 5, 3, 5);
    var totalSeconds = Math.Clamp(req.TotalSeconds ?? 60, clipSeconds, 120);
    var clipCount = Math.Clamp((int)Math.Ceiling(totalSeconds / (double)clipSeconds), 1, 24);
    var size = req.Size ?? "832x480";
    var language = string.IsNullOrWhiteSpace(req.Language) ? "English" : req.Language!.Trim();
    var narrate = req.Narrate ?? true;
    var dialogDirection = (req.DialogDirection ?? "").Trim();
    var dialogGuidance = string.IsNullOrWhiteSpace(dialogDirection)
        ? "No dialogue direction was given — invent natural, in-character lines yourself, ALWAYS in subtext (never on-the-nose). Give MOST clips a spoken line; leave dialog empty only for a rare, clearly wordless beat."
        : $@"DIALOGUE DIRECTION from the user: ""{dialogDirection}"". Treat this as the underlying MOOD / situation to convey through SUBTEXT and behaviour across the whole film — do NOT quote it back as a spoken line (e.g. if the note is 'he is sad', NEVER write ""I'm sad""; instead let a line like 'Nobody stopped tonight.' plus the narration earn that feeling). Every spoken line in {language}, in sync with its clip's action and narration.";
    var narrationRule = narrate
        ? $@"AUDIO — write ONE film script = NARRATION (voiceover) + DIALOGUE (spoken on screen), composed TOGETHER with the visuals so sound and picture stay in sync. Write BOTH in {language} USING ITS NATIVE SCRIPT (Devanagari for Hindi, Bengali script for Bengali, etc.) — NEVER romanized/transliterated, never English; only real proper names may stay as-is.

NARRATION (narrator voiceover) — write a line for EVERY clip, never leave one empty:
- Do NOT just describe what is already on screen. Lines like 'he plays the guitar', 'he looks up and plays harder', 'he packs up and walks away' are BAD — they merely repeat the picture. Instead voice what the camera CANNOT show: the character's inner feeling, what the moment MEANS, a memory, a longing, or the story's theme.
- Read end to end, the clips' narration must form ONE continuous, complete, flowing voiceover — like a storyteller or a poem — each line following naturally from the last, no repeats, no restating the action.
- Keep every line short, evocative and varied (do not reuse the same sentence shape twice).

DIALOGUE (words spoken on screen):
- The EXACT words a character speaks in THIS clip — spoken words only, NO name prefix, NO quotation marks. Give MOST clips a spoken line: any clip where a present character could plausibly speak, mutter, react or address someone gets one; even a character who is ALONE can voice a thought aloud (do NOT default to silence just because they are alone or sad). Use an empty string ONLY for the rare beat that is clearly, intentionally wordless.
- NEVER on-the-nose: a character must NEVER state their own emotion or narrate their own action. 'I'm sad', 'I'm so happy', 'Thank you', 'I look up and play' are ALL BAD. Real people speak in SUBTEXT — imply the feeling indirectly through specific, natural, in-character words (e.g. instead of 'I'm sad' a busker might say 'Nobody stopped tonight.').
- Across the clips the spoken lines read as ONE natural, evolving exchange with real emotion and specific, human detail — never generic pleasantries.
- For every clip that HAS a dialog line, set `speaker` to the cast id (from `cast`) of the character who says it; use an empty string when dialog is empty.
- {dialogGuidance}
- Keep each line very SHORT; when a clip has BOTH narration and dialog, together they must be speakable within {clipSeconds} seconds (about {Math.Max(6, clipSeconds * 2)} words TOTAL).
- Keep title, style, action and motionPrompt in ENGLISH (the video model needs English). ONLY the narration and dialog fields are written in {language}'s native script."
        : @"AUDIO:
- Set every clip's narration AND dialog to an empty string. The user turned voiceover off.";

    var sys = $@"You are a film director and casting director planning a SINGLE continuous short film produced as a CHAIN of {clipCount} image-to-video clips, each {clipSeconds} seconds long, played back-to-back into one ~{clipCount * clipSeconds}-second film.

HOW THE PRODUCTION WORKS (critical — shapes how you must write this):
- BEFORE any clip is rendered, we generate ONE canonical reference image for EACH character and EACH location (a casting / location sheet).
- Each clip is then rendered image-to-video by seeding its first frame from the canonical reference(s) of the characters present in THAT clip — NOT from the previous clip. This locks every character's identity, face and wardrobe for the whole film and prevents drift.
- Therefore you MUST: (a) define a CAST and LOCATIONS up front with fixed, detailed visual descriptions, and (b) in every clip list which characters and which location appear, and briefly restate their key locked traits in the motion prompt.

CASTING RULES:
- Give every character a stable short id (e.g. 'hero', 'rival', 'mother'), a name, and a LOCKED description: age, build, skin tone, face, hair, wardrobe, and 1-2 distinguishing features. These never change across clips.
- Give every location a stable id, a name, and a LOCKED description: place, time of day, color palette, key set pieces, lighting. These never change.
- Support MULTIPLE characters; a clip may contain one or several of them.

STORY RULES:
- Spread the idea across all {clipCount} clips as ONE evolving story: clip 1 establishes; middle clips develop/rise; the final clip resolves. NEVER repeat an action — each clip MOVES THE STORY FORWARD.
- Exactly ONE clear, physically plausible action per clip (real gravity, weight, momentum, balance, natural human motion and timing). Do not cram multiple simultaneous actions into {clipSeconds} seconds.
- ACTING & EXPRESSION: direct it like a real film. In every clip the present character(s) show readable facial expression and emotion that fits the beat; when a character has a dialog line, they are visibly SPEAKING with natural lip movement and matching expression and gesture. Bake this acting direction (plus the physics above) INTO each clip's motionPrompt.
- Photorealistic. No on-screen text, captions, logos, or watermarks. One consistent visual style/lens/lighting (state once in `style`).

{narrationRule}

Return ONE JSON object, no markdown, exactly this shape:
{{
  ""title"": ""<short film title>"",
  ""logline"": ""<one sentence describing the whole arc>"",
  ""style"": ""<film stock + grade + lens + lighting, identical across all clips>"",
  ""setting"": ""<where the story takes place and how/if it travels>"",
  ""clipSeconds"": {clipSeconds},
  ""clipCount"": {clipCount},
  ""cast"": [
    {{ ""id"": ""hero"", ""name"": ""<name>"", ""description"": ""<LOCKED full visual: age, build, skin, face, hair, wardrobe, distinguishing features>"" }}
  ],
  ""locations"": [
    {{ ""id"": ""place1"", ""name"": ""<name>"", ""description"": ""<LOCKED: place, time of day, palette, set pieces, lighting>"" }}
  ],
  ""clips"": [
    {{
      ""index"": 1,
      ""title"": ""<3-5 word beat title>"",
      ""characters"": [""hero""],
      ""location"": ""place1"",
      ""action"": ""<one sentence: the single physical action in THIS clip>"",
      ""camera"": ""<one short phrase: camera move, e.g. 'slow push-in'>"",
      ""motionPrompt"": ""<40-110 words, ENGLISH. Name the present character(s) and briefly restate their locked look, place them in the named location, then describe the SINGLE physical action + camera move over {clipSeconds} seconds with realistic physics and natural human timing. ALWAYS state the character's facial expression/emotion; if this clip has a dialog line, say the character is speaking with natural lip movement and matching expression (do NOT write the spoken words). End with 'No on-screen text, captions, or watermarks.'>"",
      ""narration"": ""<{language} narrator voiceover line for this clip, present tense, short>"",
      ""dialog"": ""<{language} words a character speaks on screen in this clip — spoken words only, no name prefix; empty string if no one speaks>"",
      ""speaker"": ""<the cast id of the single character who speaks `dialog` in this clip; empty string when dialog is empty>"",
      ""endState"": ""<one sentence: what the last frame shows>""
    }}
  ]
}}

Output JSON only. Define every character in `cast` and every place in `locations`. Exactly {clipCount} clips, each listing its characters + location.";

    var creativeLine = (narrate && !string.IsNullOrWhiteSpace(dialogDirection))
        ? $"\n\nDialogue / creative direction to build the WHOLE film around: \"{dialogDirection}\" — write every spoken dialog line and the narration in {language}'s native script."
        : "";
    var user = $@"User film idea: {req.Prompt}{creativeLine}

Cast the characters and locations this idea needs (support multiple characters), then produce the {clipCount}-clip storyboard ({clipSeconds}s per clip, {clipCount * clipSeconds}s total, aspect {size}) as ONE continuous, cinematic story — a single physically-plausible action per clip performed with readable facial expression and natural acting, each clip listing which cast members and location appear{(narrate ? $", plus a short {language} narrator NARRATION line and (when a character speaks) a {language} DIALOG line per clip — all in {language}'s native script, in sync with that clip's action" : ", with narration and dialog left empty")}. Output JSON only.";

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(90);

    object BuildFallbackStoryboard()
    {
        var clean = (req.Prompt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "A cinematic story";
        var titleWords = string.Join(" ", clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5));
        var title = string.IsNullOrWhiteSpace(titleWords) ? "Continuous Story" : titleWords;
        var clips = Enumerable.Range(1, clipCount).Select(i =>
        {
            var phase = i == 1 ? "setup" : i == clipCount ? "resolution" : "development";
            var camera = i % 3 == 1 ? "slow push-in" : i % 3 == 2 ? "handheld follow" : "steady tracking";
            return new
            {
                index = i,
                title = $"Beat {i}",
                characters = new[] { "hero" },
                location = "place1",
                action = i == 1
                    ? "The main character is introduced with a clear establishing action."
                    : i == clipCount
                        ? "The action resolves into a clear, composed ending moment."
                        : "The action advances with a visible change that raises the stakes.",
                camera,
                motionPrompt = $"The main character is centered in the scene. Over {clipSeconds} seconds, perform a single {phase} action for this story idea: {clean}. Keep motion clear, progressive and physically plausible — real weight, gravity and natural human timing. Maintain the same cinematic style and lighting. No on-screen text, captions, or watermarks.",
                narration = i == 1
                    ? "Our story begins."
                    : i == clipCount ? "And so it ends." : "The moment builds.",
                dialog = "",
                speaker = "",
                endState = i == clipCount
                    ? "Final frame settles into a composed ending shot."
                    : "Final frame lands in a stable pose that can continue."
            };
        }).ToArray();

        return new
        {
            title,
            logline = clean,
            style = "photoreal cinematic look, natural color grade, consistent lensing and lighting",
            setting = "one continuous environment with smooth spatial continuity",
            clipSeconds,
            clipCount,
            cast = new[] { new { id = "hero", name = "Lead", description = "the main subject defined by the reference image; consistent face, hair and wardrobe throughout" } },
            locations = new[] { new { id = "place1", name = "Main Setting", description = "one continuous environment with consistent palette and lighting" } },
            clips
        };
    }

    // Pull the assistant message text out of a chat-completions response body.
    static string ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";
            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) sb.Append(part.GetString());
                    else if (part.ValueKind == JsonValueKind.Object
                             && part.TryGetProperty("text", out var txt)
                             && txt.ValueKind == JsonValueKind.String) sb.Append(txt.GetString());
                }
                return sb.ToString();
            }
        }
        catch { }
        return "";
    }

    // The storyboard JSON is large (full cast + per-clip motionPrompt + native-script
    // narration/dialog) and gpt-5-mini is a reasoning model, so a single call can
    // occasionally come back empty/truncated/unparseable on a transient hiccup. Try
    // twice before dropping to the bland fallback (which carries no dialogue).
    //
    // Benign ideas (e.g. an adult and a child in an innocent scene) sometimes trip the
    // Azure content filter. On a filter block we retry ONCE with `softUser` — an explicitly
    // WHOLESOME reframing of the same idea. This only ever makes the request tamer (it can
    // never push genuinely unsafe content through), so it rescues false positives without
    // bypassing moderation.
    var softUser = $@"Reimagine the following idea as a WHOLESOME, strictly family-friendly, non-graphic short film. Depict every character respectfully; keep all interactions clearly innocent, safe and age-appropriate; avoid any violent, sexual, self-harm or otherwise sensitive framing. Idea: {req.Prompt}{creativeLine}

Produce the {clipCount}-clip storyboard ({clipSeconds}s per clip, {clipCount * clipSeconds}s total, aspect {size}) as ONE continuous, cinematic story{(narrate ? $", plus a short {language} narrator line per clip and a {language} dialog line when a character speaks, in {language}'s native script" : ", with narration and dialog left empty")}. Output JSON only.";

    System.Text.Json.Nodes.JsonNode? story = null;
    bool soften = false;
    for (int attempt = 1; attempt <= 3 && story is null; attempt++)
    {
        string body;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post,
                $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
            msg.Headers.Add("api-key", key);
            var payload = new
            {
                messages = new object[] {
                    new { role = "system", content = sys },
                    new { role = "user",   content = soften ? softUser : user }
                },
                max_completion_tokens = 12000,
                response_format = new { type = "json_object" }
            };
            msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(msg, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500) && attempt < 3) continue;
                if ((int)resp.StatusCode == 400 &&
                    (body.Contains("content_filter") || body.Contains("ResponsibleAIPolicy")))
                {
                    // First filter block → retry once with the wholesome reframing.
                    if (!soften && attempt < 3) { soften = true; continue; }
                    return Results.Problem(statusCode: 422,
                        title: "Story blocked by content policy",
                        detail: "This idea was blocked by the content safety policy even after an automatic wholesome reframing. Try rephrasing the sensitive part — for example, state ages explicitly and make every interaction clearly innocent.");
                }
                return Results.Problem($"Storyboard failed ({(int)resp.StatusCode}): {body}");
            }
        }
        catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
        {
            continue;
        }

        var raw = ExtractContent(body).Trim();
        if (string.IsNullOrWhiteSpace(raw)) continue;
        var jsonStart = raw.IndexOf('{');
        var jsonEnd = raw.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            raw = raw.Substring(jsonStart, jsonEnd - jsonStart + 1);
        try { story = System.Text.Json.Nodes.JsonNode.Parse(raw); }
        catch { story = null; }
    }

    if (story is null)
    {
        var fallback = BuildFallbackStoryboard();
        return Results.Ok(new { story = fallback, clipSeconds, clipCount, totalSeconds, size, fallback = true });
    }

    // The per-clip duration is a PRODUCTION constant (Wan renders 1-5s clips), NOT the
    // model's choice. Force clipSeconds to the server-clamped value so e.g. a 45s film
    // never comes back as 2s clips, and keep clipCount in sync with the real clips array.
    if (story is System.Text.Json.Nodes.JsonObject sobj)
    {
        sobj["clipSeconds"] = clipSeconds;
        sobj["clipCount"] = (sobj["clips"] is System.Text.Json.Nodes.JsonArray sarr && sarr.Count > 0)
            ? sarr.Count : clipCount;
    }

    return Results.Ok(new { story, clipSeconds, clipCount, totalSeconds, size });
});

// (Re)generate per-clip SPOKEN DIALOGUE for an existing storyboard, in sync with each clip's
// action and narrator narration. Optional user `direction` steers what characters say; when it
// is empty the model invents natural in-character dialogue on its own. All lines in `language`.
app.MapPost("/api/dialogue", async (DialogueRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    var clips = (req?.Clips ?? Array.Empty<DialogueClip>()).Where(c => c is not null).ToArray();
    if (clips.Length == 0)
        return Results.BadRequest(new { error = "clips are required" });

    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["CHAT_DEPLOYMENT"] ?? "gpt-4o-mini";

    var language = string.IsNullOrWhiteSpace(req?.Language) ? "English" : req!.Language!.Trim();
    var direction = (req?.Direction ?? "").Trim();
    var directionRule = string.IsNullOrWhiteSpace(direction)
        ? "The user gave NO direction — invent natural, in-character spoken dialogue yourself from each clip's action and narration, always in subtext (never on-the-nose)."
        : $@"The user's dialogue direction is ""{direction}"" — treat it as the underlying MOOD / situation to convey through SUBTEXT across the film. Do NOT quote it back as a line (if it says 'he is sad', NEVER write ""I'm sad""; imply it, e.g. 'Nobody stopped tonight.'). Every line still in {language}.";

    var sys = $@"You are a screenwriter writing the SPOKEN DIALOGUE for a short film, in sync with an existing storyboard and its narrator voiceover.
Rules:
- For each clip that needs a line, write the EXACT words a character SPEAKS aloud on screen, in {language} USING ITS NATIVE SCRIPT (Devanagari for Hindi, Bengali script for Bengali, etc.) — NEVER romanized/transliterated, never English; only real proper names may stay as-is. Spoken words only — NO character-name prefix, NO quotation marks, NO stage directions.
- NEVER on-the-nose: a character must NEVER state their own emotion or narrate their own action. 'I'm sad', 'I'm so happy', 'Thank you', 'I play harder' are ALL BAD. Speak in SUBTEXT — imply the feeling indirectly through specific, natural, in-character words.
- Do NOT simply repeat the clip's narration or action back as speech; the line must add something the narration does not say.
- Keep each line SHORT (at most 12 words) so it is speakable within the clip and does not collide with that clip's narration.
- Give MOST clips a spoken line: any clip where the present character could plausibly speak, mutter, react or address someone gets one; even a lone or sad character can voice a thought aloud (do NOT default to silence). Use an empty string ONLY for a rare, intentionally wordless beat. Across the clips the lines must read as ONE continuous, evolving exchange with real emotion and specific human detail, never generic pleasantries.
- {directionRule}
Return ONE JSON object exactly: {{ ""dialogs"": [ {{ ""index"": <clip index>, ""dialog"": ""<{language} spoken line or empty>"" }} ] }} — one entry for EVERY clip index given, no markdown.";

    var clipLines = string.Join("\n", clips.Select(c =>
        $"- clip {c.Index}: action = {(c.Action ?? "").Trim()}; narration = {(c.Narration ?? "").Trim()}"));
    var user = $@"Language for all dialogue: {language}
Write the dialog for each clip index:
{clipLines}

Return the JSON now.";

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(60);
    using var msg = new HttpRequestMessage(HttpMethod.Post,
        $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
    msg.Headers.Add("api-key", key);
    var payload = new
    {
        messages = new object[] {
            new { role = "system", content = sys },
            new { role = "user",   content = user }
        },
        max_completion_tokens = 2000,
        response_format = new { type = "json_object" }
    };
    msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    using var resp = await client.SendAsync(msg, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        return Results.Problem($"Dialogue failed ({(int)resp.StatusCode}): {body}");

    string raw;
    try
    {
        using var doc = JsonDocument.Parse(body);
        raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
    }
    catch { raw = ""; }

    var js = raw.IndexOf('{'); var je = raw.LastIndexOf('}');
    if (js >= 0 && je > js) raw = raw.Substring(js, je - js + 1);

    try
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(raw);
        var arr = node?["dialogs"] as System.Text.Json.Nodes.JsonArray;
        var outList = new List<object>();
        if (arr is not null)
            foreach (var d in arr)
            {
                if (d is null) continue;
                int.TryParse(d["index"]?.ToString(), out var idx);
                var line = d["dialog"]?.ToString() ?? "";
                outList.Add(new { index = idx, dialog = line });
            }
        return Results.Ok(new { dialogs = outList });
    }
    catch
    {
        return Results.Problem("Dialogue model returned unparseable JSON.");
    }
});

// Audio prompter: write a spoken voiceover script for a finished Story Chain
// film. `prompt` (optional) steers tone/content; without it the AI narrates
// straight from the storyboard. Output is plain English — /api/narrate will
// translate to the chosen voice's language on synthesis.
app.MapPost("/api/narration-script", async (NarrationScriptRequest req, IHttpClientFactory hf,
    IConfiguration cfg, CancellationToken ct) =>
{
    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["CHAT_DEPLOYMENT"] ?? "gpt-4o-mini";

    var totalSeconds = Math.Clamp(req.TotalSeconds ?? 30, 3, 180);
    var wordBudget = Math.Max(8, (int)Math.Round(totalSeconds * 2.4)); // ~145 wpm spoken

    var locale = "en-US";
    if (!string.IsNullOrWhiteSpace(req.Voice))
    {
        var parts = req.Voice.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            locale = $"{parts[0]}-{parts[1]}";
    }
    var languageName = locale switch
    {
        "en-US" => "English (US)",
        "en-IN" => "English (India)",
        "hi-IN" => "Hindi (India)",
        "bn-IN" => "Bengali (India)",
        _ => locale
    };
    var scriptHint = locale switch
    {
        "hi-IN" => "Use Devanagari script.",
        "bn-IN" => "Use Bengali script.",
        _ => "Use the normal writing system for that language."
    };

    var beats = (req.Actions is { Length: > 0 })
        ? string.Join("\n", req.Actions.Select((a, i) => $"  {i + 1}. {a}"))
        : "(no beat list provided)";

    var steer = string.IsNullOrWhiteSpace(req.Prompt)
        ? "No extra direction was given — choose a fitting tone yourself (cinematic, natural, in keeping with the story)."
        : $"Narration direction from the user (follow it closely): {req.Prompt}";

    var sys = $@"You are a professional voiceover scriptwriter. Write the SPOKEN narration for a ~{totalSeconds}-second short film.

RULES:
- Output ONLY the words the narrator will speak. No stage directions, no scene labels, no quotation marks, no markdown.
- Aim for about {wordBudget} words so it fits naturally in {totalSeconds} seconds when read aloud at a calm pace.
- One flowing voiceover that matches the story's arc from start to finish. Do not enumerate clips or say 'clip one'.
- Write in {languageName} (locale {locale}). Do not switch to another language.
- {scriptHint}
- {steer}

Return ONE JSON object, no markdown: {{ ""script"": ""<the narration words>"" }}";

    var user = $@"Film idea: {req.Idea}
Title: {req.Title}
Logline: {req.Logline}
Beats (in order):
{beats}

Write the voiceover now. JSON only.";

    static string ExtractChatContent(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
                return (content.GetString() ?? "").Trim();

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(part.GetString());
                        continue;
                    }
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var txt)
                        && txt.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(txt.GetString());
                    }
                }
                return sb.ToString().Trim();
            }
        }
        catch { }
        return "";
    }

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(60);
    using var msg = new HttpRequestMessage(HttpMethod.Post,
        $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
    msg.Headers.Add("api-key", key);
    var payload = new
    {
        messages = new object[] {
            new { role = "system", content = sys },
            new { role = "user",   content = user }
        },
        max_completion_tokens = 1200,
        response_format = new { type = "json_object" }
    };
    msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    using var resp = await client.SendAsync(msg, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        return Results.Problem($"Narration script failed ({(int)resp.StatusCode}): {body}");

    var raw = ExtractChatContent(body);
    string script = "";
    if (!string.IsNullOrWhiteSpace(raw))
    {
        try
        {
            using var sd = JsonDocument.Parse(raw);
            if (sd.RootElement.TryGetProperty("script", out var sc)) script = sc.GetString() ?? "";
            else if (sd.RootElement.TryGetProperty("narration", out var nr)) script = nr.GetString() ?? "";
            else if (sd.RootElement.TryGetProperty("text", out var tx)) script = tx.GetString() ?? "";
        }
        catch { script = raw; }
    }

    // Some model responses return an empty/invalid JSON object despite 200.
    // Retry once without JSON mode and accept plain text if needed.
    if (string.IsNullOrWhiteSpace(script))
    {
        using var msg2 = new HttpRequestMessage(HttpMethod.Post,
            $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");
        msg2.Headers.Add("api-key", key);
        var payload2 = new
        {
            messages = new object[] {
                new { role = "system", content = sys + " Return only the narration words as plain text." },
                new { role = "user",   content = user }
            },
            max_completion_tokens = 1200
        };
        msg2.Content = new StringContent(JsonSerializer.Serialize(payload2), Encoding.UTF8, "application/json");
        using var resp2 = await client.SendAsync(msg2, ct);
        var body2 = await resp2.Content.ReadAsStringAsync(ct);
        if (resp2.IsSuccessStatusCode)
        {
            script = ExtractChatContent(body2);
        }
    }

    script = (script ?? "").Trim();

    // Hard fallback: never fail the endpoint just because the model returned
    // empty content. Build a usable script from storyboard metadata.
    if (string.IsNullOrWhiteSpace(script))
    {
        var seed = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Title)) seed.Add(req.Title!.Trim());
        if (!string.IsNullOrWhiteSpace(req.Logline)) seed.Add(req.Logline!.Trim());
        if (req.Actions is { Length: > 0 })
        {
            seed.Add(string.Join(" ", req.Actions
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Take(6)
                .Select(a => a!.Trim())));
        }
        var baseText = string.Join(" ", seed).Trim();
        if (string.IsNullOrWhiteSpace(baseText))
            baseText = string.IsNullOrWhiteSpace(req.Idea) ? "A short cinematic story unfolds from tension to resolution." : req.Idea!.Trim();

        script = $"{baseText} The story builds steadily, holds emotional focus, and closes with a clear final moment.";
    }

    // Ensure the script language follows the selected voice locale.
    var langPrefix = locale.Split('-')[0].ToLowerInvariant();
    if (langPrefix != "en")
    {
        try
        {
            var region = Cfg(cfg, "SPEECH_REGION");
            using var tmsg = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={Uri.EscapeDataString(langPrefix)}");
            tmsg.Headers.Add("Ocp-Apim-Subscription-Key", key);
            tmsg.Headers.Add("Ocp-Apim-Subscription-Region", region);
            tmsg.Content = new StringContent(
                JsonSerializer.Serialize(new[] { new { Text = script } }),
                Encoding.UTF8, "application/json");
            using var tresp = await client.SendAsync(tmsg, ct);
            var tbody = await tresp.Content.ReadAsStringAsync(ct);
            if (tresp.IsSuccessStatusCode)
            {
                using var tdoc = JsonDocument.Parse(tbody);
                var translated = tdoc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString();
                if (!string.IsNullOrWhiteSpace(translated)) script = translated!.Trim();
            }
        }
        catch
        {
            // If translation fails, keep the generated script so narration still works.
        }
    }

    script = (script ?? "").Trim();
    if (string.IsNullOrWhiteSpace(script))
        script = "The scene unfolds with quiet tension, rises in motion and emotion, and resolves with a final cinematic beat.";

    return Results.Ok(new { script, locale, language = languageName });
});

// ─────────────────────────────────────────────────────────────────────────────
// PromptCraft: multi-layer Sora-2 prompt engine.
//
// Pipeline (all calls go to REASONING_DEPLOYMENT, default gpt-5_4):
//   1. PLAN  — single deep-reasoning call that outputs intentContract +
//              identityBible + storyboard + framesManifest as one JSON.
//   2. COMPILE — turn the plan into per-segment Sora-2 prompts.
//   3. CRITIC×5 — score faithfulness/realism/Sora-compat/frame-coherence and
//              refine until all axes ≥ 9 or 5 iterations exhausted.
//
// Two HARD locks bake into every layer's system prompt:
//   • WORD LOCK     — every meaningful word in the user ask must be visually
//                     represented; deviations are flagged.
//   • REALISM LOCK  — only physically possible imagery; no fantasy creatures,
//                     no impossible physics, no surreal elements unless the
//                     user explicitly asked for them.
// Plus the existing FACE-RISK rules: occlusion device per shot, non-face
// identifiers, no close-ups unless the brief demands.
// ─────────────────────────────────────────────────────────────────────────────
app.MapPost("/api/promptcraft", (PromptCraftRequest req, IHttpClientFactory hf,
    IConfiguration cfg, PromptCraftJobStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.UserAsk))
        return Results.BadRequest(new { error = "userAsk is required" });

    var endpoint = Cfg(cfg, "AOAI_ENDPOINT").TrimEnd('/');
    var key = Cfg(cfg, "AOAI_KEY");
    var deployment = cfg["REASONING_DEPLOYMENT"] ?? "gpt-5-mini";
    var apiVersion = "2024-12-01-preview";
    var totalSeconds = Math.Clamp(req.TotalSeconds ?? 8, 4, 60);
    var size = req.Size ?? "1280x720";
    var chunkSeconds = Math.Min(totalSeconds, 12);
    var segmentCount = (int)Math.Ceiling(totalSeconds / (double)chunkSeconds);
    var maxIter = Math.Clamp(req.MaxIterations ?? 5, 1, 8);
    // Wan2.2-TI2V-5B has no audio output; drop dialogue/narration/voice at the
    // input boundary so PromptCraft's compiled prompts stay focused on visuals.
    // The old Sora-2 path used these to shape lip-synced narration; they're
    // no-ops now but the schema is preserved for frontend compatibility.
    var voice = "";
    var narration = "";

    var jobId = Guid.NewGuid().ToString("n");
    var job = store.Create(jobId);
    // Total phases for the progress bar: PLAN(1) + COMPILE(1) + maxIter critic + FINAL(1).
    // We weight roughly equally; UI just uses pct from server.
    int totalPhases = 2 + maxIter + 1;
    int phaseIdx = 0;
    void SetPhase(string label)
    {
        phaseIdx++;
        job.Phase = label;
        job.Pct = (int)Math.Min(99, Math.Round(100.0 * phaseIdx / totalPhases));
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    // Fire-and-forget background task. We DO NOT pass the request's CT — that
    // cancels the moment the response is sent. Use the store's CT instead so
    // callers could cancel via a future /cancel endpoint.
    _ = Task.Run(async () =>
    {
        try
        {
            var client = hf.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(4);

            async Task<JsonElement> CallReasoner(string sys, string user, int maxOut)
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post,
                    $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={apiVersion}");
                msg.Headers.Add("api-key", key);
                var payload = new Dictionary<string, object?>
                {
                    ["messages"] = new object[] {
                        new { role = "system", content = sys },
                        new { role = "user",   content = user }
                    },
                    ["max_completion_tokens"] = maxOut,
                    ["response_format"] = new { type = "json_object" }
                };
                msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await client.SendAsync(msg, job.Cts.Token);
                var raw = await resp.Content.ReadAsStringAsync(job.Cts.Token);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Reasoner call failed ({(int)resp.StatusCode}): {raw}");
                using var doc = JsonDocument.Parse(raw);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
                content = content.Trim();
                if (content.StartsWith("```"))
                {
                    var nl = content.IndexOf('\n');
                    if (nl > 0) content = content[(nl + 1)..];
                    if (content.EndsWith("```")) content = content[..^3];
                    content = content.Trim();
                }
                // Surface a useful error if the model's JSON got truncated by
                // the token budget — without this the parse error is opaque.
                try
                {
                    return JsonDocument.Parse(content).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    var finishReason = "unknown";
                    try { finishReason = doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString() ?? "unknown"; } catch { }
                    var len = content.Length;
                    var tail = content.Length > 200 ? content[^200..] : content;
                    throw new InvalidOperationException(
                        $"Reasoner JSON parse failed (finish_reason={finishReason}, content_len={len}, max_completion_tokens={maxOut}). " +
                        $"Likely truncated — increase maxOut. Tail: …{tail}", ex);
                }
            }

            const string LOCKS = @"
HARD LOCKS — violating any of these means the response is invalid:

1. WORD LOCK: Every meaningful noun, adjective, verb, number, colour, location,
   profession, prop, action, mood word, and named entity from the USER ASK
   must be visually represented in the final video. If a user word cannot
   be visualised (e.g. abstract concepts), embed it as ambient/symbolic
   imagery. Maintain a deviations list naming any user word you could not
   honour and explain why. Aim for zero deviations.

2. REALISM LOCK: The video must be photorealistic. No fantasy creatures,
   no magical effects, no impossible physics, no cartoon/anime/3D-render
   styles, no surreal compositions, no glowing auras, no floating objects,
   no anthropomorphic animals, no superpowers — UNLESS the user ask
   explicitly contains words like ""fantasy"", ""magic"", ""anime"",
   ""cartoon"", ""dream"", ""surreal"", ""sci-fi"". Default = documentary-grade
   real-world plausibility, on real film stock, with normal physics.

3. FACE-RISK MITIGATION (Sora-2 has identity drift + RAI face-block):
   • Default framing is medium or wider (waist-up or full-body), NEVER tight
     close-ups unless the user explicitly demanded one.
   • Pick ONE face-occlusion device that fits the brief and use it
     consistently: sunglasses, cap brim shadow, scarf, ¾ profile, back-of-
     head shot, helmet visor, surgical/dust mask, beard+hair coverage, or
     environmental occlusion (steam, rain, foreground branch). Never leave
     the face fully exposed and centred.
   • Identity is locked via 3-5 NON-FACE signatures: a saturated coloured
     garment, a unique prop carried, a visible scar/tattoo with location,
     hair ornament, distinctive footwear. These must be repeated verbatim
     in every segment.
   • No on-screen text, captions, logos, or watermarks ever (Sora can't
     render text reliably).
";

            // Layer 1: PLAN
            SetPhase("Planning intent + identity + storyboard");
            var planSys = $@"You are the chief planner for a Sora-2 video shoot. Think step by step about the user ask, then output a SINGLE JSON object — no markdown, no prose outside JSON.

{LOCKS}

OUTPUT SCHEMA (must match exactly):
{{
  ""intentContract"": {{
    ""userAskVerbatim"": ""<echo the user ask exactly as given>"",
    ""extractedWords"": [
      {{ ""word"": ""<one meaningful word/phrase from the ask>"", ""category"": ""subject|action|setting|mood|colour|prop|number|adjective|other"", ""visualisation"": ""<exactly how this will appear on screen>"" }}
    ],
    ""nonNegotiables"": [""<list every constraint the user implied or stated>""],
    ""realismMode"": ""photoreal|user-permitted-stylised"",
    ""realismJustification"": ""<one sentence: why this mode given the ask>""
  }},
  ""identityBible"": {{
    ""characterAnchor"": ""<3-5 distinctive repeatable details: gender/age/hair/wardrobe/build, one sentence, pasted verbatim per segment>"",
    ""distinctiveTraits"": ""<3-5 NON-FACE signatures, comma-separated, includes at least ONE high-contrast saturated colour element>"",
    ""faceOcclusionDevice"": ""<the single occlusion strategy chosen, e.g. 'aviator sunglasses + cap brim shadow throughout'>"",
    ""defaultFraming"": ""<medium-wide|medium|waist-up|full-body|over-shoulder>""
  }},
  ""sceneAnchor"": ""<2-3 sentences: environment, time of day, weather, palette of 3-5 anchor colours, ambient details>"",
  ""cinematography"": ""<multi-line: Camera shot / Camera motion / Lens / Lighting / Mood — kept identical across segments>"",
  ""voiceDescription"": ""<one sentence describing speaker timbre, age range, accent, pace>"",
  ""backgroundSound"": ""<2-4 concrete diegetic sounds, comma-separated>"",
  ""storyboard"": {{
    ""totalSeconds"": {totalSeconds},
    ""segmentCount"": {segmentCount},
    ""chunkSeconds"": {chunkSeconds},
    ""segments"": [
      {{
        ""index"": 1,
        ""startSec"": 0,
        ""endSec"": {chunkSeconds},
        ""summary"": ""<one sentence>"",
        ""beats"": [""Beat 1 (0-Xs): <ONE concrete physical action with face-occlusion preserved>"", ""Beat 2 ..."", ""... 4-6 beats""],
        ""dialogue"": ""<verbatim narration line if spoken in this segment, else null>""
      }}
    ]
  }},
  ""framesManifest"": [
    {{ ""t"": 0, ""segment"": 1, ""description"": ""<one line: framing, subject pose, occlusion device visible, key prop, lighting, ambient — what is on screen at this exact second>"" }}
  ]
}}

framesManifest MUST contain one entry per second of total video (so {totalSeconds} entries total: t=0..{totalSeconds - 1}).
storyboard.segments MUST contain exactly {segmentCount} entries.
extractedWords MUST cover every meaningful token in the user ask — aim for thoroughness, list 10-30 words.";

            var planUser = $@"USER ASK (verbatim, treat every word as a constraint):
{req.UserAsk}

Total duration: {totalSeconds}s rendered as {segmentCount} segment(s) of up to {chunkSeconds}s.
Aspect: {size}
Voice: {voice}
Narration to embed verbatim (distribute across segments naturally if non-empty): {narration}

Plan now. Output JSON only.";

            var planJson = await CallReasoner(planSys, planUser, 4000);

            // Layer 2: COMPILE
            SetPhase("Compiling Sora-2 segment prompts");
            var compileSys = $@"You are a Sora-2 prompt compiler. Given a structured plan, produce per-segment prompts ready to send to Sora-2.

{LOCKS}

CHARACTER LOCK PROTOCOL — strict, mandatory:
  • Take the plan's identityBible.characterAnchor and identityBible.distinctiveTraits as a single canonical block.
  • Every single segment prompt MUST begin with that block reproduced VERBATIM, word-for-word, in the same order, with the same adjectives, colours, props, tattoos, hairstyle, age, build, accessories. Do NOT paraphrase, abbreviate, reorder, translate, or substitute synonyms in the lock block.
  • After the lock block, append a single sentence: ""Reproduce these identity anchors identically in every frame; do not introduce new wardrobe, props, hair, or facial features.""
  • Then continue with scene/cinematography/beats/dialogue.
  • The lock block MUST be IDENTICAL string content across all {segmentCount} segments. A reader diffing segment 1 prompt vs segment {segmentCount} prompt should see the first paragraph match exactly.

Each segment prompt MUST follow this template internally (but write it as flowing paragraphs, no labels):
  [CHARACTER LOCK BLOCK — verbatim from identityBible, identical across all segments] → [STYLE line] → [scene anchor sentences] → [cinematography block] → [face-occlusion device statement] → [beats as numbered actions with timestamps] → [dialogue if present] → [voice description] → [background sound] → [negative prompt: 'no on-screen text, no captions, no logos, no watermarks, no extra speakers, no duplicated limbs, no fantasy elements (unless ask permits), no impossible physics']

OUTPUT SCHEMA:
{{
  ""segments"": [
    {{
      ""index"": 1,
      ""prompt"": ""<the full Sora-2 prompt as a single string, 200-500 words, flowing paragraphs not bullet points, MUST start with the verbatim CHARACTER LOCK block>"",
      ""dialogue"": ""<verbatim line or null>"",
      ""seconds"": {chunkSeconds}
    }}
  ]
}}

Output JSON only.";

            var compileUser = $@"PLAN:
{planJson.GetRawText()}

Compile the {segmentCount} segment prompts now. Output JSON only.";

            var compiled = await CallReasoner(compileSys, compileUser, 6000);

            // Layer 3: CRITIC LOOP
            var iterations = new List<object>();
            var currentSegments = compiled.GetProperty("segments");

            var criticSys = $@"You are a brutally honest video-prompt critic. Given the user ask, the plan, and current per-segment Sora-2 prompts, score on FOUR axes (1-10) and emit refined prompts.

{LOCKS}

Scoring axes:
  • faithfulnessScore: every meaningful user word represented? Lower = more deviations.
  • realismScore: zero unreal/fantasy/impossible elements (unless ask permits)? Lower = more violations.
  • soraCompatScore: face-occlusion device present every segment? non-face anchors locked? no on-screen text? short dialogue? Lower = more risks.
  • frameCoherenceScore: does segment N's last beat flow into segment N+1's first beat? identity & wardrobe identical? Lower = more breaks.

OUTPUT SCHEMA:
{{
  ""scores"": {{ ""faithfulness"": <int>, ""realism"": <int>, ""soraCompat"": <int>, ""frameCoherence"": <int> }},
  ""deviations"": {{
    ""faithfulness"": [""<user word/concept missing or misrepresented>""],
    ""realism"": [""<unreal element to remove>""],
    ""soraCompat"": [""<face-risk or sora issue>""],
    ""frameCoherence"": [""<continuity break>""]
  }},
  ""refinedSegments"": [
    {{ ""index"": 1, ""prompt"": ""<refined prompt — surgically improved, NOT a full rewrite if axis already ≥ 9>"", ""dialogue"": ""<verbatim or null>"", ""seconds"": <int>, ""changeNote"": ""<one line: what you changed and why>"" }}
  ],
  ""verdict"": ""ship|refine_again""
}}

Be ruthless on faithfulness — if the user said 'red bicycle in the rain' and the prompt has a 'crimson cycle in mist', that is a deviation. Use the exact user words.
Output JSON only.";

            JsonElement finalScores = default;
            string finalVerdict = "refine_again";
            int iterDone = 0;

            for (int i = 1; i <= maxIter; i++)
            {
                SetPhase($"Critic iteration {i}/{maxIter}");
                var criticUser = $@"USER ASK (verbatim):
{req.UserAsk}

PLAN:
{planJson.GetRawText()}

CURRENT SEGMENTS:
{currentSegments.GetRawText()}

Score, list deviations, and emit refinedSegments now. Output JSON only.";

                var critique = await CallReasoner(criticSys, criticUser, 6000);
                var scores = critique.GetProperty("scores");
                int faith = scores.GetProperty("faithfulness").GetInt32();
                int real = scores.GetProperty("realism").GetInt32();
                int soraS = scores.GetProperty("soraCompat").GetInt32();
                int frame = scores.GetProperty("frameCoherence").GetInt32();

                var iterRecord = new
                {
                    n = i,
                    scores = new { faithfulness = faith, realism = real, soraCompat = soraS, frameCoherence = frame },
                    deviations = critique.TryGetProperty("deviations", out var dev) ? dev.Clone() : default,
                    verdict = critique.TryGetProperty("verdict", out var v) ? v.GetString() : "refine_again"
                };
                iterations.Add(iterRecord);
                job.LiveIterations = iterations.ToArray(); // visible to status poller

                if (critique.TryGetProperty("refinedSegments", out var refined) && refined.GetArrayLength() > 0)
                    currentSegments = refined.Clone();

                finalScores = scores.Clone();
                finalVerdict = critique.TryGetProperty("verdict", out var vv) ? (vv.GetString() ?? "refine_again") : "refine_again";
                iterDone = i;

                if (faith >= 9 && real >= 9 && soraS >= 9 && frame >= 9 && finalVerdict == "ship") break;
            }

            // Layer 4: FINAL ASSEMBLY
            SetPhase("Assembling final prompt");

            // SAFETY NET: enforce the character-lock invariant by prepending the
            // canonical block to every segment prompt that doesn't already start
            // with it. Even if the LLM drifted on iteration 5/5, the rendered
            // prompts will be byte-identical in their identity preface, which is
            // the single strongest signal against character drift across clips.
            string characterLockBlock = "";
            try
            {
                var ib = planJson.GetProperty("identityBible");
                var ca = ib.TryGetProperty("characterAnchor", out var caEl) ? caEl.GetString() ?? "" : "";
                var dt = ib.TryGetProperty("distinctiveTraits", out var dtEl) ? dtEl.GetString() ?? "" : "";
                var fo = ib.TryGetProperty("faceOcclusionDevice", out var foEl) ? foEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(ca))
                {
                    characterLockBlock =
                        "[CHARACTER LOCK — reproduce identically in every frame; do not paraphrase, do not introduce new wardrobe, props, hair, or facial features.] "
                        + ca
                        + (string.IsNullOrWhiteSpace(dt) ? "" : " Distinctive identity markers that must remain visible and consistent: " + dt + ".")
                        + (string.IsNullOrWhiteSpace(fo) ? "" : " Face-occlusion device, kept consistent throughout: " + fo + ".");
                }
            }
            catch { /* plan didn't include identityBible — leave block empty */ }

            if (!string.IsNullOrWhiteSpace(characterLockBlock))
            {
                var segArr = currentSegments.EnumerateArray().ToArray();
                var rebuilt = new List<object>();
                foreach (var seg in segArr)
                {
                    var promptText = seg.GetProperty("prompt").GetString() ?? "";
                    // Idempotent: only prepend if the prompt doesn't already lead
                    // with the canonical block (within a small tolerance window).
                    var trimmed = promptText.TrimStart();
                    if (!trimmed.StartsWith("[CHARACTER LOCK", StringComparison.OrdinalIgnoreCase))
                        promptText = characterLockBlock + "\n\n" + promptText;
                    rebuilt.Add(new
                    {
                        index = seg.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0,
                        prompt = promptText,
                        dialogue = seg.TryGetProperty("dialogue", out var dl) && dl.ValueKind != JsonValueKind.Null ? dl.GetString() : null,
                        seconds = seg.TryGetProperty("seconds", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : chunkSeconds,
                        changeNote = seg.TryGetProperty("changeNote", out var cn) ? cn.GetString() : null
                    });
                }
                // Replace currentSegments with the locked version for downstream use.
                currentSegments = JsonSerializer.SerializeToElement(rebuilt);
            }

            var combinedPrompt = new StringBuilder();
            foreach (var seg in currentSegments.EnumerateArray())
            {
                if (combinedPrompt.Length > 0) combinedPrompt.AppendLine().AppendLine("---").AppendLine();
                combinedPrompt.Append(seg.GetProperty("prompt").GetString());
            }

            job.Result = new
            {
                userAsk = req.UserAsk,
                totalSeconds,
                segmentCount,
                chunkSeconds,
                plan = planJson,
                segments = currentSegments,
                iterations,
                iterationsRun = iterDone,
                finalScores,
                verdict = finalVerdict,
                combinedPrompt = combinedPrompt.ToString(),
                reasoningModel = deployment
            };
            job.Phase = "done";
            job.Pct = 100;
            job.Done = true;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            job.Error = "cancelled";
            job.Done = true;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Done = true;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
    });

    return Results.Json(new { jobId, totalPhases });
});

// Status poller: returns current phase/pct, plus the result once done.
app.MapGet("/api/promptcraft/{jobId}", (string jobId, PromptCraftJobStore store) =>
{
    var job = store.Get(jobId);
    if (job == null) return Results.NotFound(new { error = "job not found or expired" });
    return Results.Json(new
    {
        jobId,
        phase = job.Phase,
        pct = job.Pct,
        done = job.Done,
        error = job.Error,
        liveIterations = job.LiveIterations,
        result = job.Done && job.Error == null ? job.Result : null
    });
});

// Cancel an in-flight PromptCraft job. Cooperative — only stops at the next reasoner call.
app.MapPost("/api/promptcraft/{jobId}/cancel", (string jobId, PromptCraftJobStore store) =>
{
    var job = store.Get(jobId);
    if (job == null) return Results.NotFound(new { error = "job not found" });
    job.Cts.Cancel();
    return Results.Ok(new { cancelled = true });
});

// ----- Web Push: VAPID key, subscriptions, per-Sora-job notifier -----------
//
// Goal: when the user's phone is locked / Chrome is backgrounded, the JS poll
// loop is suspended by the browser, so the user never knows when each clip has
// finished. To bridge that gap, the server polls Sora itself and sends a Web
// Push notification on completion, which appears on the lock screen / status
// bar. Tapping the notification refocuses the tab; the page's interruptible
// sleep + Wake Lock then resume the JS render loop for the next chunk.

app.MapGet("/api/push/vapid-public-key", (VapidConfig vapid) =>
    Results.Ok(new { publicKey = vapid.PublicKey }));

// Subscribe a browser to receive push notifications for a specific Sora job.
// Body: { jobId, endpoint, keys: { p256dh, auth }, label? }.
// Side effect: registers a server-side watcher Task that polls Sora until the
// job reaches a terminal state, then fires one notification to all subs.
app.MapPost("/api/push/subscribe", async (HttpRequest http, PushSubscriptionStore subs,
    SoraJobWatcherRegistry registry, VapidConfig vapid, IHttpClientFactory hf,
    IConfiguration cfg, ILogger<PushSubscriptionStore> log, CancellationToken ct) =>
{
    PushSubscribeBody? body;
    try { body = await JsonSerializer.DeserializeAsync<PushSubscribeBody>(http.Body, cancellationToken: ct); }
    catch { return Results.BadRequest(new { error = "invalid json" }); }
    if (body is null || string.IsNullOrWhiteSpace(body.JobId) || string.IsNullOrWhiteSpace(body.Endpoint)
        || body.Keys is null || string.IsNullOrWhiteSpace(body.Keys.P256dh) || string.IsNullOrWhiteSpace(body.Keys.Auth))
        return Results.BadRequest(new { error = "jobId, endpoint, keys.p256dh, keys.auth required" });

    subs.Add(body.JobId, new StoredPushSub(body.Endpoint, body.Keys.P256dh, body.Keys.Auth, body.Label ?? body.JobId));

    // Start a one-shot watcher (idempotent: registry de-dupes by jobId).
    registry.StartWatcher(body.JobId, async stoppingToken =>
    {
        var wan = app.Services.GetRequiredService<WanClient>();
        var elapsed = 0;
        var consecutiveFails = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Match the client's polling cadence: less aggressive early, faster late.
            var wait = elapsed < 60_000 ? 6000 : elapsed < 120_000 ? 4000 : 3000;
            try { await Task.Delay(wait, stoppingToken); } catch { break; }
            elapsed += wait;
            string? status = null;
            try
            {
                var json = await wan.GetStatusJsonAsync(body.JobId, stoppingToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("status", out var s)) status = s.GetString();
                consecutiveFails = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "watcher {Id}: poll error", body.JobId);
                consecutiveFails++;
                if (consecutiveFails >= 12) break;
                continue;
            }

            if (status == "succeeded" || status == "completed")
            {
                await SendPushAsync(subs, vapid, body.JobId, $"Clip ready: {body.Label ?? body.JobId}",
                    "Tap to return and continue rendering.", "videotool-clip", log);
                break;
            }
            if (status == "failed" || status == "cancelled")
            {
                await SendPushAsync(subs, vapid, body.JobId, $"Clip {status}: {body.Label ?? body.JobId}",
                    $"Wan2.2 job {status}. Open videotool to retry.", "videotool-clip", log);
                break;
            }
        }
    });

    return Results.Ok(new { ok = true });
});

// Local helper used above.
static async Task SendPushAsync(PushSubscriptionStore subs, VapidConfig vapid, string jobId,
    string title, string body, string tag, ILogger log)
{
    var details = new VapidDetails("mailto:noreply@videotool.local", vapid.PublicKey, vapid.PrivateKey);
    var pushClient = new WebPushClient();
    var payload = JsonSerializer.Serialize(new { title, body, tag, jobId, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    var dead = new List<StoredPushSub>();
    foreach (var s in subs.Get(jobId))
    {
        try
        {
            var sub = new PushSubscription(s.Endpoint, s.P256dh, s.Auth);
            await pushClient.SendNotificationAsync(sub, payload, details);
        }
        catch (WebPushException wpe) when ((int)wpe.StatusCode == 404 || (int)wpe.StatusCode == 410)
        {
            // Subscription is gone; clean up.
            dead.Add(s);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "push send failed for {Endpoint}", s.Endpoint);
        }
    }
    foreach (var d in dead) subs.Remove(jobId, d);
}

app.Run();

public record NarrateRequest(string Text, string? Voice);
public record TranslateRequest(string Text, string? To);
public record EnhanceRequest(string Prompt, string? Narration, string? Voice, int? Seconds, string? Size);
public record PromptCraftRequest(string UserAsk, int? TotalSeconds, string? Size, string? Narration, string? Voice, int? MaxIterations);
public record StitchRequest(string[] Urls, double? Crossfade);
public record IdentityRequest(string VideoUrl, string? OriginalAnchor);
public record TailRequest(string VideoUrl, double? Seconds, string? Size);
public record SliceRequest(string VideoUrl, double? StartSec, double? DurationSec, string? Size, bool? FromEnd);
public record RemixRequest(string VideoId, string Prompt, int? Seconds, string? Size);
public record ExtractFrameRequest(string VideoUrl, string? Size);
public record StoryboardRequest(string Prompt, int? TotalSeconds, int? ClipSeconds, string? Size, string? Language, bool? Narrate, string? DialogDirection);
public record NarrationScriptRequest(string? Idea, string? Title, string? Logline, string[]? Actions, int? TotalSeconds, string? Prompt, string? Voice);
public record MuxAudioRequest(string VideoUrl, string AudioUrl, string? Mode);
public record ConcatAudioRequest(string[]? AudioUrls, int? GapMs);
public record DialogueClip(int Index, string? Title, string? Action, string? Narration);
public record DialogueRequest(string? Direction, string? Language, DialogueClip[]? Clips);

// In-memory store for PromptCraft jobs. Single-instance F1 deployment, so a
// process-local concurrent dictionary is the simplest viable storage. Jobs
// older than 10 minutes are evicted on each Create() call so the dict never
// grows unbounded.
public class PromptCraftJob
{
    public string Phase { get; set; } = "queued";
    public int Pct { get; set; } = 0;
    public bool Done { get; set; } = false;
    public string? Error { get; set; }
    public object? Result { get; set; }
    public object[]? LiveIterations { get; set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();
}

public class PromptCraftJobStore
{
    private readonly ConcurrentDictionary<string, PromptCraftJob> _jobs = new();

    public PromptCraftJob Create(string id)
    {
        // Evict anything older than 10 minutes before adding the new one.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var kv in _jobs)
        {
            if (kv.Value.UpdatedAt < cutoff)
                _jobs.TryRemove(kv.Key, out _);
        }
        var job = new PromptCraftJob();
        _jobs[id] = job;
        return job;
    }

    public PromptCraftJob? Get(string id) =>
        _jobs.TryGetValue(id, out var j) ? j : null;
}

// ----- Web Push support types --------------------------------------------

public record PushSubscribeBody(string JobId, string Endpoint, PushKeys Keys, string? Label);
public record PushKeys(string P256dh, string Auth);
public record StoredPushSub(string Endpoint, string P256dh, string Auth, string Label);

// VAPID keys are required by the Web Push protocol to identify the application
// server. We auto-generate on first run and persist to disk so subscriptions
// survive restarts. On Azure App Service Linux, /home/data is a writable
// persistent path; locally we fall back to ./vapid.json.
public class VapidConfig
{
    public string PublicKey { get; init; } = "";
    public string PrivateKey { get; init; } = "";

    public static VapidConfig LoadOrCreate(ILogger<VapidConfig> log)
    {
        // Allow override via env vars / config (e.g. App Settings).
        var envPub = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        var envPriv = Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(envPub) && !string.IsNullOrWhiteSpace(envPriv))
        {
            log.LogInformation("VAPID keys loaded from environment");
            return new VapidConfig { PublicKey = envPub!, PrivateKey = envPriv! };
        }

        var dataDir = Directory.Exists("/home/data") ? "/home/data" : AppContext.BaseDirectory;
        var path = Path.Combine(dataDir, "vapid.json");
        if (File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize<VapidConfig>(fs);
                if (loaded != null && !string.IsNullOrWhiteSpace(loaded.PublicKey) && !string.IsNullOrWhiteSpace(loaded.PrivateKey))
                {
                    log.LogInformation("VAPID keys loaded from {Path}", path);
                    return loaded;
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "failed to load VAPID keys from {Path}; regenerating", path); }
        }

        var keys = VapidHelper.GenerateVapidKeys();
        var cfg = new VapidConfig { PublicKey = keys.PublicKey, PrivateKey = keys.PrivateKey };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(cfg));
            log.LogInformation("VAPID keys generated and persisted to {Path}", path);
        }
        catch (Exception ex) { log.LogWarning(ex, "VAPID keys generated but could not persist to {Path} (in-memory only)", path); }
        return cfg;
    }
}

// In-memory store mapping Sora jobId → list of subscriptions interested in that
// job. Subscriptions are added per render submission and removed when they
// become invalid (404/410 from push gateway).
public class PushSubscriptionStore
{
    private readonly ConcurrentDictionary<string, List<StoredPushSub>> _subs = new();

    public void Add(string jobId, StoredPushSub sub)
    {
        var list = _subs.GetOrAdd(jobId, _ => new List<StoredPushSub>());
        lock (list)
        {
            // De-dupe by endpoint so a re-subscribe doesn't fan out.
            list.RemoveAll(s => s.Endpoint == sub.Endpoint);
            list.Add(sub);
        }
    }

    public IReadOnlyList<StoredPushSub> Get(string jobId)
    {
        if (!_subs.TryGetValue(jobId, out var list)) return Array.Empty<StoredPushSub>();
        lock (list) return list.ToArray();
    }

    public void Remove(string jobId, StoredPushSub sub)
    {
        if (!_subs.TryGetValue(jobId, out var list)) return;
        lock (list) list.RemoveAll(s => s.Endpoint == sub.Endpoint);
    }
}

// Tracks active per-Sora-job watcher Tasks. Idempotent registration: calling
// StartWatcher twice for the same jobId only spins one polling loop.
public class SoraJobWatcherRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public void StartWatcher(string jobId, Func<CancellationToken, Task> body)
    {
        // Drop completed watchers from earlier jobs.
        foreach (var kv in _active.ToArray())
            if (kv.Value.IsCancellationRequested) _active.TryRemove(kv.Key, out _);

        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(jobId, cts)) { cts.Dispose(); return; } // already watching
        cts.CancelAfter(TimeSpan.FromMinutes(20)); // hard ceiling so a stuck Sora poll never leaks

        _ = Task.Run(async () =>
        {
            try { await body(cts.Token); }
            catch { /* swallow; watcher errors must not crash the host */ }
            finally
            {
                if (_active.TryRemove(jobId, out var owned)) owned.Dispose();
            }
        });
    }
}
