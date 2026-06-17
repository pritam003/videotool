using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VideoTool;

/// <summary>
/// Thin client for the ComfyUI HTTP API on the Wan2.2 container app.
/// Translates between ComfyUI's prompt/history surface and the
/// Sora-shaped status object the rest of the app already understands.
/// Workflow JSON is loaded from <c>workflows/wan22-t2v.json</c> at content root
/// so it can be edited without recompiling.
/// </summary>
public sealed class WanClient
{
    private readonly IHttpClientFactory _hf;
    private readonly string _baseUrl;
    private readonly string _animateUrl;  // Can be different from _baseUrl if using separate container
    private readonly string? _authKey;
    private readonly Lazy<string> _workflowJson;
    private readonly Lazy<string> _i2vWorkflowJson;
    private readonly Lazy<string> _fluxWorkflowJson;
    private readonly Lazy<string> _foleyWorkflowJson;
    private readonly Lazy<string> _aceWorkflowJson;
    private readonly Lazy<string> _animateWorkflowJson;
    private readonly ILogger<WanClient> _log;

    public WanClient(IHttpClientFactory hf, IConfiguration cfg, IHostEnvironment env, ILogger<WanClient> log)
    {
        _hf = hf;
        _log = log;
        _baseUrl = (cfg["WAN_BASE_URL"]
            ?? throw new InvalidOperationException("WAN_BASE_URL is not set")).TrimEnd('/');
        // Optional separate endpoint for Wan-Animate (can route to dedicated container)
        _animateUrl = (cfg["WAN_ANIMATE_BASE_URL"] ?? _baseUrl).TrimEnd('/');
        _authKey = cfg["WAN_AUTH_KEY"]; // optional shared secret, sent as X-Wan-Auth
        var workflowPath = cfg["WAN_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "wan22-t2v.json");
        _workflowJson = new Lazy<string>(() => File.ReadAllText(workflowPath));
        // Image-to-video workflow (Wan2.2 I2V-A14B + WanImageToVideo). Used for the
        // story-chain continuation feature where each clip is conditioned on the
        // previous clip's last frame (or the user's character image for clip 1).
        var i2vWorkflowPath = cfg["WAN_I2V_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "wan22-i2v.json");
        _i2vWorkflowJson = new Lazy<string>(() => File.ReadAllText(i2vWorkflowPath));
        // FLUX.1 Schnell text-to-image workflow (ComfyUI core CheckpointLoaderSimple).
        // Runs on the same A100 as Wan2.2 for fast, high-quality character portraits.
        var fluxWorkflowPath = cfg["WAN_FLUX_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "flux-schnell.json");
        _fluxWorkflowJson = new Lazy<string>(() => File.ReadAllText(fluxWorkflowPath));
        // HunyuanVideo-Foley workflow (neural video->audio SFX/ambience). Only loaded when the
        // gated /api/foley endpoint is hit (WAN_AUDIO_ENABLED + the audio-enabled GPU image with
        // the ComfyUI-HunyuanVideoFoley custom node + weights on the share). The file is a
        // starter graph — validate node ids against the installed node version at activation.
        var foleyWorkflowPath = cfg["WAN_FOLEY_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "hunyuan-foley.json");
        _foleyWorkflowJson = new Lazy<string>(() => File.ReadAllText(foleyWorkflowPath));
        // ACE-Step text-to-music workflow (ComfyUI native ACE-Step nodes). Used by the gated
        // /api/generate-score endpoint to compose an LLM-directed instrumental score for a film.
        // Needs ace_step_v1_3.5b.safetensors staged to the checkpoints share + a recent ComfyUI.
        var aceWorkflowPath = cfg["WAN_ACE_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "ace-step.json");
        _aceWorkflowJson = new Lazy<string>(() => File.ReadAllText(aceWorkflowPath));
        // Wan2.2-Animate workflow (character image + driving video -> animated video). Only loaded when
        // the gated /api/animate-submit endpoint is hit (WAN_ANIMATE_ENABLED + the Animate weights on the
        // share + a ComfyUI with the WanAnimateToVideo node + VideoHelperSuite). Starter graph — validate
        // node inputs against the installed node at activation.
        var animateWorkflowPath = cfg["WAN_ANIMATE_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "wan-animate.json");
        _animateWorkflowJson = new Lazy<string>(() => File.ReadAllText(animateWorkflowPath));
    }

    /// <summary>
    /// Submit a Wan2.2 text-to-video job. Returns the ComfyUI prompt_id (a UUID)
    /// which we surface as the "id" in the Sora-shaped response so the rest of
    /// the app keeps working unchanged.
    /// </summary>
    public async Task<string> SubmitAsync(string prompt, int seconds, int width, int height, CancellationToken ct)
    {
        var (frames, w, h, seed) = NormalizeDims(seconds, width, height);
        var workflow = _workflowJson.Value
            .Replace("__PROMPT__", JsonSerializer.Serialize(WithRealismDirectives(prompt)))
            .Replace("__WIDTH__", w.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__HEIGHT__", h.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__FRAMES__", frames.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SEED__", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await PostGraphAsync(workflow, ct);
    }

    /// <summary>
    /// Submit a Wan2.2 image-to-video job conditioned on <paramref name="imageName"/>
    /// (a filename already present in ComfyUI's input directory — upload it first via
    /// <see cref="UploadImageAsync"/>). This is the engine behind the story-chain
    /// continuation: clip 1 is conditioned on the user's character image, and every
    /// subsequent clip is conditioned on the previous clip's extracted last frame so
    /// the character, wardrobe and scene carry forward frame-to-frame.
    /// </summary>
    public async Task<string> SubmitI2VAsync(string prompt, string imageName, int seconds, int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageName))
            throw new ArgumentException("imageName is required for I2V", nameof(imageName));
        var (frames, w, h, seed) = NormalizeDims(seconds, width, height);
        var workflow = _i2vWorkflowJson.Value
            .Replace("__PROMPT__", JsonSerializer.Serialize(WithRealismDirectives(prompt)))
            .Replace("__IMAGE__", JsonSerializer.Serialize(imageName))
            .Replace("__WIDTH__", w.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__HEIGHT__", h.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__FRAMES__", frames.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SEED__", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await PostGraphAsync(workflow, ct);
    }

    /// <summary>
    /// Submit a Wan2.2-Animate job: drive a character <paramref name="image"/> with a sample/driving
    /// <paramref name="video"/> so the character replicates its motion and expression. Uploads both into
    /// ComfyUI's input dir, substitutes the animate workflow, and returns the prompt_id — poll it with
    /// <see cref="GetStatusJsonAsync"/> and download the produced MP4 with <see cref="DownloadAsync"/>.
    /// Requires the Animate weights on the share + a ComfyUI with the WanAnimateToVideo node.
    /// </summary>
    public async Task<string> SubmitAnimateAsync(
        Stream image, string imageFilename, Stream video, string videoFilename,
        string prompt, int seconds, int width, int height, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Upload to animate container (might be different from _baseUrl)
        var imageName = await UploadImageAsync(image, imageFilename, ct, _animateUrl);
        var videoName = await UploadVideoAsync(video, videoFilename, ct, _animateUrl);
        // Wan-Animate output length follows the driving video, up to ~1 minute (NOT the 5s clip cap).
        // 16 fps, frame count 4n+1 (temporal compression factor 4); spatial dims multiples of 16.
        var secClamped = Math.Clamp(seconds, 1, 60);
        var frames = secClamped * 16 + 1;
        var w = Math.Max(256, (width / 16) * 16);
        var h = Math.Max(256, (height / 16) * 16);
        var seed = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
        var workflow = _animateWorkflowJson.Value
            .Replace("__IMAGE__", JsonSerializer.Serialize(imageName))
            .Replace("__VIDEO__", JsonSerializer.Serialize(videoName))
            .Replace("__PROMPT__", JsonSerializer.Serialize(WithRealismDirectives(prompt)))
            .Replace("__WIDTH__", w.ToString(inv))
            .Replace("__HEIGHT__", h.ToString(inv))
            .Replace("__FRAMES__", frames.ToString(inv))
            .Replace("__SEED__", seed.ToString(inv));
        return await PostGraphAsync(workflow, ct, _animateUrl);
    }

    /// <summary>
    /// Generate a still image with FLUX.1 Schnell (text-to-image) on the same A100 that
    /// runs Wan2.2. Submits the FLUX workflow graph, polls /history until the SaveImage
    /// node emits an output, and returns the (filename, subfolder) of the produced PNG
    /// (download it with <see cref="DownloadAsync"/>). FLUX Schnell is a 4-step model so a
    /// portrait renders in a few seconds once the checkpoint is resident in VRAM.
    /// </summary>
    public async Task<(string filename, string? subfolder)> GenerateImageAsync(
        string prompt, int width, int height, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // FLUX likes dims that are multiples of 16; clamp to a sane portrait/landscape range.
        var w = Math.Clamp((width / 16) * 16, 256, 1536);
        var h = Math.Clamp((height / 16) * 16, 256, 1536);
        var seed = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
        var workflow = _fluxWorkflowJson.Value
            .Replace("__PROMPT__", JsonSerializer.Serialize(prompt ?? string.Empty))
            .Replace("__WIDTH__", w.ToString(inv))
            .Replace("__HEIGHT__", h.ToString(inv))
            .Replace("__SEED__", seed.ToString(inv));
        var promptId = await PostGraphAsync(workflow, ct);

        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        // FLUX Schnell is fast, but the first call after a cold start must also load the
        // ~17 GB checkpoint into VRAM — allow generous headroom before giving up.
        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var hResp = await http.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (hResp.IsSuccessStatusCode)
            {
                using var hist = JsonDocument.Parse(await hResp.Content.ReadAsStringAsync(ct));
                if (hist.RootElement.TryGetProperty(promptId, out var entry))
                {
                    if (entry.TryGetProperty("status", out var st) &&
                        st.TryGetProperty("status_str", out var ss) &&
                        string.Equals(ss.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("FLUX image generation failed in ComfyUI (check checkpoint is on the share).");

                    if (entry.TryGetProperty("outputs", out var outs))
                    {
                        foreach (var node in outs.EnumerateObject())
                        {
                            if (node.Value.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
                            {
                                var fn = imgs[0].GetProperty("filename").GetString()
                                    ?? throw new InvalidOperationException("FLUX output had no filename");
                                string? sf = imgs[0].TryGetProperty("subfolder", out var s) ? s.GetString() : null;
                                return (fn, sf);
                            }
                        }
                    }
                }
            }
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("FLUX image generation timed out.");
    }

    /// <summary>
    /// Upload an image into ComfyUI's input directory (POST /upload/image). Returns the
    /// final filename ComfyUI assigned (it may de-duplicate, e.g. append "(1)"), which
    /// is what the I2V workflow's LoadImage node must reference. A unique name is used
    /// per upload so ComfyUI's LoadImage cache never serves a stale frame mid-chain.
    /// </summary>
    public async Task<string> UploadImageAsync(Stream image, string filename, CancellationToken ct, string? baseUrl = null)
    {
        baseUrl ??= _baseUrl;
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(image);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "image", filename);
        // overwrite=true keeps the exact name we generated (unique per call anyway).
        content.Add(new StringContent("true"), "overwrite");
        content.Add(new StringContent("input"), "type");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/upload/image") { Content = content };
        if (!string.IsNullOrEmpty(_authKey)) req.Headers.Add("X-Wan-Auth", _authKey);

        using var resp = await http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI image upload failed ({(int)resp.StatusCode}): {respBody}");

        using var doc = JsonDocument.Parse(respBody);
        var name = doc.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("ComfyUI upload did not return a name");
        // If ComfyUI placed it in a subfolder, LoadImage expects "subfolder/name".
        if (doc.RootElement.TryGetProperty("subfolder", out var sf) && !string.IsNullOrEmpty(sf.GetString()))
            name = $"{sf.GetString()}/{name}";
        return name;
    }

    // Physics / realism directives appended to every positive prompt so the
    // Lightning 4-step Wan2.2 renders obey gravity, weight and anatomy instead of
    // producing floaty, rubber-limbed, foot-sliding motion. Kept terse so it
    // reinforces — not drowns — the caller's motion description.
    private const string RealismSuffix =
        " Physically accurate motion: natural gravity, real weight and momentum, balanced posture, feet firmly planted on the ground, stable horizon. Anatomically correct human movement with natural joint articulation and realistic timing. Consistent lighting, shadows and reflections; solid object permanence; smooth coherent motion with no warping.";

    private static string WithRealismDirectives(string prompt)
    {
        prompt = (prompt ?? string.Empty).Trim();
        return prompt.Length == 0 ? RealismSuffix.Trim() : prompt + RealismSuffix;
    }

    // Frame count must be 4n+1 (temporal compression factor 4); spatial dims multiples
    // of 16. Seconds capped at 5 to keep each clip's render under the cost budget.
    private static (int frames, int width, int height, long seed) NormalizeDims(int seconds, int width, int height)
    {
        seconds = Math.Clamp(seconds, 1, 5);
        var frames = seconds * 16 + 1; // 17, 33, 49, 65, 81 — all 4n+1.
        width = Math.Max(256, (width / 16) * 16);
        height = Math.Max(256, (height / 16) * 16);
        var seed = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
        return (frames, width, height, seed);
    }

    // Shared ComfyUI /prompt POST. Wraps a substituted workflow graph as
    // {"prompt": <graph>, "client_id": "..."} and returns the prompt_id.
    private async Task<string> PostGraphAsync(string workflow, CancellationToken ct, string? baseUrl = null)
    {
        baseUrl ??= _baseUrl;
        var clientId = $"videotool-{Guid.NewGuid():N}";
        // `workflow` is the already-substituted graph JSON (an object literal), so embed it
        // directly — serializing it would double-encode it into a string and ComfyUI would reject it.
        var body = $"{{\"prompt\":{workflow},\"client_id\":{JsonSerializer.Serialize(clientId)}}}";

        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/prompt")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_authKey)) req.Headers.Add("X-Wan-Auth", _authKey);

        using var resp = await http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI submit failed ({(int)resp.StatusCode}): {respBody}");

        using var doc = JsonDocument.Parse(respBody);
        return doc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new InvalidOperationException("ComfyUI did not return prompt_id");
    }

    /// <summary>
    /// Asks ComfyUI to unload all cached models and release CUDA memory (POST /free
    /// with unload_models + free_memory). Used between the FLUX portrait phase and the
    /// Wan2.2 I2V clip phase: the 17 GB FLUX checkpoint must be evicted before the far
    /// larger video models load, otherwise both resident at once OOMs the 80 GB A100.
    /// ComfyUI processes the flag on its idle loop, so allow ~1-2s before enqueuing work.
    /// </summary>
    public async Task FreeMemoryAsync(CancellationToken ct)
    {
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/free")
        {
            Content = new StringContent("{\"unload_models\":true,\"free_memory\":true}", Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_authKey)) req.Headers.Add("X-Wan-Auth", _authKey);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ComfyUI /free failed ({(int)resp.StatusCode}): {Trunc(body, 200)}");
        }
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];

    /// <summary>
    /// Returns a Sora-shaped status JSON ({id, status, progress, ...}) so existing
    /// callers (frontend poll, push watcher, finalize) stay unchanged. Also includes
    /// outputFilename + outputSubfolder when the job finishes, used by Finalize.
    /// </summary>
    public async Task<string> GetStatusJsonAsync(string promptId, CancellationToken ct, string? baseUrl = null)
    {
        baseUrl ??= _baseUrl;
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        // /history/{id} populates only when the job reaches a terminal state.
        using var hResp = await http.GetAsync($"{baseUrl}/history/{Uri.EscapeDataString(promptId)}", ct);
        if (!hResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI history failed: {(int)hResp.StatusCode}");

        var hBody = await hResp.Content.ReadAsStringAsync(ct);
        using (var hist = JsonDocument.Parse(hBody))
        {
            if (hist.RootElement.TryGetProperty(promptId, out var entry))
            {
                var status = "succeeded";
                if (entry.TryGetProperty("status", out var st) &&
                    st.TryGetProperty("status_str", out var ss) &&
                    string.Equals(ss.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    status = "failed";
                }

                string? outputFilename = null;
                string? outputSubfolder = null;
                if (entry.TryGetProperty("outputs", out var outs))
                {
                    foreach (var node in outs.EnumerateObject())
                    {
                        // VHS_VideoCombine emits under "gifs" (legacy name) for video files.
                        if (node.Value.TryGetProperty("gifs", out var gifs) && gifs.GetArrayLength() > 0)
                        {
                            outputFilename = gifs[0].GetProperty("filename").GetString();
                            if (gifs[0].TryGetProperty("subfolder", out var sf)) outputSubfolder = sf.GetString();
                            break;
                        }
                        if (node.Value.TryGetProperty("videos", out var vids) && vids.GetArrayLength() > 0)
                        {
                            outputFilename = vids[0].GetProperty("filename").GetString();
                            if (vids[0].TryGetProperty("subfolder", out var sf)) outputSubfolder = sf.GetString();
                            break;
                        }
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    id = promptId,
                    status,
                    progress = status == "succeeded" ? 100 : 0,
                    outputFilename,
                    outputSubfolder,
                });
            }
        }

        // Not yet in history — distinguish queued vs running via /queue.
        var inFlight = false;
        var queued = false;
        try
        {
            using var qResp = await http.GetAsync($"{baseUrl}/queue", ct);
            if (qResp.IsSuccessStatusCode)
            {
                using var qDoc = JsonDocument.Parse(await qResp.Content.ReadAsStringAsync(ct));
                if (qDoc.RootElement.TryGetProperty("queue_running", out var run))
                    foreach (var item in run.EnumerateArray())
                        if (item.GetArrayLength() >= 2 && item[1].GetString() == promptId) inFlight = true;
                if (qDoc.RootElement.TryGetProperty("queue_pending", out var pend))
                    foreach (var item in pend.EnumerateArray())
                        if (item.GetArrayLength() >= 2 && item[1].GetString() == promptId) queued = true;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "queue probe failed for {Id}", promptId); }

        return JsonSerializer.Serialize(new
        {
            id = promptId,
            status = inFlight ? "running" : "queued",
            progress = 0,
        });
    }

    /// <summary>Cancel a queued or running ComfyUI job (best-effort).</summary>
    public async Task<(int statusCode, string body)> CancelAsync(string promptId, CancellationToken ct)
    {
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        // Remove from pending queue.
        try
        {
            var delBody = JsonSerializer.Serialize(new { delete = new[] { promptId } });
            using var del = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/queue")
            { Content = new StringContent(delBody, Encoding.UTF8, "application/json") };
            await http.SendAsync(del, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "cancel /queue delete failed"); }

        // Interrupt the currently running job (no targeting; we run one at a time).
        try
        {
            using var intr = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/interrupt");
            using var iResp = await http.SendAsync(intr, ct);
            return ((int)iResp.StatusCode, $"{{\"id\":\"{promptId}\",\"status\":\"cancelled\"}}");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "cancel /interrupt failed");
            return (200, $"{{\"id\":\"{promptId}\",\"status\":\"cancelled\"}}");
        }
    }

    /// <summary>
    /// Get status of a Wan-Animate job. Routes to the dedicated animate container
    /// (WAN_ANIMATE_BASE_URL) when configured, otherwise falls back to WAN_BASE_URL.
    /// Reuses the proven GetStatusJsonAsync logic (incl. output filename extraction).
    /// </summary>
    public Task<string> GetAnimateStatusJsonAsync(string promptId, CancellationToken ct)
        => GetStatusJsonAsync(promptId, ct, _animateUrl);

    /// <summary>
    /// Streams the rendered MP4 from ComfyUI's /view endpoint. Caller is
    /// responsible for streaming to blob and disposing the response.
    /// </summary>
    public async Task<HttpResponseMessage> DownloadAsync(string filename, string? subfolder, CancellationToken ct)
    {
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        var url = $"{_baseUrl}/view?filename={Uri.EscapeDataString(filename)}&type=output";
        if (!string.IsNullOrEmpty(subfolder)) url += $"&subfolder={Uri.EscapeDataString(subfolder)}";

        return await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Neural foley: synthesize a synced SFX/ambience track FROM a rendered clip using
    /// HunyuanVideo-Foley on the GPU. Uploads the video into ComfyUI's input dir, runs the
    /// foley workflow, polls /history, then downloads and returns the produced audio bytes +
    /// content type. Requires the audio-enabled GPU image (ComfyUI-HunyuanVideoFoley node +
    /// weights on the share). Throws if the node/weights/output aren't present, so the caller
    /// (the gated /api/foley endpoint) can fall back to the procedural soundscape.
    /// </summary>
    public async Task<(byte[] audio, string contentType)> RunFoleyAsync(
        Stream video, string videoFilename, string prompt, CancellationToken ct)
    {
        var inputName = await UploadVideoAsync(video, videoFilename, ct);
        var workflow = _foleyWorkflowJson.Value
            .Replace("__VIDEO__", JsonSerializer.Serialize(inputName))
            .Replace("__PROMPT__", JsonSerializer.Serialize(prompt ?? string.Empty));
        var promptId = await PostGraphAsync(workflow, ct);

        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var hResp = await http.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (hResp.IsSuccessStatusCode)
            {
                using var hist = JsonDocument.Parse(await hResp.Content.ReadAsStringAsync(ct));
                if (hist.RootElement.TryGetProperty(promptId, out var entry))
                {
                    if (entry.TryGetProperty("status", out var st) &&
                        st.TryGetProperty("status_str", out var ss) &&
                        string.Equals(ss.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("HunyuanVideo-Foley failed in ComfyUI (check the audio node + weights are present).");

                    if (entry.TryGetProperty("outputs", out var outs))
                    {
                        foreach (var node in outs.EnumerateObject())
                        {
                            // SaveAudio emits under "audio"; some video-foley nodes re-mux and emit
                            // under "gifs"/"videos" — accept whichever the installed node produces.
                            foreach (var key in new[] { "audio", "gifs", "videos" })
                            {
                                if (node.Value.TryGetProperty(key, out var arr) && arr.GetArrayLength() > 0)
                                {
                                    var fn = arr[0].GetProperty("filename").GetString()
                                        ?? throw new InvalidOperationException("foley output had no filename");
                                    string? sf = arr[0].TryGetProperty("subfolder", out var s) ? s.GetString() : null;
                                    using var dl = await DownloadAsync(fn, sf, ct);
                                    dl.EnsureSuccessStatusCode();
                                    var bytes = await dl.Content.ReadAsByteArrayAsync(ct);
                                    var ctype = dl.Content.Headers.ContentType?.ToString() ?? "audio/wav";
                                    return (bytes, ctype);
                                }
                            }
                        }
                    }
                }
            }
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("HunyuanVideo-Foley timed out.");
    }

    /// <summary>
    /// Generate an instrumental music score with ACE-Step (text-to-music) on the GPU.
    /// <paramref name="tags"/> is a comma-separated style/genre/mood/tempo brief (ACE-Step's prompt
    /// format, e.g. "cinematic, emotional strings, piano, hopeful, 90 bpm"); <paramref name="lyrics"/>
    /// is empty for an instrumental bed. Runs the ACE-Step workflow, polls /history, and returns the
    /// produced audio bytes + content type. Requires ace_step_v1_3.5b.safetensors on the checkpoints
    /// share + a recent ComfyUI (native ACE-Step nodes). Throws if absent so the caller can fall back.
    /// </summary>
    public async Task<(byte[] audio, string contentType)> GenerateMusicAsync(
        string tags, string lyrics, int seconds, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sec = Math.Clamp(seconds, 5, 240);
        var seed = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;
        var workflow = _aceWorkflowJson.Value
            .Replace("__TAGS__", JsonSerializer.Serialize(tags ?? string.Empty))
            .Replace("__LYRICS__", JsonSerializer.Serialize(lyrics ?? string.Empty))
            .Replace("__SECONDS__", sec.ToString(inv))
            .Replace("__SEED__", seed.ToString(inv));
        var promptId = await PostGraphAsync(workflow, ct);

        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        // ACE-Step renders minutes of music in seconds on an A100, but the first call also loads the
        // 3.5B checkpoint into VRAM — allow generous headroom before giving up.
        var deadline = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var hResp = await http.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}", ct);
            if (hResp.IsSuccessStatusCode)
            {
                using var hist = JsonDocument.Parse(await hResp.Content.ReadAsStringAsync(ct));
                if (hist.RootElement.TryGetProperty(promptId, out var entry))
                {
                    if (entry.TryGetProperty("status", out var st) &&
                        st.TryGetProperty("status_str", out var ss) &&
                        string.Equals(ss.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("ACE-Step music generation failed in ComfyUI (check the checkpoint is on the share).");
                    if (entry.TryGetProperty("outputs", out var outs))
                    {
                        foreach (var node in outs.EnumerateObject())
                        {
                            if (node.Value.TryGetProperty("audio", out var arr) && arr.GetArrayLength() > 0)
                            {
                                var fn = arr[0].GetProperty("filename").GetString()
                                    ?? throw new InvalidOperationException("ACE-Step output had no filename");
                                string? sf = arr[0].TryGetProperty("subfolder", out var s) ? s.GetString() : null;
                                using var dl = await DownloadAsync(fn, sf, ct);
                                dl.EnsureSuccessStatusCode();
                                var bytes = await dl.Content.ReadAsByteArrayAsync(ct);
                                var ctype = dl.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
                                return (bytes, ctype);
                            }
                        }
                    }
                }
            }
            await Task.Delay(1500, ct);
        }
        throw new TimeoutException("ACE-Step music generation timed out.");
    }

    /// <summary>
    /// Upload a video into ComfyUI's input directory so a VHS_LoadVideo node can read it.
    /// ComfyUI's core /upload/image endpoint stores any uploaded file into input/ (the field is
    /// historically named "image"); the returned name is what the workflow's loader references.
    /// </summary>
    private async Task<string> UploadVideoAsync(Stream video, string filename, CancellationToken ct, string? baseUrl = null)
    {
        baseUrl ??= _baseUrl;
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(video);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        content.Add(fileContent, "image", filename);
        content.Add(new StringContent("true"), "overwrite");
        content.Add(new StringContent("input"), "type");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/upload/image") { Content = content };
        if (!string.IsNullOrEmpty(_authKey)) req.Headers.Add("X-Wan-Auth", _authKey);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI video upload failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var name = doc.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("ComfyUI upload did not return a name");
        if (doc.RootElement.TryGetProperty("subfolder", out var sf) && !string.IsNullOrEmpty(sf.GetString()))
            name = $"{sf.GetString()}/{name}";
        return name;
    }
}
