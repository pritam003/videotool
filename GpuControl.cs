using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace VideoTool;

/// <summary>
/// Start / stop / status for the scale-to-zero Wan2.2 GPU (the <c>wan22</c> Azure Container App)
/// via the Azure Resource Manager (management plane). This lets the UI wake the GPU, force it to
/// stop (cutting A100 billing immediately instead of waiting out the idle cooldown), and read its
/// true running state WITHOUT sending data-plane traffic that would itself keep the GPU warm.
///
/// Auth: uses the App Service system-assigned managed identity (the shared <see cref="TokenCredential"/>)
/// to get an ARM token. The identity must hold a role with start/stop/read on the container app —
/// granted resource-scoped (Contributor on the wan22 container app). Target resource is configured
/// via GPU_SUBSCRIPTION_ID (+ optional GPU_RESOURCE_GROUP / GPU_CONTAINERAPP, defaulted).
/// </summary>
public sealed class GpuControl
{
    private readonly IHttpClientFactory _hf;
    private readonly TokenCredential _cred;
    private readonly ILogger<GpuControl> _log;
    private readonly string _sub;
    private readonly string _rg;
    private readonly string _name;

    private const string ApiVersion = "2024-03-01";
    private static readonly string[] ArmScope = { "https://management.azure.com/.default" };

    public GpuControl(IHttpClientFactory hf, TokenCredential cred, IConfiguration cfg, ILogger<GpuControl> log)
    {
        _hf = hf; _cred = cred; _log = log;
        _sub = cfg["GPU_SUBSCRIPTION_ID"] ?? cfg["AZURE_SUBSCRIPTION_ID"] ?? "";
        _rg = cfg["GPU_RESOURCE_GROUP"] ?? "rg-videotool";
        _name = cfg["GPU_CONTAINERAPP"] ?? "wan22";
    }

    /// <summary>True only when a target subscription is configured (otherwise the endpoints report "unknown").</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(_sub);

    private string ResourceUrl =>
        $"https://management.azure.com/subscriptions/{_sub}/resourceGroups/{_rg}/providers/Microsoft.App/containerApps/{_name}";

    private async Task<HttpClient> ArmClientAsync(CancellationToken ct)
    {
        var token = await _cred.GetTokenAsync(new TokenRequestContext(ArmScope), ct);
        var http = _hf.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>
    /// Reads the container app's running state from ARM and maps it to a small status:
    ///   stopped  — manually stopped (no replicas, $0 GPU)
    ///   idle     — Running but scaled to zero (no replicas yet; a render cold-starts it)
    ///   ready    — Running with ≥1 active replica (warm, serving)
    ///   unknown  — not configured or ARM call failed
    /// Uses only management-plane reads, so it never sends traffic that would keep the GPU warm.
    /// </summary>
    public async Task<GpuStatusView> GetStatusAsync(CancellationToken ct)
    {
        if (!Configured)
            return new GpuStatusView("unknown", null, 0, "GPU_SUBSCRIPTION_ID not configured");
        try
        {
            var http = await ArmClientAsync(ct);
            using var resp = await http.GetAsync($"{ResourceUrl}?api-version={ApiVersion}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var b = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("gpu status ARM get failed {Code}: {Body}", (int)resp.StatusCode, Trunc(b, 300));
                return new GpuStatusView("unknown", null, 0, $"ARM get {(int)resp.StatusCode}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var props = doc.RootElement.GetProperty("properties");
            var running = props.TryGetProperty("runningStatus", out var rs) ? rs.GetString() : null;
            var latestReady = props.TryGetProperty("latestReadyRevisionName", out var lr) ? lr.GetString() : null;

            if (string.Equals(running, "Stopped", StringComparison.OrdinalIgnoreCase))
                return new GpuStatusView("stopped", running, 0, null);

            // Running (or transitioning): count active replicas on the latest ready revision.
            // 0 replicas => idle (scaled to zero); ≥1 => actually warm.
            var replicas = 0;
            if (!string.IsNullOrWhiteSpace(latestReady))
            {
                try
                {
                    using var rr = await http.GetAsync($"{ResourceUrl}/revisions/{latestReady}/replicas?api-version={ApiVersion}", ct);
                    if (rr.IsSuccessStatusCode)
                    {
                        using var rdoc = JsonDocument.Parse(await rr.Content.ReadAsStringAsync(ct));
                        if (rdoc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                            replicas = arr.GetArrayLength();
                    }
                }
                catch (Exception ex) { _log.LogDebug(ex, "replica count failed"); }
            }
            var state = replicas > 0 ? "ready" : "idle";
            return new GpuStatusView(state, running, replicas, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "gpu status failed");
            return new GpuStatusView("unknown", null, 0, ex.Message);
        }
    }

    /// <summary>Start the container app (idempotent). Needed when it was explicitly Stopped — a
    /// Stopped app won't auto-start on ingress traffic, so this must run before any warm render.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        EnsureConfigured();
        var http = await ArmClientAsync(ct);
        using var resp = await http.PostAsync($"{ResourceUrl}/start?api-version={ApiVersion}", null, ct);
        // 2xx = accepted; Conflict = already running/starting (fine).
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ARM start failed ({(int)resp.StatusCode}): {Trunc(body, 300)}");
        }
    }

    /// <summary>Stop the container app immediately (kills all replicas → $0 A100 billing now,
    /// rather than waiting out the scale-to-zero idle cooldown).</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        EnsureConfigured();
        var http = await ArmClientAsync(ct);
        using var resp = await http.PostAsync($"{ResourceUrl}/stop?api-version={ApiVersion}", null, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ARM stop failed ({(int)resp.StatusCode}): {Trunc(body, 300)}");
        }
    }

    private void EnsureConfigured()
    {
        if (!Configured)
            throw new InvalidOperationException("GPU control is not configured (set GPU_SUBSCRIPTION_ID).");
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];
}

/// <summary>Small status projection returned to the UI.</summary>
public sealed record GpuStatusView(string State, string? Running, int Replicas, string? Note);
