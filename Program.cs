using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// POST /api/generate { prompt, seconds?, size? }
app.MapPost("/api/generate", async (GenerateRequest req,
    IHttpClientFactory httpFactory,
    TokenCredential credential,
    IConfiguration cfg,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Prompt))
        return Results.BadRequest(new { error = "prompt is required" });

    var endpoint = cfg["AOAI_ENDPOINT"]?.TrimEnd('/')
        ?? throw new InvalidOperationException("AOAI_ENDPOINT not set");
    var apiKey = cfg["AOAI_KEY"]
        ?? throw new InvalidOperationException("AOAI_KEY not set");
    var deployment = cfg["AOAI_DEPLOYMENT"] ?? "sora-2";
    var storageAccount = cfg["STORAGE_ACCOUNT"]
        ?? throw new InvalidOperationException("STORAGE_ACCOUNT not set");
    var container = cfg["STORAGE_CONTAINER"] ?? "videos";

    // Sora 2 requires seconds as a string ("4" | "8" | "12")
    var secondsInt = req.Seconds is > 0 and <= 20 ? req.Seconds.Value : 4;
    var seconds = secondsInt.ToString();
    var size = string.IsNullOrWhiteSpace(req.Size) ? "1280x720" : req.Size!;

    var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromMinutes(10);
    http.DefaultRequestHeaders.Add("api-key", apiKey);

    // 1. Submit video generation
    var submitUrl = $"{endpoint}/openai/v1/videos";
    var submitBody = JsonSerializer.Serialize(new
    {
        model = deployment,
        prompt = req.Prompt,
        seconds,
        size
    });
    using var submit = await http.PostAsync(submitUrl,
        new StringContent(submitBody, Encoding.UTF8, "application/json"), ct);
    var submitJson = await submit.Content.ReadAsStringAsync(ct);
    if (!submit.IsSuccessStatusCode)
        return Results.Problem($"Submit failed ({(int)submit.StatusCode}): {submitJson}");

    using var submitDoc = JsonDocument.Parse(submitJson);
    var videoId = submitDoc.RootElement.GetProperty("id").GetString()!;

    // 2. Poll until terminal state
    string status = "queued";
    var deadline = DateTime.UtcNow.AddMinutes(8);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        using var poll = await http.GetAsync($"{endpoint}/openai/v1/videos/{videoId}", ct);
        var pollJson = await poll.Content.ReadAsStringAsync(ct);
        if (!poll.IsSuccessStatusCode)
            return Results.Problem($"Poll failed ({(int)poll.StatusCode}): {pollJson}");
        using var pollDoc = JsonDocument.Parse(pollJson);
        status = pollDoc.RootElement.GetProperty("status").GetString() ?? "unknown";
        if (status == "completed") break;
        if (status is "failed" or "cancelled")
            return Results.Problem($"Job {status}: {pollJson}");
    }
    if (status != "completed")
        return Results.Problem($"Timed out waiting for video (last status: {status})");

    // 3. Download MP4
    using var videoResp = await http.GetAsync(
        $"{endpoint}/openai/v1/videos/{videoId}/content?variant=video", ct);
    if (!videoResp.IsSuccessStatusCode)
    {
        var err = await videoResp.Content.ReadAsStringAsync(ct);
        return Results.Problem($"Video download failed ({(int)videoResp.StatusCode}): {err}");
    }
    var mp4 = await videoResp.Content.ReadAsByteArrayAsync(ct);

    // 4. Upload to blob storage (managed identity)
    var blobService = new BlobServiceClient(
        new Uri($"https://{storageAccount}.blob.core.windows.net"), credential);
    var containerClient = blobService.GetBlobContainerClient(container);
    var blobName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{videoId}.mp4";
    var blob = containerClient.GetBlobClient(blobName);
    using (var ms = new MemoryStream(mp4))
    {
        await blob.UploadAsync(ms, overwrite: true, ct);
    }

    // 5. Issue user-delegation SAS (1 hour)
    var udk = await blobService.GetUserDelegationKeyAsync(
        DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1), ct);
    var sas = new BlobSasBuilder
    {
        BlobContainerName = container,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
    };
    sas.SetPermissions(BlobSasPermissions.Read);
    var sasToken = sas.ToSasQueryParameters(udk.Value, storageAccount).ToString();
    var url = $"{blob.Uri}?{sasToken}";

    return Results.Ok(new { url, blob = blobName, videoId, seconds, size });
});

app.Run();

public record GenerateRequest(string Prompt, int? Seconds, string? Size);
