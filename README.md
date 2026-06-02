# videotool

Minimal .NET 8 web app deployed to Azure App Service via GitHub Actions (OIDC).

- App entrypoint: [Program.cs](Program.cs)
- CI/CD workflow: [.github/workflows/deploy.yml](.github/workflows/deploy.yml)
- Project rules: [SKILL.md](SKILL.md)

## Deploy

Every push to `main` triggers the GitHub Actions workflow, which builds and
deploys to Azure App Service using OIDC federated credentials (no secrets).
