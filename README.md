# avideo

Minimal .NET 8 web app deployed to Azure App Service via GitHub Actions (OIDC).

- App entrypoint: [Program.cs](Program.cs)
- CI/CD workflow: [.github/workflows/deploy.yml](.github/workflows/deploy.yml)
- **Architecture, resources, endpoints, code flow, cost model: [ARCHITECTURE.md](ARCHITECTURE.md)**
- Project rules: [SKILL.md](SKILL.md)
- Login design: [docs/login-plan.md](docs/login-plan.md)

## Deploy

Every push to `main` triggers the GitHub Actions workflow, which builds and
deploys to Azure App Service using OIDC federated credentials (no secrets).
# Trigger redeploy
