# Aura fork of calendar-mcp → `aura-pim-mcp`

Thin fork of [`MarimerLLC/calendar-mcp`](https://github.com/MarimerLLC/calendar-mcp), tracking
upstream. Adapts the server into Aura's unified mail + calendar + contacts **HTTP sidecar**.
Design: Aura repo `docs/superpowers/specs/2026-06-16-calendar-pim-mcp-fork-design.md`.

Branch: `main`. Remotes: `origin` = this fork, `upstream` = MarimerLLC/calendar-mcp.
Last upstream sync: **1.9.0** (2026-10-23).

This file is the authority on where the fork differs. Upstream's own documents (`README.md`,
`docs/`) are kept close to upstream so merges stay cheap; where they describe something this
file says the fork does not have, this file wins.

## Changes vs upstream

1. **Dependencies ahead of upstream.** The fork carries newer packages than upstream 1.9.0
   (Microsoft.Graph 6.5, Kiota.Abstractions 2.0 -- which also closes GHSA-7j59-v9qr-6fq9 -- MSAL
   4.88, Google APIs 1.75/1.76, OpenTelemetry 1.18, Serilog 10, MSTest 4.3). MailKit 4.18.2 and
   Microsoft.Extensions.* / DataProtection 10.0.13 bring System.Security.Cryptography.Pkcs and
   .Xml to 10.0.13, closing GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7, GHSA-r955-8q5j-3h83,
   GHSA-7hqp-7c5m-4vq6 and GHSA-c399-9853-6jmj. The HTTP server takes Xml from the ASP.NET
   shared framework instead, so the image is fixed by the `aspnet:10.0` runtime it is built on,
   not by this file. On a sync, keep the fork's versions unless upstream's are newer.

2. **Authentication is Aura's OAuth, not upstream's API key or admin console.** Every MCP
   (`/`), attachment (`/attachments`) and admin (`/admin`) request carries an OAuth bearer
   issued by Aura: a JWT validated against the configured issuers, with an audience naming any
   name the server is reachable under and a `scope` claim that includes `OAuth:ToolsScope`
   (default `mcp:tools`). The scope is one check (`Security/McpToolsScope`), applied as the
   `McpTools` authorization policy on `/` and inline by `AdminAuthMiddleware` on `/admin` +
   `/attachments`. The bearer's subject is the **tenant**: accounts, the attachment store and
   the admin API are all scoped to it (`ITenantContext`), bound by `AdminAuthMiddleware` for
   `/admin` + `/attachments`, and by the curated tool and the attachment resource for MCP
   calls (`resources/read` on `attachment://stash/{id}` binds it too).
   Upstream's API-key scheme, admin OIDC sign-in, admin users/claim codes, rate limiting and
   `CalendarMcp:Mcp:RequireApiKey` are **not** taken (1.8.3 sync dropped
   `Security/McpApiKeyAuthentication`, `FileMcpKeyStore`, `AdminUserStore`,
   `AdminClaimCodeService`, `AdminOidc`, `AdminAuthConfiguration` and their tests).

   - **A tenant cannot make the server read its own disk.** Accounts written through `/admin`
     pass `Admin/TenantProviderConfig`: upstream's per-provider rules, plus a refusal of a JSON
     account whose `source` is not `onedrive` or that names `filePath`, `emailsFilePath` or
     `contactsFilePath` in any casing. Local JSON files remain for accounts the operator writes
     into the configuration.
   - **A tenant's DAV account reaches the HTTPS host it names.** `dav`/`caldav`/`carddav`
     (upstream 1.9.0) is accepted through `/admin` with upstream's rules: HTTPS only, credentials
     sent to the preset's hosts or to the configured entry host. That is the posture IMAP already
     has (a tenant names its own IMAP/SMTP hosts); nothing here restricts the target network.
   - **The stdio server serves one local tenant**, named by `CALENDAR_MCP_TENANT_ID` (the
     variable the CLI adds accounts under). An incoming message filter sets it as every
     request's principal, so the tool binds it exactly as it binds a bearer's `sub`; the
     server refuses to start without it. Both transports register the same curated surface.

3. **No Blazor admin UI.** Aura's cockpit drives connect/account management through the
   `/admin` REST API. `Components/` and `BlazorAdmin/` are deleted; upstream changes to them are
   resolved as deletions on every sync.

   - **Headless Google OAuth.** `StartGoogleOAuth` returns `{authUrl, redirectUri}` JSON instead
     of a 302; the callback renders a self-contained HTML result page; the cockpit polls
     `/admin/accounts/{id}/status` (which also reports `linked` for Google). Only
     `/admin/auth/google/callback` is exempt from the bearer.
   - **Google redirects through a shared relay** (gitlab.com/Chetto1983/aura-connect, on
     Cloudflare Pages). The
     registered redirect URI is one fixed page, identical for every install; `state` is
     `<nonce>.<base64url(install callback)>` and the page forwards the browser to that callback.
     `StartGoogleOAuth` takes an optional `returnBase` (winning over `ExternalBaseUrl`) and
     returns the relay URI as `redirectUri`. The exchange replays the redirect URI and a PKCE
     (S256) verifier stored with the issued state, and refuses any state this server did not
     issue. Measured 2026-09-23: with a Desktop OAuth client the loopback redirect only reaches a
     listener on the browser's own machine, so a server-side install cannot receive it.

4. **One curated tool instead of 29.** Both servers register `WithCalendarMcpSurface()`: the
   curated `calendar` tool (`action` discriminator over all 29 upstream operations), the MCP
   Apps view (`WithCalendarView()`), the `attachment://stash/{id}` resource
   (`WithEmailAttachmentResource()`), upstream's quoted-scalar filter
   (`WithStringScalarArguments()`, which reads the curated schema) and the three prompt
   classes -- one extension so a
   `Program.cs` merge conflict cannot silently drop one of them. Every action **forwards** to
   the upstream implementation class, constructed per call through `ActivatorUtilities` (no
   tool class is registered in DI), so upstream fixes reach it without a copy.

   - **Events are addressed by an opaque reference** (`CalendarActionTool.Calendar.cs`).
     `get_calendar_events` and `create_event` return `eventId = EventRef(accountId, id)`;
     `get_calendar_event_details`, `update_event`, `delete_event` and `respond_to_event` take
     that reference and no `accountId`. Each result echoes the reference, never the provider
     id, and an upstream error quoting the provider id quotes the reference instead. The
     rewrite fails loudly if upstream renames an id field or starts emitting its own `eventId`.
   - **Required value-type parameters are checked by the forwarder.** Every parameter of the
     multiplexed schema is optional, so where upstream declares a non-nullable value type
     (`isRead` on `mark_email_read`/`bulk_mark_emails_read`, `start`/`end` on `create_event`)
     the forwarder rejects its absence instead of inventing a value. Required strings, lists
     and arrays pass through: upstream already rejects them when missing.
   - **Attachments come back as a resource link.** `get_email_attachment` always stashes and
     returns the stash JSON plus a `resource_link` to `attachment://stash/<attachmentId>`,
     served by `EmailAttachmentResource` (tenant from the request principal, non-consuming
     `TryRead`, MIME from the file name when the provider gives none or says
     `application/octet-stream`). The curated schema has no `mode`; upstream's tool class
     keeps its inline mode, unreachable here. The per-attachment store cap is 25 MiB, fixed;
     the curated forwarder rewrites upstream's store-full error with the cap and the store's
     15-minute TTL instead of upstream's "Try inline mode if the file is small." hint.

5. **Health and docs endpoints.** `/health/ready` resolves the account registry rather than
   querying it: accounts are tenant-scoped and an anonymous probe has no tenant.
   OpenAPI/Scalar are mapped in Development only (as upstream).

6. **Model-facing text names what exists here.** Re-authentication messages point at
   `calendar-mcp-cli reauth` or "the client that manages this server's accounts", not at
   upstream's admin UI; `k8s/deployment.yaml` runs `ghcr.io/chetto1983/aura-pim-mcp`.

## Build / run (sidecar)

```bash
docker build -t calendar-mcp .
docker run -d -p 127.0.0.1:8093:8080 -v calendar-mcp-data:/app/data \
  -e CALENDAR_MCP_OAuth__Issuer=https://auth.example \
  -e CALENDAR_MCP_OAuth__MetadataAddress=https://auth.example/.well-known/oauth-authorization-server \
  -e CALENDAR_MCP_OAuth__Resource=https://calendar.example/ calendar-mcp
```
MCP endpoint `/`, admin REST `/admin`, health `/health` (internal port 8080).

## Pulling upstream updates

```bash
git fetch upstream && git merge upstream/main
```

Resolution rules, in the order the 1.8.3 and 1.9.0 syncs needed them:

- `Components/`, `BlazorAdmin/`, admin-console security, API-key auth: keep deleted.
- `*.csproj`: keep the fork's package versions; add only packages the kept code needs.
- A tool class upstream changed: take upstream's version. The curated tool forwards to it.
  If it added a non-nullable value-type parameter, add the forwarder check (change 4).
- Tests upstream adds for a tool class: keep them. They exercise the code the fork forwards to.
- `AccountValidation` stays upstream's; the tenant rules live in `TenantProviderConfig`.
- `Program.cs` (both servers): the fork's auth pipeline and the `.WithCalendarMcpSurface()`
  call must survive -- it is the tool, the view, the attachment resource and the prompts in
  one line, so a conflict resolved by re-adding only `WithCalendarActionTool()` would
  silently drop the rest. Take upstream's endpoint hardening. Check that non-conflicting
  hunks did not pull in registrations for dropped types or tool classes (the tests call the
  extension, not `Program.cs`, so only the stdio smoke would see a second tool), and that
  `MapAttachmentEndpoints()` survived too.
- Model-facing messages upstream adds that mention its admin UI: reword (change 6). CLI output
  too: "the client that manages this server's accounts".
- A registration upstream adds to its `Program.cs` tool chain goes into
  `WithCalendarMcpSurface()`, not into `Program.cs` (1.9.0: `WithStringScalarArguments()`).
- A CLI command upstream adds: `ExecuteAsync(..., CancellationToken)` (the fork's Spectre.Console.Cli),
  and account ids and `TenantId` through `CliTenant`, like the other `add-*` commands.
- Tests upstream adds for `Components/` or `BlazorAdmin/`: drop them with the code they test.
- Tests on MSTest 3 asserts: `ThrowsException[Async]` becomes `ThrowsExactly[Async]` (same
  exact-type check), `[DataTestMethod]` becomes `[TestMethod]`.
- `ImapProviderService` is split into partials (`.cs`, `.Folders.cs`, `.Support.cs`) to stay
  under 600 lines; upstream edits to the tail of their single file land in `.Support.cs` by hand.
- Tests from upstream run against a bound tenant: configs need a `TenantId`, account ids use
  `TenantIdentity.AccountId(...)`.
