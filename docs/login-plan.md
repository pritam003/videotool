# Login Plan — Google SSO gated by an Entra (AAD) group

> Status: approved plan. Implementation tracked separately.

## 1. Goals & non-goals

**Goals**
- Users log in with their Google (Gmail) account.
- Only Gmail addresses present in an Entra ID (AAD) group can authenticate; everyone else gets rejected (not just hidden — actually 401/403).
- Whitelist is managed by adding/removing members in the Entra group, not by editing app config or redeploying.
- All current endpoints (`/api/jobs`, `/api/videos`, `/api/stitch`, `/api/identity`, `/api/promptcraft`, `/api/push/*`, `/api/render/*`, etc.) require auth. A short list of public endpoints (`/health`, `/sw.js`, `/favicon.ico`, `/api/push/vapid-public-key`) stay anonymous.
- Existing UX (htm/React, no build step) keeps working — auth must be redirect-based, no SPA library.
- Web Push notifications keep working when the user is logged out (the server holds the subscription).

**Non-goals**
- Multi-tenant SaaS / self-service signup.
- Roles/admin tiers — single "allowed" tier.
- Session sharing across devices beyond what the IdP cookie provides.
- Migrating away from F1 (single instance is fine; auth is stateless cookie).

## 2. The three viable architectures

| | A. Easy Auth + Google IdP + app-side whitelist | B. Easy Auth + Microsoft IdP + Entra B2B (Google federation) + group claim | C. Custom OIDC in code (Google) + Graph group lookup |
|---|---|---|---|
| Login button goes to | Google directly | login.microsoftonline.com → redirects to Google | Google directly |
| Token validation | Easy Auth sidecar | Easy Auth sidecar | We write it |
| Whitelist source | App reads Entra group via Graph and matches `email` claim | Token contains `groups` claim — Easy Auth gates with "Assignment required" | App reads Entra group via Graph |
| User experience | "Sign in with Google" → done in 1 step | "Sign in with Microsoft" → "Sign in with Google" → 2 hops | "Sign in with Google" → done in 1 step |
| Code in `Program.cs` | Tiny middleware checking `email` claim | ~5 lines: `[Authorize]` + group ID check from claims | Full OIDC handler, cookie auth, refresh, PKCE (~200+ lines) |
| Whitelist UX | Awkward — Entra groups don't take raw email strings as members | Standard B2B guest invite + add to group | Awkward (same as A) |
| Risk | `email` claim only safe if `email_verified=true` | Bulletproof — no per-request Graph call | We own all auth code → larger attack surface |
| F1 friendliness | ✅ no app overhead | ✅ no app overhead | ⚠️ adds memory/CPU, cookie store |

### The hidden gotcha that decides this

You can't put a raw `gmail.com` email string into an Entra group as a member. Entra group members must be **directory objects** (users, service principals, devices, other groups). To put a Gmail user in a group, that user must first exist in your tenant — which means **inviting them as a B2B guest**. Once invited, they're a guest user with their gmail as the UPN/email; you can then add them to a group normally.

That makes **Option B the natural fit**, because B2B guest + group membership is exactly the flow Entra is designed for. Options A and C have to invent a parallel "list of email strings" mechanism (custom security attribute, extension attribute, or separate config blob), defeating the point of using an Entra group.

## 3. Recommended architecture: Option B

**App Service Easy Auth (Microsoft IdP) → tenant-level Google federation → B2B guest users in an Entra security group → Easy Auth enforces "Assignment required" on the group.**

### One-time tenant setup

1. **Add Google as an external identity provider** in Entra:
   `Entra admin center → External Identities → All identity providers → + Google`
   Needs a Google Cloud OAuth client ID + secret.
2. **Create an Entra security group** `videotool-allowed-users`. Type: Security. Membership: Assigned.
3. **Invite each whitelisted Gmail as a B2B guest**:
   `Users → + New user → Invite external user → email = the gmail address`. Invite redemption goes through Google federation automatically.
4. **Add each guest** to `videotool-allowed-users`.

### One-time app setup

1. **Register an Entra app** for videotool (single-tenant).
   Redirect URI: `https://videotool-pritam003-23209.azurewebsites.net/.auth/login/aad/callback`
   Logout URI: `https://videotool-pritam003-23209.azurewebsites.net/.auth/logout`
2. **Enterprise applications → videotool → Properties**: **Assignment required = Yes**.
3. **Enterprise applications → videotool → Users and groups**: assign `videotool-allowed-users`.
4. **App registration → Token configuration**: add optional `groups` claim (Security groups, Group ID).
5. **App Service → Authentication → Add identity provider → Microsoft**:
   - Use the existing app registration above.
   - **Restrict access: Require authentication.**
   - **Unauthenticated requests: HTTP 401** (frontend handles redirect itself).
   - Token store: enabled.

### What the user experiences

1. Hits `/`.
2. Easy Auth redirects to login.microsoftonline.com.
3. Clicks "Sign in with Google" (federated IdP shown automatically for guests).
4. Google authenticates. Entra issues an ID token. App Service sets the auth cookie.
5. Lands back at `/`.
6. Anyone not in the group: Entra returns AADSTS50105 — blocked at the IdP, before any code runs.

### What the server sees

Easy Auth injects request headers (always trusted; sidecar-set, never client-spoofable):
- `X-MS-CLIENT-PRINCIPAL` — base64 JSON with all claims
- `X-MS-CLIENT-PRINCIPAL-NAME` — the gmail address
- `X-MS-CLIENT-PRINCIPAL-ID` — Entra object id (stable per user)
- `X-MS-CLIENT-PRINCIPAL-IDP` — `aad`

We do **not** validate JWTs ourselves.

## 4. Application changes

### 4.1 Backend (`Program.cs`)

~60–80 lines.

1. **Auth middleware** (after Easy Auth's sidecar):
   - Public allowlist: `/health`, `/sw.js`, `/favicon.ico`, `/api/push/vapid-public-key`, `/.auth/*`.
   - Everything else: read `X-MS-CLIENT-PRINCIPAL-ID`. If missing → 401 with `WWW-Authenticate: Bearer`.
   - Defense-in-depth: parse `X-MS-CLIENT-PRINCIPAL`, decode base64 JSON, verify `groups` claim contains `ALLOWED_GROUP_ID`. Reject if absent. Cache decode in `HttpContext.Items`.
2. **`GET /api/me`** returning `{ email, name, objectId }` for the frontend.
3. Tag each `RenderJob` / `PromptCraftJob` with creator's object id; reject `GET /api/jobs/{id}` etc. if requester ≠ owner.
4. Sora job watcher / push watcher unchanged — server-side, no user identity required.

### 4.2 Frontend (`wwwroot/index.html`)

1. On mount, `fetch("/api/me")`. If 401 → redirect to `/.auth/login/aad?post_login_redirect_url=...`.
2. "Signed in as `email` · [Sign out](/.auth/logout?post_logout_redirect_url=/)" header.
3. `jsonOrThrow`: on 401, redirect to login.
4. Optional branded `/login.html` splash — Entra default is fine.

### 4.3 Service worker (`wwwroot/sw.js`)

No changes. Push events have no user identity; server is authoritative.

### 4.4 Web Push subscribe path

Cookie sent automatically (same-origin). Add user object id to `StoredPushSub` so notifications survive logout/login.

## 5. Easy Auth configuration nuance

- "Action when unauthenticated" = **Return HTTP 401** (not redirect). Frontend handles the redirect on first 401. This is the cleaner pattern Microsoft now recommends for SPAs.

## 6. Edge cases

| Edge case | Handling |
|---|---|
| Google session expired mid-render | Sora job continues server-side. On return, frontend gets 401, redirects, federates silently if Google cookie alive. |
| Push notification tap when logged out | `clients.openWindow("/")` → Easy Auth login → `/`. One extra hop. |
| Spoofed `X-MS-CLIENT-PRINCIPAL-*` headers | Impossible — Easy Auth strips inbound copies; only sidecar can set them. |
| User removed from group while session active | Entra cookie up to 24h. Accept the window; for force-logout reduce token lifetime via Conditional Access. |
| F1 cold start + first auth | Easy Auth sidecar adds ~200ms. Acceptable. |
| Local dev (localhost) | Easy Auth doesn't run locally. `DEV_BYPASS_AUTH=1` env var + `IsDevelopment()` injects fake principal. Never read in prod. |
| Health checks from Azure | `/health` anonymous (allowlisted). |
| Service worker registration | `/sw.js` anonymous. |
| Logout | `GET /.auth/logout` clears Easy Auth + Entra session. Doesn't sign out of Google globally (correct). |
| Cost | Easy Auth $0. Entra B2B first 50K MAU free. Google OAuth $0. |

## 7. Security review

- **Token validation**: delegated to Easy Auth.
- **Whitelist**: enforced **twice** — Entra (assignment required + group), and our middleware (groups claim check). Defense-in-depth.
- **OWASP A01**: every API requires principal header; explicit public allowlist; per-job ownership checks.
- **OWASP A02**: token handling in platform.
- **OWASP A07**: SSO via Google; MFA inheritable from user's Google account.
- **CSRF**: `SameSite=Lax` cookies + JSON-only POSTs. Optional `X-Requested-With: fetch` check.
- **Email spoofing**: irrelevant — Entra issues the token, Google federation already verified.

## 8. Rollout

1. Tenant prep (portal, ~15 min): Google IdP, group, app reg, assignment-required, invite test gmails.
2. App Service Authentication setup (portal, ~5 min): enable, point at app reg, "Return 401".
3. Backend middleware + `/api/me` + per-job ownership (~80 LOC).
4. Frontend `/api/me` bootstrap + 401 redirect + signed-in header (~30 LOC).
5. Smoke test.
6. Add remaining gmail addresses.

## 9. Open questions

1. **Group name** — using `videotool-allowed-users` unless overridden.
2. **Per-job ownership** — included in v1.
3. **Branded login splash** — using Entra default initially.
4. **Initial gmail allowlist size** — affects whether to add an admin endpoint later or stay portal-only.
