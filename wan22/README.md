# Wan 2.2 TI2V-5B on Azure Container Apps Serverless GPU

Self-hosted text-to-video alternative to Sora-2. Apache-2.0, no face-block, scale-to-zero pricing.

## Cost (per A100, ~₹85/$)

| Mode | $/hr | ₹/clip (5s, ~9min) | ₹/day @ 2hr |
|---|---|---|---|
| A100 80GB on-demand | $3.67 | ₹47 | ₹624 |
| (idle, scale-to-zero) | $0 | — | — |

## Prerequisites

1. **A100 quota** — Default is 0. Request on `standardNCADSA100v4Family`:
   ```bash
   az quota update --resource-name "standardNCADSA100v4Family" \
       --scope "/subscriptions/<SUB_ID>/providers/Microsoft.Compute/locations/swedencentral" \
       --value 24
   ```
2. **Region with GPU workload profile** — Sweden Central, West US 3, Australia East.
3. **Existing storage account** — reuses `videotoolstor6085/videos`.

## Deploy

```bash
cd wan22
bash deploy.sh
```

Takes 20-40 min (image build with 28GB weights bake). Output prints the FQDN and bearer token.

## Wire to App Service

```bash
az webapp config appsettings set -g <rg> -n videotool-pritam003-23209 \
    --settings \
      LOCAL_VIDEO_ENDPOINT="https://wan22-gpu.<region>.azurecontainerapps.io" \
      LOCAL_VIDEO_TOKEN="<token-from-deploy>"
```

App Service restart picks up the env vars. UI toggle "Generate with: ◯ Sora-2 ◉ Wan 2.2 (self-hosted)" routes to `/api/jobs/local`.

## Architecture

```
videotool (App Service F1)
  └─ POST /api/jobs/local
       └─ POST <wan22-fqdn>/infer  (Bearer)
            └─ uploads to videotoolstor6085/videos
            └─ returns SAS URL
       └─ returns to UI as a normal job result
```

No polling needed — `/infer` is synchronous (~9 min). Frontend shows progress via long-polling status.

## Trade-offs vs Sora-2

| | Sora-2 | Wan 2.2 |
|---|---|---|
| Quality (text-to-video) | Higher | Good |
| Identity from photo | Blocked (face-RAI) | Works |
| Audio | Native | None (mux via /api/narrate) |
| Cost/clip | $0.50 | $0.51 |
| Latency | 60-90s | 9 min |
| Throughput | 1 concurrent | 1 concurrent |
| Setup | None | 1 deploy |

**Use Sora for**: fast iteration, audio-native, non-human subjects.
**Use Wan for**: human characters, license control, no content gating.
