using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace VideoTool;

// =============================================================================
//  Server-side story render pipeline.
//
//  The whole multi-clip film render (cast → clip chain → narrate → mux → stitch)
//  used to run in the browser. It now runs as a DURABLE BACKGROUND JOB on the
//  server: the UI submits a finalized story spec, is freed immediately to start
//  another, and a single background worker drains a queue one film at a time
//  (the GPU is single-instance). Job state is persisted to blob so completed
//  films survive an app restart and show up when the user returns. Cancel is the
//  only interrupt — a per-job CancellationTokenSource the worker honours and that
//  also cancels the in-flight GPU clip.
//
//  Implementation note: the worker reuses the app's OWN existing HTTP endpoints
//  (/api/jobs, /api/flux-image, /api/extract-last-frame, /api/narrate,
//  /api/concat-audio, /api/mux-audio, /api/stitch) over localhost, carrying an
//  internal token so the auth middleware lets it through. This avoids
//  re-implementing the GPU + ffmpeg + TTS plumbing; only the ORCHESTRATION is
//  ported here from the former JS runStoryChain().
// =============================================================================

/// <summary>Per-process secret that lets the background worker call the app's own
/// endpoints over localhost without an Easy Auth principal.</summary>
public sealed class InternalAuth
{
    public string Token { get; } = Guid.NewGuid().ToString("N");
}

public sealed record CastMemberSpec(string Id, string Name, string Description, string? Voice, string? RefName);

/// <summary>The finalized film the UI submits: a reviewed storyboard + cast +
/// voice choices + render options. Everything the worker needs to render headless.</summary>
public sealed record StorySpec
{
    public string Language { get; init; } = "English";
    public string Size { get; init; } = "832x480";
    public int ClipSeconds { get; init; } = 5;
    public bool Narrate { get; init; } = true;
    public string NarratorVoice { get; init; } = "en-US-AvaMultilingualNeural";
    public string? DialogVoice { get; init; }
    public string ImageEngine { get; init; } = "flux"; // "flux" | "comfy"
    public int Crossfade { get; init; }
    public JsonNode? Story { get; init; }
    public List<CastMemberSpec> Cast { get; init; } = new();
}

public sealed class StoryJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled film";
    public StorySpec Spec { get; set; } = new();
    public string Status { get; set; } = "queued"; // queued|running|succeeded|failed|cancelled|interrupted
    public double Pct { get; set; }
    public string Label { get; set; } = "queued";
    public string? ResultUrl { get; set; }
    public List<string> ClipUrls { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonIgnore] public CancellationTokenSource? Cts { get; set; }
    [JsonIgnore] public string? CurrentGpuJobId { get; set; }
}

/// <summary>In-memory job registry + bounded FIFO queue + best-effort blob
/// persistence. F1 is single-instance so a process-local dictionary is fine;
/// blob persistence is what lets finished films reappear after an idle recycle.</summary>
public sealed class StoryRenderQueue
{
    private readonly ConcurrentDictionary<string, StoryJob> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly IConfiguration _cfg;
    private readonly TokenCredential _cred;
    private readonly ILogger<StoryRenderQueue> _log;
    private BlobContainerClient? _container;

    private static readonly JsonSerializerOptions PersistOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public StoryRenderQueue(IConfiguration cfg, TokenCredential cred, ILogger<StoryRenderQueue> log)
    {
        _cfg = cfg; _cred = cred; _log = log;
    }

    public ChannelReader<string> Reader => _queue.Reader;

    public StoryJob Enqueue(StorySpec spec, string title)
    {
        var job = new StoryJob { Spec = spec, Title = title, Cts = new CancellationTokenSource() };
        _jobs[job.Id] = job;
        _ = PersistAsync(job);
        _queue.Writer.TryWrite(job.Id);
        return job;
    }

    public StoryJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<StoryJob> List() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        if (j.Status is "queued" or "running")
        {
            try { j.Cts?.Cancel(); } catch { /* already disposed */ }
            if (j.Status == "queued") { j.Status = "cancelled"; j.Label = "cancelled"; j.FinishedAt = DateTimeOffset.UtcNow; _ = PersistAsync(j); }
        }
        return true;
    }

    private BlobContainerClient? Container()
    {
        if (_container is not null) return _container;
        var account = _cfg["STORAGE_ACCOUNT"];
        if (string.IsNullOrWhiteSpace(account)) return null;
        var name = _cfg["STORAGE_CONTAINER"] ?? "videos";
        var svc = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), _cred);
        _container = svc.GetBlobContainerClient(name);
        return _container;
    }

    public async Task PersistAsync(StoryJob job)
    {
        try
        {
            var c = Container();
            if (c is null) return;
            var blob = c.GetBlobClient($"story-jobs/{job.Id}.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(job, PersistOpts);
            using var ms = new MemoryStream(bytes);
            await blob.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "story-job persist failed for {Id}", job.Id);
        }
    }

    public async Task LoadPersistedAsync(CancellationToken ct)
    {
        try
        {
            var c = Container();
            if (c is null) return;
            await foreach (var item in c.GetBlobsAsync(prefix: "story-jobs/", cancellationToken: ct))
            {
                try
                {
                    var blob = c.GetBlobClient(item.Name);
                    var resp = await blob.DownloadContentAsync(ct);
                    var job = JsonSerializer.Deserialize<StoryJob>(resp.Value.Content.ToString());
                    if (job is null) continue;
                    // A render that was mid-flight when the process died can't be
                    // resumed reliably (some clips are rendered, some not) — surface
                    // it as interrupted so the user can re-submit.
                    if (job.Status is "queued" or "running")
                    {
                        job.Status = "interrupted";
                        job.Label = "interrupted by an app restart — re-submit to render";
                        job.FinishedAt ??= DateTimeOffset.UtcNow;
                    }
                    _jobs.TryAdd(job.Id, job);
                }
                catch { /* skip a corrupt record */ }
            }
            _log.LogInformation("loaded {N} persisted story jobs", _jobs.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "story-job load failed");
        }
    }
}

/// <summary>Mutable per-render view of a cast member (description edits + the
/// rendered/uploaded reference frame name).</summary>
internal sealed class CastWork
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string? Voice;
    public string? RefName;
}

/// <summary>The single background worker. Drains the queue one film at a time and
/// runs the full render by calling the app's own endpoints over localhost.</summary>
public sealed class StoryRenderWorker : BackgroundService
{
    private readonly StoryRenderQueue _queue;
    private readonly IHttpClientFactory _hf;
    private readonly IServer _server;
    private readonly InternalAuth _auth;
    private readonly ILogger<StoryRenderWorker> _log;
    private string? _base;

    public StoryRenderWorker(StoryRenderQueue queue, IHttpClientFactory hf, IServer server,
        InternalAuth auth, ILogger<StoryRenderWorker> log)
    {
        _queue = queue; _hf = hf; _server = server; _auth = auth; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await _queue.LoadPersistedAsync(stop);

        await foreach (var id in _queue.Reader.ReadAllAsync(stop))
        {
            var job = _queue.Get(id);
            if (job is null || job.Status == "cancelled") continue;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, job.Cts?.Token ?? CancellationToken.None);
            try
            {
                job.Status = "running"; job.Pct = 1; job.Label = "starting";
                await _queue.PersistAsync(job);
                await RunJobAsync(job, linked.Token);
                if (job.Status == "running")
                {
                    job.Status = "succeeded"; job.Pct = 100; job.Label = "film ready";
                }
            }
            catch (OperationCanceledException) when (job.Cts?.IsCancellationRequested == true)
            {
                job.Status = "cancelled"; job.Label = "cancelled";
                await TryCancelGpuAsync(job);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                job.Status = "interrupted"; job.Label = "server stopping — re-submit to render";
                await TryCancelGpuAsync(job);
            }
            catch (Exception ex)
            {
                job.Status = "failed"; job.Error = Trunc(ex.Message, 400); job.Label = "error";
                _log.LogError(ex, "story job {Id} failed", job.Id);
            }
            finally
            {
                job.CurrentGpuJobId = null;
                job.FinishedAt = DateTimeOffset.UtcNow;
                await _queue.PersistAsync(job);
            }
        }
    }

    // ---- the ported orchestration -----------------------------------------

    private async Task RunJobAsync(StoryJob job, CancellationToken ct)
    {
        var spec = job.Spec;
        var story = spec.Story ?? new JsonObject();
        var clips = (story["clips"] as JsonArray) ?? new JsonArray();
        var N = clips.Count;
        if (N == 0) throw new InvalidOperationException("storyboard has no clips");

        var clipSec = Math.Clamp(spec.ClipSeconds, 3, 5);
        var size = string.IsNullOrWhiteSpace(spec.Size) ? "832x480" : spec.Size;
        var narrating = spec.Narrate;
        var engine = spec.ImageEngine == "comfy" ? "comfy" : "flux";

        // Working cast map (id -> editable view with reference frame).
        var cast = new Dictionary<string, CastWork>();
        foreach (var c in spec.Cast)
            cast[c.Id] = new CastWork { Id = c.Id, Name = c.Name, Description = c.Description, Voice = c.Voice, RefName = c.RefName };

        // ---- 1) CAST: render a canonical reference for every character that
        //         doesn't already have one (uploaded leads keep their RefName). ----
        var castList = cast.Values.ToList();
        for (int i = 0; i < castList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = castList[i];
            if (!string.IsNullOrWhiteSpace(c.RefName)) continue;
            job.Pct = 2 + (i / (double)Math.Max(1, castList.Count)) * 5;
            job.Label = $"casting {c.Name}";
            var prompt = CastSnapshotPrompt(c, story);
            var (name, _) = await RenderPortraitAsync(prompt, engine, size, ct);
            c.RefName = name;
        }

        // ---- 2) CLIP CHAIN: first clip seeds from the cast reference; every
        //         later clip seeds from the previous clip's extracted last frame. ----
        var finals = new List<string>();
        string? prevFrameName = null;
        JsonNode? prevClip = null;

        for (int i = 0; i < N; i++)
        {
            ct.ThrowIfCancellationRequested();
            var clip = clips[i]!;
            var label = $"clip {i + 1}/{N}";
            var basePct = 8 + (i / (double)N) * 84;
            var spanPct = (1 / (double)N) * 80;
            job.Pct = basePct;
            job.Label = $"{label}: {S(clip["title"], label)}";

            string startImage;
            if (i == 0 || prevFrameName is null)
            {
                var seed = await ClipStartImageAsync(clip, story, cast, engine, size, ct);
                if (seed is null) throw new InvalidOperationException($"{label}: no character reference available");
                startImage = seed;
            }
            else startImage = prevFrameName;

            var thePrompt = BuildClipPrompt(clip, story, prevClip, narrating, cast);
            var gpuId = await SubmitI2VAsync(startImage, thePrompt, clipSec, size, ct);
            job.CurrentGpuJobId = gpuId;
            await PollClipAsync(gpuId, ct, (st, elapsed) =>
            {
                var frac = Math.Min(0.98, elapsed / (clipSec * 16000.0));
                job.Pct = basePct + spanPct * frac;
                job.Label = $"{label}: {st} ({elapsed / 1000}s)";
            });
            job.CurrentGpuJobId = null;

            job.Label = $"{label}: finalizing";
            var fin = await PostJsonAsync("/api/jobs/" + gpuId + "/finalize", new { }, ct);
            var clipUrl = fin.GetProperty("url").GetString()!;

            // Extract THIS clip's last frame (from the silent render) to seed the next.
            if (i < N - 1)
            {
                try
                {
                    var ef = await PostJsonAsync("/api/extract-last-frame",
                        new { videoUrl = clipUrl, size }, ct);
                    prevFrameName = ef.GetProperty("name").GetString();
                }
                catch { prevFrameName = null; }
            }

            // Per-clip audio: dialogue (speaker voice) + narration (narrator voice).
            if (narrating)
            {
                try
                {
                    var nLine = S(clip["narration"], "").Trim();
                    var dLine = S(clip["dialog"], "").Trim();
                    var speakerId = S(clip["speaker"], "");
                    if (string.IsNullOrEmpty(speakerId) && clip["characters"] is JsonArray cs && cs.Count == 1)
                        speakerId = S(cs[0], "");
                    var dVoice = (!string.IsNullOrEmpty(speakerId) && cast.TryGetValue(speakerId, out var sw) && !string.IsNullOrWhiteSpace(sw.Voice))
                        ? sw.Voice!
                        : (!string.IsNullOrWhiteSpace(spec.DialogVoice) ? spec.DialogVoice! : spec.NarratorVoice);

                    var tracks = new List<string>();
                    if (dLine.Length > 0)
                    {
                        job.Label = $"{label}: dialogue";
                        var dr = await PostJsonAsync("/api/narrate", new { text = dLine, voice = dVoice }, ct);
                        tracks.Add(dr.GetProperty("url").GetString()!);
                    }
                    if (nLine.Length > 0)
                    {
                        job.Label = $"{label}: narrating";
                        var nr = await PostJsonAsync("/api/narrate", new { text = nLine, voice = spec.NarratorVoice }, ct);
                        tracks.Add(nr.GetProperty("url").GetString()!);
                    }
                    if (tracks.Count > 0)
                    {
                        var audioUrl = tracks[0];
                        if (tracks.Count > 1)
                        {
                            var cr = await PostJsonAsync("/api/concat-audio",
                                new { audioUrls = tracks.ToArray(), gapMs = 350 }, ct);
                            audioUrl = cr.GetProperty("url").GetString()!;
                        }
                        var mr = await PostJsonAsync("/api/mux-audio",
                            new { videoUrl = clipUrl, audioUrl, mode = "replace" }, ct);
                        clipUrl = mr.GetProperty("url").GetString()!;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "{Label} audio failed; clip kept silent", label); }
            }

            finals.Add(clipUrl);
            job.ClipUrls = new List<string>(finals);
            await _queue.PersistAsync(job);
            prevClip = clip;
        }

        // ---- 3) STITCH into the final film (hard cuts when narrated). ----
        if (finals.Count >= 2)
        {
            job.Pct = 95; job.Label = $"stitching {finals.Count} clips into the film";
            var cf = narrating ? 0 : spec.Crossfade;
            try
            {
                var r = await PostJsonAsync("/api/stitch",
                    new { urls = finals.ToArray(), crossfade = cf }, ct);
                job.ResultUrl = r.GetProperty("url").GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "stitch failed; clips kept individually");
                job.Error = "stitch failed — individual clips are available";
            }
        }
        else if (finals.Count == 1)
        {
            job.ResultUrl = finals[0];
        }
    }

    // ---- prompt builders (ported 1:1 from the JS) -------------------------

    private static string CastSnapshotPrompt(CastWork c, JsonNode story)
    {
        var look = (c.Description ?? "").Trim();
        var style = S(story["style"], "");
        var name = string.IsNullOrWhiteSpace(c.Name) ? "the character" : c.Name;
        return $"Cinematic full-body character portrait of {name}: {look}. Single subject, standing, facing camera, neutral seamless studio backdrop, soft even key light, sharp focus, photorealistic, accurate human anatomy and proportions. {style}. No on-screen text, captions, or watermarks.";
    }

    private static string BuildClipPrompt(JsonNode clip, JsonNode story, JsonNode? prevClip,
        bool narrating, Dictionary<string, CastWork> cast)
    {
        var castById = new Dictionary<string, (string name, string desc)>();
        if (story["cast"] is JsonArray sc)
            foreach (var c in sc)
                if (c is not null) castById[S(c["id"], "")] = (S(c["name"], ""), S(c["description"], ""));
        foreach (var kv in cast) castById[kv.Key] = (kv.Value.Name, kv.Value.Description); // user edits win

        var locById = new Dictionary<string, (string name, string desc)>();
        if (story["locations"] is JsonArray sl)
            foreach (var l in sl)
                if (l is not null) locById[S(l["id"], "")] = (S(l["name"], ""), S(l["description"], ""));

        var charIds = (clip["characters"] as JsonArray)?.Select(x => S(x, "")).Where(s => s.Length > 0).ToList() ?? new();
        var present = charIds.Where(id => castById.ContainsKey(id)).Select(id => (id, castById[id])).ToList();
        var who = string.Join("; ", present.Select(p => $"{(string.IsNullOrEmpty(p.Item2.name) ? p.id : p.Item2.name)} — {p.Item2.desc}"));

        var locId = S(clip["location"], "");
        var where = locById.TryGetValue(locId, out var loc) ? $"Location: {(string.IsNullOrEmpty(loc.name) ? locId : loc.name)}, {loc.desc}. " : "";
        var idn = who.Length > 0 ? $"Featuring {who}. " : "";

        var cont = "";
        if (prevClip is not null)
        {
            var pa = S(prevClip["action"], "").Trim();
            cont = $"This clip continues STRICTLY from the previous shot — its first frame IS the previous shot's final frame, so keep the exact same character identities, faces, wardrobe, location, lighting and camera continuity. Previously: {pa}. Now the story moves forward: ";
        }

        var baseLine = S(clip["motionPrompt"], "");
        if (baseLine.Length == 0) baseLine = S(clip["action"], "");

        var nLine = narrating ? S(clip["narration"], "").Trim() : "";
        var dLine = narrating ? S(clip["dialog"], "").Trim() : "";
        var acting = !narrating ? ""
            : dLine.Length > 0
                ? " In this moment the character is speaking their line aloud — show natural, well-timed lip movement and a facial expression that matches the emotion. Never render captions, subtitles or any on-screen text."
                : nLine.Length > 0
                    ? " Show the character's facial expression and emotion clearly. Never render captions, subtitles or any on-screen text."
                    : "";

        return $"{idn}{where}{cont}{baseLine}{acting}".Trim();
    }

    private async Task<string?> ClipStartImageAsync(JsonNode clip, JsonNode story,
        Dictionary<string, CastWork> cast, string engine, string size, CancellationToken ct)
    {
        var charIds = (clip["characters"] as JsonArray)?.Select(x => S(x, "")).Where(s => s.Length > 0).ToList() ?? new();
        var present = charIds.Where(id => cast.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c.RefName)).ToList();
        if (present.Count == 0)
            return cast.Values.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.RefName))?.RefName;
        if (present.Count == 1) return cast[present[0]].RefName;

        var locById = new Dictionary<string, (string name, string desc)>();
        if (story["locations"] is JsonArray sl)
            foreach (var l in sl)
                if (l is not null) locById[S(l["id"], "")] = (S(l["name"], ""), S(l["description"], ""));
        var who = string.Join(". ", present.Select(id =>
        {
            var c = cast[id];
            return $"{(string.IsNullOrEmpty(c.Name) ? id : c.Name)}: {c.Description}";
        }));
        var locId = S(clip["location"], "");
        var where = locById.TryGetValue(locId, out var loc)
            ? $"{(string.IsNullOrEmpty(loc.name) ? locId : loc.name)} — {loc.desc}"
            : S(story["setting"], "");
        var action = S(clip["action"], "");
        var style = S(story["style"], "");
        var prompt = $"Cinematic establishing wide shot showing multiple characters together in the same frame. {who}. Setting: {where}. {action}. All characters fully visible and in-frame, photorealistic, consistent lighting, physically plausible composition. {style}. No on-screen text, captions, or watermarks.";
        var (name, _) = await RenderPortraitAsync(prompt, engine, size, ct);
        return name;
    }

    // ---- portrait + still rendering (self-HTTP) ---------------------------

    private async Task<(string name, string? preview)> RenderPortraitAsync(string prompt, string engine, string size, CancellationToken ct)
    {
        if (engine == "comfy") return await RenderStillFromTextAsync(prompt, size, ct);
        try { return await FluxPortraitAsync(prompt, size, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FLUX portrait failed; falling back to ComfyUI still");
            return await RenderStillFromTextAsync(prompt, size, ct);
        }
    }

    private async Task<(string name, string? preview)> FluxPortraitAsync(string prompt, string size, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (code, body) = await PostJsonRawAsync("/api/flux-image", new { prompt, size }, ct);
            if (code >= 200 && code < 300)
            {
                using var doc = JsonDocument.Parse(body);
                return (doc.RootElement.GetProperty("name").GetString()!,
                        doc.RootElement.TryGetProperty("previewUrl", out var p) ? p.GetString() : null);
            }
            if (code == 503 && body.Contains("warming", StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(15000, ct); continue; }
            throw new HttpRequestException($"/api/flux-image -> {code}: {Trunc(body, 300)}");
        }
        throw new TimeoutException("FLUX did not become ready after several minutes");
    }

    private async Task<(string name, string? preview)> RenderStillFromTextAsync(string prompt, string size, CancellationToken ct)
    {
        var gpuId = await SubmitT2VAsync(prompt, 1, size, ct);
        await PollClipAsync(gpuId, ct, null);
        var fin = await PostJsonAsync("/api/jobs/" + gpuId + "/finalize", new { }, ct);
        var url = fin.GetProperty("url").GetString()!;
        var ef = await PostJsonAsync("/api/extract-last-frame", new { videoUrl = url, size }, ct);
        return (ef.GetProperty("name").GetString()!,
                ef.TryGetProperty("previewUrl", out var p) ? p.GetString() : null);
    }

    // ---- GPU job submit + poll (self-HTTP, with warm-up retry) ------------

    private Task<string> SubmitI2VAsync(string startImage, string prompt, int sec, string size, CancellationToken ct)
        => SubmitJobAsync(prompt, sec, size, startImage, ct);

    private Task<string> SubmitT2VAsync(string prompt, int sec, string size, CancellationToken ct)
        => SubmitJobAsync(prompt, sec, size, null, ct);

    private async Task<string> SubmitJobAsync(string prompt, int sec, string size, string? startImage, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var client = NewClient();
            using var form = new MultipartFormDataContent
            {
                { new StringContent(prompt), "prompt" },
                { new StringContent(sec.ToString()), "seconds" },
                { new StringContent(size), "size" },
            };
            if (!string.IsNullOrEmpty(startImage)) form.Add(new StringContent(startImage), "startImage");

            using var resp = await client.PostAsync(BaseUrl() + "/api/jobs", form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.GetProperty("id").GetString()!;
            }
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && body.Contains("warming", StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(15000, ct); continue; }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests || body.Contains("Too many running tasks", StringComparison.OrdinalIgnoreCase))
            { await Task.Delay(5000 + attempt * 2000, ct); continue; }
            throw new HttpRequestException($"/api/jobs -> {(int)resp.StatusCode}: {Trunc(body, 300)}");
        }
        throw new TimeoutException("GPU did not become ready after several minutes");
    }

    private async Task PollClipAsync(string id, CancellationToken ct, Action<string, long>? onProgress)
    {
        long elapsed = 0;
        int transient = 0;
        var hardStop = DateTimeOffset.UtcNow.AddMinutes(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > hardStop) throw new TimeoutException($"job {id} exceeded 15 min");
            var wait = elapsed < 60_000 ? 6000 : elapsed < 120_000 ? 4000 : 3000;
            await Task.Delay(wait, ct);
            elapsed += wait;

            int code; string body;
            try { (code, body) = await GetRawAsync("/api/jobs/" + id, ct); }
            catch { if (++transient >= 6) throw; continue; }

            if (code == 429 || (code >= 500 && code <= 599))
            {
                if (++transient >= 6) throw new HttpRequestException($"status {code} 6x for job {id}");
                continue;
            }
            transient = 0;

            string st;
            try { using var doc = JsonDocument.Parse(body); st = doc.RootElement.GetProperty("status").GetString() ?? "unknown"; }
            catch { continue; }

            onProgress?.Invoke(st, elapsed);
            if (st is "succeeded" or "completed") return;
            if (st is "failed" or "cancelled") throw new InvalidOperationException($"job {st}");
        }
    }

    private async Task TryCancelGpuAsync(StoryJob job)
    {
        var gid = job.CurrentGpuJobId;
        if (string.IsNullOrEmpty(gid)) return;
        try
        {
            using var client = NewClient();
            using var resp = await client.PostAsync(BaseUrl() + "/api/jobs/" + gid + "/cancel", null);
        }
        catch { /* best effort */ }
    }

    // ---- self-HTTP plumbing ----------------------------------------------

    private HttpClient NewClient()
    {
        var c = _hf.CreateClient();
        c.Timeout = TimeSpan.FromMinutes(10);
        c.DefaultRequestHeaders.Remove("X-Internal-Token");
        c.DefaultRequestHeaders.Add("X-Internal-Token", _auth.Token);
        return c;
    }

    private async Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        var (code, txt) = await PostJsonRawAsync(path, body, ct);
        if (code < 200 || code >= 300)
            throw new HttpRequestException($"{path} -> {code}: {Trunc(txt, 300)}");
        using var doc = JsonDocument.Parse(txt);
        return doc.RootElement.Clone();
    }

    private async Task<(int code, string body)> PostJsonRawAsync(string path, object body, CancellationToken ct)
    {
        using var client = NewClient();
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(BaseUrl() + path, content, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task<(int code, string body)> GetRawAsync(string path, CancellationToken ct)
    {
        using var client = NewClient();
        using var resp = await client.GetAsync(BaseUrl() + path, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private string BaseUrl()
    {
        if (_base is not null) return _base;
        try
        {
            var feat = _server.Features.Get<IServerAddressesFeature>();
            var addr = feat?.Addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    ?? feat?.Addresses?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(addr))
            {
                _base = Normalize(addr);
                return _base;
            }
        }
        catch { /* fall through to env */ }

        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            _base = Normalize(urls.Split(';')[0]);
            return _base;
        }
        var port = Environment.GetEnvironmentVariable("WEBSITES_PORT")
                 ?? Environment.GetEnvironmentVariable("PORT") ?? "8080";
        _base = $"http://127.0.0.1:{port}";
        return _base;
    }

    private static string Normalize(string addr) =>
        addr.Replace("://0.0.0.0", "://127.0.0.1")
            .Replace("://[::]", "://127.0.0.1")
            .Replace("://+", "://127.0.0.1")
            .TrimEnd('/');

    // ---- small helpers ----------------------------------------------------

    private static string S(JsonNode? n, string fallback)
    {
        if (n is null) return fallback;
        try { return n.GetValue<string>(); }
        catch { try { return n.ToString(); } catch { return fallback; } }
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];
}

/// <summary>Shapes a <see cref="StoryJob"/> for the UI without leaking the full
/// (potentially large) storyboard JSON on every poll.</summary>
public static class StoryJobView
{
    public static object Summary(StoryJob j) => new
    {
        id = j.Id,
        title = j.Title,
        status = j.Status,
        pct = Math.Round(j.Pct, 1),
        label = j.Label,
        resultUrl = j.ResultUrl,
        clips = j.ClipUrls.Count,
        error = j.Error,
        createdAt = j.CreatedAt,
        finishedAt = j.FinishedAt,
        language = j.Spec.Language,
        size = j.Spec.Size,
        narrate = j.Spec.Narrate
    };

    public static object Full(StoryJob j) => new
    {
        id = j.Id,
        title = j.Title,
        status = j.Status,
        pct = Math.Round(j.Pct, 1),
        label = j.Label,
        resultUrl = j.ResultUrl,
        clipUrls = j.ClipUrls,
        error = j.Error,
        createdAt = j.CreatedAt,
        finishedAt = j.FinishedAt,
        language = j.Spec.Language,
        size = j.Spec.Size,
        narrate = j.Spec.Narrate,
        crossfade = j.Spec.Crossfade
    };
}

