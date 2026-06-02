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
        var url = await UploadAndSign(container, name, mp4, "video/mp4", svc, account, ct);
        return Results.Ok(new { url, blob = name, clips = req.Urls.Length, crossfade, bytes = mp4.Length });
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
    var url = await UploadAndSign(container, name, mp3, "audio/mpeg", svc, account, ct);
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
- The five anchors (style/characterAnchor/sceneAnchor/cinematography/voiceDescription/backgroundSound) MUST stay constant across segments — they describe the unchanging world. The segments array carries the changing action.
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
        temperature = 0.7,
        max_tokens = 2400,
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

app.Run();

public record NarrateRequest(string Text, string? Voice);
public record TranslateRequest(string Text, string? To);
public record EnhanceRequest(string Prompt, string? Narration, string? Voice, int? Seconds, string? Size);
public record StitchRequest(string[] Urls, double? Crossfade);
