# Project Skill: Azure CLI + Git-Driven Deployment

This skill defines the standard operating rules for working in this workspace.
Any agent or contributor MUST follow these rules end-to-end.

## 1. Azure: Always Use Azure CLI

- For **any** Azure operation (create, read, update, delete, deploy, configure,
  diagnose), use the **Azure CLI (`az`)**.
- Do **not** use the Azure Portal, ARM REST calls, or ad-hoc SDK scripts when
  an `az` command exists.
- Prefer official commands over shell hacks. Examples:
  - Login: `az login`
  - Set subscription: `az account set --subscription "<name-or-id>"`
  - Resource group: `az group create -n <rg> -l <region>`
  - Web app deploy: `az webapp up` / `az webapp deployment source config`
  - Functions: `az functionapp ...`
  - Static Web Apps: `az staticwebapp ...`
- Always parameterize: resource group, location, and names come from variables
  or prompts — never hardcode secrets.
- Verify success with a follow-up `az ... show` or `az ... list` command.

## 2. Cleanup of Unused Files After Deployment

After every successful deployment:

1. Identify build/deploy artifacts no longer needed at runtime:
   - `dist/`, `build/`, `out/`, `.next/cache/`, `coverage/`
   - `*.zip` deployment packages produced locally
   - Temporary logs, `*.tmp`, `*.log`, local `.env.local` copies
   - Unused scaffolding files, dead code, orphaned assets
2. Remove them from the working tree.
3. Ensure they are listed in `.gitignore` so they never re-enter the repo.
4. Run a quick audit:
   ```bash
   git status
   git clean -nd        # dry-run preview
   ```
   Only run `git clean -fd` after confirming nothing important is listed.
5. Re-run the app/build locally (or smoke-test the deployed Azure resource) to
   confirm cleanup did not break anything.

## 3. Git-Only Workflow — Everything Lives in Git

- The repository is the **single source of truth**. Every file required to
  build, configure, or deploy the project MUST be committed to Git.
- No file should exist only on a local machine or only in Azure. If it matters,
  it is in the repo (with secrets externalized to Key Vault / app settings).
- Standard flow for any change:
  ```bash
  git status
  git add -A
  git commit -m "<concise message>"
  git pull --rebase origin <branch>
  git push origin <branch>
  ```
- Branch hygiene: work on feature branches, open PRs, keep `main` deployable.

## 4. Deploy to Azure via Git Only

Deployments are driven by Git — never by uploading zips or editing in the
portal.

Acceptable mechanisms (pick one per resource and stick to it):

- **Azure App Service / Functions — Local Git or GitHub deployment**
  ```bash
  az webapp deployment source config-local-git \
      -n <app> -g <rg>
  # or
  az webapp deployment source config \
      -n <app> -g <rg> \
      --repo-url <github-url> --branch main --git-token <token>
  ```
  Then `git push azure main` (for local git) — deployment is triggered by the
  push.

- **Static Web Apps**: connect the SWA resource to the GitHub repo; deployments
  happen automatically on push via the generated GitHub Actions workflow.

- **GitHub Actions / Azure Pipelines**: workflow YAML files live in the repo
  (`.github/workflows/` or `azure-pipelines.yml`) and run `az` commands. A
  `git push` is the only trigger.

Rules:
- Never deploy by drag-drop, FTP, Kudu zip upload, or manual portal edits.
- If a deployment is needed, the answer is always: **commit, push, let the
  Git-connected pipeline deploy**.
- If the Git connection is missing, set it up first (using `az`), then deploy.

## 5. Pre-Deploy Checklist (must pass every time)

- [ ] Working tree clean (`git status` shows no surprises)
- [ ] Unused/build artifacts removed and `.gitignore` updated
- [ ] All required files committed and pushed to the correct branch
- [ ] Azure resources targeted via `az` and confirmed (`az account show`)
- [ ] Deployment triggered **only** by `git push` (no manual uploads)
- [ ] Post-deploy verification with `az ... show` and an app smoke test
