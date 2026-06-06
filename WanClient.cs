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
    private readonly string? _authKey;
    private readonly Lazy<string> _workflowJson;
    private readonly ILogger<WanClient> _log;

    public WanClient(IHttpClientFactory hf, IConfiguration cfg, IHostEnvironment env, ILogger<WanClient> log)
    {
        _hf = hf;
        _log = log;
        _baseUrl = (cfg["WAN_BASE_URL"]
            ?? throw new InvalidOperationException("WAN_BASE_URL is not set")).TrimEnd('/');
        _authKey = cfg["WAN_AUTH_KEY"]; // optional shared secret, sent as X-Wan-Auth
        var workflowPath = cfg["WAN_WORKFLOW_PATH"]
            ?? Path.Combine(env.ContentRootPath, "workflows", "wan22-t2v.json");
        _workflowJson = new Lazy<string>(() => File.ReadAllText(workflowPath));
    }

    /// <summary>
    /// Submit a Wan2.2 text-to-video job. Returns the ComfyUI prompt_id (a UUID)
    /// which we surface as the "id" in the Sora-shaped response so the rest of
    /// the app keeps working unchanged.
    /// </summary>
    public async Task<string> SubmitAsync(string prompt, int seconds, int width, int height, CancellationToken ct)
    {
        // Wan2.2-T2V-A14B (dual-stage MoE) + Lightning 4-step LoRA: native 16 fps,
        // temporal compression factor 4 so total frame count must be 4n+1.
        // Cap at 5 s to keep render under ~60 s and stay inside the per-clip cost budget.
        seconds = Math.Clamp(seconds, 1, 5);
        var frames = seconds * 16 + 1; // 17, 33, 49, 65, 81 — all 4n+1.

        // Spatial dims must be multiples of 16. A14B native sizes: 1280x720, 720x1280, 832x480, 480x832.
        width = Math.Max(256, (width / 16) * 16);
        height = Math.Max(256, (height / 16) * 16);

        var seed = Random.Shared.NextInt64() & 0x7FFFFFFFFFFFFFFFL;

        var workflow = _workflowJson.Value
            .Replace("__PROMPT__", JsonSerializer.Serialize(prompt))
            .Replace("__WIDTH__", width.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__HEIGHT__", height.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__FRAMES__", frames.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SEED__", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var clientId = $"videotool-{Guid.NewGuid():N}";
        var body = $"{{\"prompt\":{workflow},\"client_id\":{JsonSerializer.Serialize(clientId)}}}";

        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/prompt")
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
    /// Returns a Sora-shaped status JSON ({id, status, progress, ...}) so existing
    /// callers (frontend poll, push watcher, finalize) stay unchanged. Also includes
    /// outputFilename + outputSubfolder when the job finishes, used by Finalize.
    /// </summary>
    public async Task<string> GetStatusJsonAsync(string promptId, CancellationToken ct)
    {
        var http = _hf.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        if (!string.IsNullOrEmpty(_authKey)) http.DefaultRequestHeaders.Add("X-Wan-Auth", _authKey);

        // /history/{id} populates only when the job reaches a terminal state.
        using var hResp = await http.GetAsync($"{_baseUrl}/history/{Uri.EscapeDataString(promptId)}", ct);
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
            using var qResp = await http.GetAsync($"{_baseUrl}/queue", ct);
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
}
