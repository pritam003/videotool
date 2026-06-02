# Videotool — Rebuild Guide

Everything needed to spin this app back up from scratch on Azure after the resource group has been deleted. Pair with [ARCHITECTURE.md](ARCHITECTURE.md) for code-flow context.

> Source of truth for code: this repo (`main` branch, commit `506153d` or later).
> All Azure infra was created manually via `az` CLI — no Bicep/azd in this repo (yet). The script at the bottom recreates everything.

---

## 1. Snapshot of what existed (deleted on 2 Jun 2026)

### Subscription & tenant
| Key | Value |
|---|---|
| Subscription name | `Visual Studio Enterprise Subscription` |
| Subscription ID | `cc4e707a-06b6-43c5-85e1-3d6b406a33c2` |
| Tenant ID | `2c2d753c-7844-4a95-8ed4-3844729e0803` |
| Owner OID | `c3036fa6-bc04-4e6b-8d56-6be8f94ebaf2` |

### Resource group
| Key | Value |
|---|---|
| Name | `rg-videotool` |
| Location | `centralus` |

### Resources inside `rg-videotool`
| Resource | Type | SKU / Kind | Region | Notes |
|---|---|---|---|---|
| `asp-videotool` | App Service Plan | Linux **F1 Free** | centralus | Single instance, in-memory state OK |
| `videotool-pritam003-23209` | App Service (Linux) | `DOTNETCORE\|8.0` | centralus | HTTPS not enforced (F1) |
| `videotool-aoai` | Azure AI Services (Cognitive) | `S0` / kind=`AIServices` | **eastus2** | Cross-region from app — by design (Sora-2 region) |
| `videotoolstor6085` | Storage Account | `Standard_LRS` / `StorageV2` | eastus2 | Container `videos`, public-blob disabled |

### AOAI deployments (on `videotool-aoai`)
| Deployment name | Model | Version | SKU | Capacity | Used for |
|---|---|---|---|---|---|
| `sora-2` | sora-2 | 2025-12-08 | GlobalStandard | 1 | Video generation |
| `gpt-5-mini` | gpt-5-mini | 2025-08-07 | GlobalStandard | 50 | PromptCraft + chat/translate (default for both) |

> Note: `gpt-4o-mini` deployment was removed; `gpt-5-mini` now serves both `CHAT_DEPLOYMENT` and `REASONING_DEPLOYMENT`. If you want a cheaper chat path, recreate `gpt-4o-mini` (cap 50, GlobalStandard) and set `CHAT_DEPLOYMENT=gpt-4o-mini`.

### Entra (auth) artifacts — these survive RG deletion
| Artifact | Value |
|---|---|
| Security group `videotool-allowed-users` | `913b2179-36bb-4451-97cd-e420657529b7` |
| App registration `videotool` | appId `054ed937-90d8-4874-94db-9cb7db9214bc` |
| Service principal | objectId `f948a3f2-a342-459e-a6b7-c7223275a98c` |
| Sign-in audience | `AzureADMyOrg` (single-tenant) |
| Redirect URI | `https://<webapp-host>/.auth/login/aad/callback` |
| Group claim | `SecurityGroup` on idToken (`groups` claim) |

> ⚠️ The app registration **client secret** (`MICROSOFT_PROVIDER_AUTHENTICATION_SECRET`) is NOT recoverable. Always create a fresh secret on rebuild (script does this).

### App settings on the webapp (names — values rotated on rebuild)
```
AOAI_ENDPOINT                              = https://videotool-aoai.cognitiveservices.azure.com/
AOAI_KEY                                   = <from cognitiveservices keys list>
AOAI_DEPLOYMENT                            = sora-2
CHAT_DEPLOYMENT                            = gpt-5-mini   (or gpt-4o-mini if recreated)
REASONING_DEPLOYMENT                       = gpt-5-mini
SPEECH_REGION                              = eastus2
STORAGE_ACCOUNT                            = videotoolstor<XXXX>
STORAGE_CONTAINER                          = videos
AUTH_REQUIRED                              = 1
ALLOWED_GROUP_ID                           = 913b2179-36bb-4451-97cd-e420657529b7
MICROSOFT_PROVIDER_AUTHENTICATION_SECRET   = <fresh client secret>
```

### Easy Auth v2 config (set via PUT authsettingsV2)
- `platform.enabled = true`
- `globalValidation.unauthenticatedClientAction = RedirectToLoginPage`
- `globalValidation.redirectToProvider = azureactivedirectory`
- `globalValidation.excludedPaths = [/health, /.auth/*, /sw.js, /favicon.ico, /api/push/vapid-public-key]`
- `identityProviders.azureActiveDirectory.registration.clientId = <APP_ID>`
- `identityProviders.azureActiveDirectory.registration.openIdIssuer = https://login.microsoftonline.com/<TENANT>/v2.0`
- `identityProviders.azureActiveDirectory.registration.clientSecretSettingName = MICROSOFT_PROVIDER_AUTHENTICATION_SECRET`
- `login.tokenStore.enabled = true`

### Code-side knobs (in `Program.cs`, no env override required)
- Default `REASONING_DEPLOYMENT` fallback = `gpt-5-mini` (line ~1016)
- PLAN max tokens = 4000, COMPILE = 6000, CRITIC = 6000
- PromptCraft default `maxIterations` = 3 (frontend `wwwroot/index.html` line ~670)
- `input_reference` clamped to [1,5] sec for Sora-2 anchor frames

---

## 2. One-shot recreate script

Run from this repo root. Idempotent on names where possible — change `STAMP` to avoid collisions.

```bash
# scripts/recreate-azure.sh — see file in this repo
bash scripts/recreate-azure.sh
```

The script:
1. Creates RG `rg-videotool` in `centralus`
2. Creates Storage `videotoolstor<RANDOM>` + container `videos`
3. Creates AOAI account `videotool-aoai` in `eastus2` (kind=AIServices, S0)
4. Creates deployments `sora-2` (cap 1) + `gpt-5-mini` (cap 50)
5. Creates Linux F1 App Service Plan + .NET 8 webapp
6. Reuses existing app registration `054ed937-...` and group `913b2179-...` (skips creation if present)
7. Mints a fresh client secret and writes `MICROSOFT_PROVIDER_AUTHENTICATION_SECRET`
8. Sets all required app settings
9. Configures Easy Auth v2 with the excluded-paths list
10. Configures GitHub deployment from `https://github.com/pritam003/videotool.git` branch `main`

Estimated rebuild time: **8–12 minutes** (mostly waiting for AOAI capacity).

---

## 3. Manual steps the script does NOT do

1. **Add yourself (or other users) to the security group** — already there from before, but to add others:
   ```bash
   az ad group member add --group 913b2179-36bb-4451-97cd-e420657529b7 --member-id <USER_OID>
   ```
2. **Sora-2 region availability** — must remain in eastus2 (or whatever region Microsoft has Sora-2 GA'd in at rebuild time). If eastus2 capacity is full, try `swedencentral`.
3. **Custom domain / TLS** — not configured here (free tier). Add later if needed.
4. **Push notifications VAPID keys** — currently generated in code at startup; persistent across restarts only if you store them in app settings (TODO, not done).
5. **GPU quota request** (ticket `2606020030007681`, currently open) — only relevant if pivoting to self-hosted Wan/LTX on ACA Serverless GPU; not part of the Sora-2 path.

---

## 4. Validate after rebuild

```bash
# Health (unauthenticated allowed)
curl -sf https://videotool-pritam003-23209.azurewebsites.net/health

# Auth gate works (should redirect)
curl -sI https://videotool-pritam003-23209.azurewebsites.net/api/me | grep -i location

# Trigger a sample job (after browser-login + grabbing a session cookie, or set AUTH_REQUIRED=0 temporarily)
```

---

## 5. Tear-down (clean delete)

```bash
az group delete -n rg-videotool --yes --no-wait
# App reg + group + SP survive; delete only if you really want to:
# az ad app delete --id 054ed937-90d8-4874-94db-9cb7db9214bc
# az ad group delete --group 913b2179-36bb-4451-97cd-e420657529b7
```

---

## 6. Cost reference (₹85 ≈ $1, 2 Jun 2026)

Per-video cost is dominated by Sora-2:

| Config | Per video | 5/day → /month |
|---|---|---|
| 5s · 720p (no PromptCraft) | ₹42 | ₹6,300 |
| 4s · 720p (no PromptCraft) | ₹34 | ₹5,100 |
| 8s · 720p (no PromptCraft) | ₹68 | ₹10,200 |
| 12s · 720p · PromptCraft | ₹102 + ₹2.50 | ₹15,675 |

Infra (App Service F1 + Storage + AOAI idle) ≈ ₹0–₹50/month.

Within ₹5K/month budget on current Sora-2 path: **~5 videos/day at 4s/720p** is the sweet spot.

---

## 7. Useful repo files

- [Program.cs](Program.cs) — minimal API, ~1700 lines
- [wwwroot/index.html](wwwroot/index.html) — React 18 + htm, no build step
- [ARCHITECTURE.md](ARCHITECTURE.md) — detailed code flow, endpoints, diagrams
- [scripts/recreate-azure.sh](scripts/recreate-azure.sh) — the rebuild script
