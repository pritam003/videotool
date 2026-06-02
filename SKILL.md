# Project Skill: Azure CLI + Git-Driven Deployment

This skill defines the standard operating rules for working in this workspace.
Any agent or contributor MUST follow these rules end-to-end.

## 1. Azure: Always Use Azure CLI

- For **any** Azure operation (create, read, update, delete, deploy, configure,
  diagnose), use the **Azure CLI (`az`)**.
- Do **not** use the Azure Portal, ARM REST calls, or ad-hoc SDK scripts when
  an `az` command exists.
- Always parameterize: resource group, location, names come from variables —
  never hardcode secrets.
- Verify success with a follow-up `az ... show` or `az ... list` command.

## 2. Cleanup of Unused Files After Deployment

After every successful deployment:

1. Identify build/deploy artifacts no longer needed:
   - `bin/`, `obj/`, `publish/`, `out/`
   - `*.zip` deployment packages, temp logs, `*.tmp`, `*.log`, `.env.local`
2. Remove them from the working tree.
3. Ensure they are listed in [.gitignore](.gitignore) so they never re-enter the repo.
4. Audit:
   ```bash
   git status
   git clean -nd        # dry-run preview
   ```
5. Re-run a smoke test against the deployed app to confirm cleanup didn't break anything.

## 3. Git-Only Workflow — Everything Lives in Git

- Repository is the **single source of truth**. Every file required to build,
  configure, or deploy MUST be committed.
- Secrets stay in GitHub Actions secrets / Azure Key Vault — never in the repo.
- Standard flow:
  ```bash
  git status
  git add -A
  git commit -m "<concise message>"
  git pull --rebase origin main
  git push origin main
  ```

## 4. Deploy to Azure via Git Only

Deployments are driven by Git — never by uploading zips or editing in the portal.

- Never deploy by drag-drop, FTP, Kudu zip upload, or manual portal edits.
- The only trigger for a deployment is `git push` (or `workflow_dispatch` on the
  same workflow).

---

## 5. This Repo's CI/CD Setup (concrete)

### Azure resources (provisioned with `az`)

| Item | Value |
|---|---|
| Subscription | `cc4e707a-06b6-43c5-85e1-3d6b406a33c2` (Visual Studio Enterprise) |
| Tenant | `2c2d753c-7844-4a95-8ed4-3844729e0803` |
| Resource group | `rg-videotool` |
| Region | `centralus` |
| App Service Plan | `asp-videotool` (Linux, F1 free tier) |
| Web App | `videotool-pritam003-23209` |
| Runtime | `DOTNETCORE:8.0` |
| Public URL | https://videotool-pritam003-23209.azurewebsites.net |
| AI Services account | `videotool-aoai` (kind `AIServices`, S0, eastus2) |
| AOAI endpoint | `https://videotool-aoai.cognitiveservices.azure.com/` |
| Model deployment | `sora-2` (model `sora-2`, version `2025-12-08`, GlobalStandard, capacity 1) |
| Storage account | `videotoolstor6085` (StandardLRS, blob public access disabled) |
| Blob container | `videos` |
| App Service identity | system-assigned managed identity |
| MI roles | `Cognitive Services OpenAI User` on `videotool-aoai`; `Storage Blob Data Contributor` on `videotoolstor6085` |
| App settings | `AOAI_ENDPOINT`, `AOAI_DEPLOYMENT=sora-2`, `STORAGE_ACCOUNT`, `STORAGE_CONTAINER=videos` |

### GitHub repo

- Repo: https://github.com/pritam003/videotool (public)
- Branch: `main`
- Environment: `production`
- Workflow: [.github/workflows/deploy.yml](.github/workflows/deploy.yml)

### Auth: OIDC federated credentials (no client secrets)

- Entra app: `gh-oidc-videotool`
- App / client ID: `1dbd231d-4a2a-426b-9a6b-83c7e2431648`
- Federated credentials:
  - `repo:pritam003/videotool:ref:refs/heads/main`
  - `repo:pritam003/videotool:environment:production`
- Role assignment scope: the web app resource only (least privilege)
  - Roles: `Website Contributor`, `Contributor`

### GitHub Actions secrets / variables

| Kind | Name | Purpose |
|---|---|---|
| Secret | `AZURE_CLIENT_ID` | Entra app ID for OIDC login |
| Secret | `AZURE_TENANT_ID` | Entra tenant |
| Secret | `AZURE_SUBSCRIPTION_ID` | Target subscription |
| Variable | `AZURE_WEBAPP_NAME` | Target web app name |

### Deployment flow

```
git push origin main
        │
        ▼
GitHub Actions (.github/workflows/deploy.yml)
  1. build job:  dotnet restore → build → publish → upload artifact
  2. deploy job: download artifact → azure/login (OIDC) → azure/webapps-deploy → az logout
        │
        ▼
Azure App Service: videotool-pritam003-23209
```

### Reproducing the setup from scratch

```bash
# 1. Provision Azure resources
RG=rg-videotool LOC=centralus PLAN=asp-videotool APP=<unique-name>
az group create -n $RG -l $LOC
az appservice plan create -g $RG -n $PLAN --sku F1 --is-linux
az webapp create -g $RG -p $PLAN -n $APP --runtime "DOTNETCORE:8.0"

# 2. Create Entra app for OIDC
APP_ID=$(az ad app create --display-name gh-oidc-videotool --query appId -o tsv)
az ad sp create --id $APP_ID
az ad app federated-credential create --id $APP_ID --parameters '{
  "name":"github-main",
  "issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:pritam003/videotool:ref:refs/heads/main",
  "audiences":["api://AzureADTokenExchange"]
}'
az ad app federated-credential create --id $APP_ID --parameters '{
  "name":"github-env-production",
  "issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:pritam003/videotool:environment:production",
  "audiences":["api://AzureADTokenExchange"]
}'

# 3. Grant least-privilege role on the web app only
SCOPE=$(az webapp show -g $RG -n $APP --query id -o tsv)
az role assignment create --assignee $APP_ID --role "Website Contributor" --scope $SCOPE
az role assignment create --assignee $APP_ID --role "Contributor"          --scope $SCOPE

# 4. Configure GitHub repo
gh secret   set AZURE_CLIENT_ID       --body "$APP_ID"
gh secret   set AZURE_TENANT_ID       --body "<tenant-id>"
gh secret   set AZURE_SUBSCRIPTION_ID --body "<subscription-id>"
gh variable set AZURE_WEBAPP_NAME     --body "$APP"
gh api -X PUT repos/pritam003/videotool/environments/production --silent

# 5. Deploy (Git-only)
git push origin main
```

## 6. Pre-Deploy Checklist (must pass every time)

- [ ] Working tree clean (`git status` shows no surprises)
- [ ] Build artifacts (`bin/`, `obj/`, `publish/`) ignored, not committed
- [ ] All required files committed and pushed to `main`
- [ ] Azure resources targeted via `az` and confirmed (`az account show`)
- [ ] Deployment triggered **only** by `git push` (no manual uploads)
- [ ] Post-deploy: `curl https://<app>.azurewebsites.net/health` returns 200
