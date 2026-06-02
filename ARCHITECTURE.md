# videotool — Architecture & Operations

A single-tenant Sora-2 video generation tool: prompt → optional PromptCraft reasoning → Sora-2 render → blob storage → SAS URL.
Deployed to Azure App Service (Linux F1 free), gated by Microsoft Entra Easy Auth + a security-group claim.

- Last deployed commit: see `git log` on `main`
- CI: every push to `main` triggers [.github/workflows/deploy.yml](.github/workflows/deploy.yml) (OIDC federated, no secrets)
- Project rules: [SKILL.md](SKILL.md)
- Login design: [docs/login-plan.md](docs/login-plan.md)

---

## Azure resources (subscription `cc4e707a-06b6-43c5-85e1-3d6b406a33c2`)

Resource group: **`rg-videotool`**

| Resource | Type | Purpose |
|---|---|---|
| `asp-videotool` | App Service Plan (Linux, F1 Free) | Hosts the web app |
| `videotool-pritam003-23209` | App Service | .NET 8 minimal API + static `wwwroot/` |
| `videotool-aoai` | Azure OpenAI (eastus2) | Sora-2 + chat + reasoning |
| `videotoolstor6085` | Storage account (eastus2) | Container `videos` for rendered MP4s |

### AOAI deployments (on `videotool-aoai`)

| Deployment | Model | Version | SKU | Capacity | Used by |
|---|---|---|---|---|---|
| `sora-2` | sora-2 | 2025-12-08 | GlobalStandard | 1 | `/api/jobs`, `/api/remix` |
| `gpt-4o-mini` | gpt-4o-mini | 2024-07-18 | GlobalStandard | 50 | `/api/identity`, `/api/enhance`, `/api/translate` |
| `gpt-5-mini` | gpt-5-mini | 2025-08-07 | GlobalStandard | 50 | `/api/promptcraft` (reasoning loop) |

### Identity / auth

| Object | ID | Notes |
|---|---|---|
| Tenant | `2c2d753c-7844-4a95-8ed4-3844729e0803` | |
| App registration `videotool` | `054ed937-90d8-4874-94db-9cb7db9214bc` | Single-tenant, web platform; `groups` optional claim on id_token |
| Service principal | `f948a3f2-a342-459e-a6b7-c7223275a98c` | "Assignment required" enforced |
| Allowed group `videotool-allowed-users` | `913b2179-36bb-4451-97cd-e420657529b7` | Members can sign in |

Easy Auth v2 is configured directly via REST (`PUT authsettingsV2`); excluded paths: `/health`, `/.auth`, `/sw.js`, `/favicon.ico`, `/api/push/vapid-public-key`.

---

## App settings (`videotool-pritam003-23209`)

| Key | Value | Purpose |
|---|---|---|
| `AOAI_ENDPOINT` | `https://videotool-aoai.openai.azure.com/` | AOAI base URL |
| `AOAI_KEY` | *(secret)* | AOAI API key |
| `AOAI_DEPLOYMENT` | `sora-2` | Default video deployment |
| `CHAT_DEPLOYMENT` | `gpt-4o-mini` *(default in code)* | Prompt rewrite / vision |
| `VISION_DEPLOYMENT` | (unset → falls back to CHAT) | Identity locking from frames |
| `REASONING_DEPLOYMENT` | `gpt-5-mini` | PromptCraft critic loop |
| `STORAGE_ACCOUNT` | `videotoolstor6085` | Blob host |
| `STORAGE_CONTAINER` | `videos` | Container name |
| `AUTH_REQUIRED` | `1` | Enforce X-MS-CLIENT-PRINCIPAL |
| `ALLOWED_GROUP_ID` | `913b2179-...` | Group-claim gate |
| `SPEECH_KEY`, `SPEECH_REGION` | *(secret)* | Azure Speech for `/api/narrate` & `/api/voices` |

Managed identity: App Service system-assigned MI has **Storage Blob Data Contributor** on `videotoolstor6085` (used to mint SAS URLs and stream uploads).

---

## Endpoints (`Program.cs`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Liveness (public) |
| GET | `/api/me` | Echo principal claims (debug) |
| POST | `/api/jobs` | Submit Sora-2 video job |
| GET | `/api/jobs/{id}` | Poll job status (retries on 5xx/429) |
| POST | `/api/jobs/{id}/cancel` | Cancel in-flight job |
| POST | `/api/jobs/{id}/finalize` | Stream MP4 from Sora → Blob, return SAS |
| GET | `/api/videos` | List previously rendered videos |
| POST | `/api/stitch` | Concatenate clips |
| POST | `/api/identity` | Lock character identity from anchor frames (vision) |
| POST | `/api/tail` | Pull last N seconds of an existing video |
| POST | `/api/slice` | Cut a segment out of a video |
| POST | `/api/remix` | Sora-2 remix endpoint (new render from existing clip) |
| POST | `/api/narrate` | TTS narration (Azure Speech) |
| GET | `/api/voices` | List available TTS voices |
| POST | `/api/translate` | gpt-4o-mini translation |
| POST | `/api/enhance` | gpt-4o-mini prompt rewrite (Sora-optimized) |
| POST | `/api/promptcraft` | Start reasoning pipeline (PLAN → COMPILE → CRITIC × N) |
| GET | `/api/promptcraft/{jobId}` | Poll PromptCraft progress |
| POST | `/api/promptcraft/{jobId}/cancel` | Cancel a PromptCraft job |
| GET | `/api/push/vapid-public-key` | Web Push public VAPID key (public) |
| POST | `/api/push/subscribe` | Subscribe a browser to lock-screen notifications for a Sora job |

---

## Code flow

### Basic render (default, ~₹68/video at 8s/720p)

```
Browser (wwwroot/index.html)
  ↓ POST /api/jobs { prompt, n_seconds, size }
App Service (Program.cs)
  ↓ POST {AOAI}/openai/v1/video/generations/jobs?api-version=preview
videotool-aoai (Sora-2)
  ↓ returns { id, status: "queued" }
Browser polls GET /api/jobs/{id} (retries on 5xx/429)
  → status: in_progress → succeeded
Browser POST /api/jobs/{id}/finalize
  ↓ Sora returns generation_id, server downloads MP4 stream
  ↓ uploads stream → BlobContainerClient (videotoolstor6085/videos/{id}.mp4)
  ↓ mints user-delegation SAS (24h)
  → returns { sasUrl, generationId }
```

### PromptCraft pipeline (optional, +₹2.5/video)

```
POST /api/promptcraft { userAsk, totalSeconds, size, maxIterations }
  → spawns background Task, returns { jobId }
  Task:
    1. PLAN  (gpt-5-mini, max_completion_tokens=4000) → JSON storyboard
    2. COMPILE (gpt-5-mini, 6000) → Sora-ready prompt segments
    3. CRITIC × maxIter (gpt-5-mini, 6000):
         { faith, realism, soraCompat, frameCoherence, verdict }
         early-exit when all ≥9 AND verdict==ship
    4. final compiled prompt + scores stored in PromptCraftJobStore (10min TTL)
Browser polls /api/promptcraft/{jobId} → renders progress + final prompt
```

### Auth flow

```
Anonymous request → 302 to /.auth/login/aad
  → login.microsoftonline.com → tenant 2c2d753c
  → if user is in group videotool-allowed-users → callback with id_token
  → Easy Auth sidecar injects X-MS-CLIENT-PRINCIPAL-* headers
  → middleware verifies group claim 913b2179 in payload
    ↳ no header → 401
    ↳ wrong group → 403
    ↳ ok → request proceeds
```

---

## Cost model (₹85 ≈ $1)

Sora-2 dominates (~97% of every video).

| Config | Per video |
|---|---|
| 4s @ 720p | ~₹34 |
| 8s @ 720p | ~₹68 |
| 12s @ 720p | ~₹102 |
| 8s @ 1080p | ~₹204 |

Add ~₹2.50/video if PromptCraft is on (3 critic loops on gpt-5-mini).

**At ₹5,000/month budget** (current target):

| Scenario | Sustainable rate |
|---|---|
| 4s/720p, no PromptCraft | ~5 videos/day |
| 8s/720p, no PromptCraft | ~2 videos/day |
| 12s/720p, with PromptCraft | ~1.5 videos/day |
| 8s/1080p, with PromptCraft | <1 video/day |

For volumes above ~30 videos/day, self-hosting Wan 2.2 on Azure Container Apps (Serverless GPU, A100, scale-to-zero) is ~6× cheaper per video at ~₹15–18.

---

## Operational notes

- F1 plan is single-instance: in-memory job stores (`PromptCraftJobStore`, `SoraJobWatcherRegistry`) are fine, no Redis needed. State is lost on restart.
- VAPID keys auto-persist to `/home/data/vapid.json` on App Service Linux (survives restarts; lost only if the plan is recreated).
- Cost Management `query` API rate-limits hard on MSA subscriptions; for real-time spend, use `az monitor metrics list` against the AOAI account with `--filter "ModelDeploymentName eq '...'"`.
- Easy Auth `az webapp auth microsoft update` may hang on a preview-extension prompt. Prefer `az rest GET/PUT authsettingsV2`.
- `htm` in `wwwroot/index.html` does **not** decode HTML entities — write `&` not `&amp;` in JSX literals.

---

## Recent commits (cost-mitigation thread)

| SHA | Change |
|---|---|
| `c9aafb8` | Default `REASONING_DEPLOYMENT` → `gpt-5-mini`; old `gpt-5_4` deployment removed |
| `7eb80bd` | PLAN 8K→4K, COMPILE/CRITIC 12K→6K tokens; default critic loops 5→3 |
| `d9f1241` | Easy Auth gating + `/api/me` + frontend redirect |
| `bfe9e13` | Web Push lock-screen notifications + service worker |
| `6f04d25` | Wake Lock + interruptible sleep for backgrounded mobile tabs |
