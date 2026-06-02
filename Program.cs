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

    var seconds = req.Seconds ?? 8;
    var size = req.Size ?? "1280x720";

    var sys = @"You are a senior cinematographer who writes prompts for the Sora-2 text-to-video model.
Rewrite the user's idea into ONE single English paragraph (max 120 words) that Sora-2 will turn into a great video.
Rules:
- Be visually concrete: subject, action, setting, time of day, lighting, lens / camera move, mood.
- Use ""photorealistic"", ""shallow depth of field"", ""35mm film look"" or ""anime"" style cues only when they fit the user's intent.
- If a narration script is provided, the visible character must clearly be speaking it (open mouth, lip-sync) and the prompt must include a Voiceover instruction with the EXACT translated line in the target language, the speaker's gender, and the language name.
- Never invent extra dialogue. Never add background music unless asked. Mention real ambient sound that fits.
- Output ONLY the final prompt. No preamble, no markdown, no quotes around it.";

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

    var userMsg = $@"User idea: {req.Prompt}
Target duration: {seconds} seconds
Aspect: {size}
Narration language: {langName}
Speaker gender: {gender}
Voice persona: {voiceShort}
Narration line to embed verbatim (already in {langName}): {translatedNarration}

Compose the final Sora-2 prompt now.";

    var client = hf.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(60);
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
        max_tokens = 600
    };
    chatMsg.Content = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(payload),
        Encoding.UTF8, "application/json");
    using var chatResp = await client.SendAsync(chatMsg, ct);
    var chatBody = await chatResp.Content.ReadAsStringAsync(ct);
    if (!chatResp.IsSuccessStatusCode)
        return Results.Problem($"Enhance failed ({(int)chatResp.StatusCode}): {chatBody}");

    using var chatDoc = System.Text.Json.JsonDocument.Parse(chatBody);
    var enhanced = chatDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? req.Prompt;
    return Results.Ok(new { enhanced, translatedNarration, lang = langPrefix, langName });
});

app.Run();

public record NarrateRequest(string Text, string? Voice);
public record TranslateRequest(string Text, string? To);
public record EnhanceRequest(string Prompt, string? Narration, string? Voice, int? Seconds, string? Size);
