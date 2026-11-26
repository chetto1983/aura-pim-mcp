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
   4.88, Google APIs 1.75/1.76, OpenTelemetry 1.18, Serilog 10, MSTest 4.3), and three upstream
   has not at all: ReverseMarkdown 6.3.0, AngleSharp.Css 1.3.0 and AngleSharp 1.9.0, all MIT, pinned
   exactly (`[x.y.z]`) because `HiddenText` and the pipeline in `MailBody` lean on their behaviour.
   AngleSharp is named by the fork rather than left to the other two: 1.8.6 is the first with
   `MaximumDepth`, which bounds the parse, and what ReverseMarkdown and AngleSharp.Css ask for is
   lower. Mail discovery (change 12) adds DnsClient 1.8.0 (Apache-2.0) for the MX lookup, which can go on .NET 11
   (`Dns.ResolveMxAsync`), and Microsoft.Extensions.Caching.Memory 10.0.13 for its cache. MailKit 4.18.2 and
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
   `/admin` + `/attachments`, and by the tenant-binding call-tool filter and the attachment
   resource for MCP calls (`resources/read` on `attachment://stash/{id}` binds it too).
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
     request's principal, so the tenant binding binds it exactly as it binds a bearer's `sub`; the
     server refuses to start without it. Both transports register the same surface.

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

4. **Upstream's 29 tools, one per operation, under Aura's names** (`Tools/CalendarMcpSurface.cs`, 2026-11-24).
   Until then a curated `calendar` tool multiplexed them behind an `action` argument (commits of 2026-08-22,
   `38c94fd`); it is gone, with its dispatcher, and nothing takes an `action` argument. The surface is these
   29, each class carrying its name explicitly (`[McpServerTool(Name = ...)]`), never the SDK's derived one:
   `list_accounts`, `get_emails`, `get_email_details`, `search_emails`, `get_email_attachment`,
   `get_contextual_email_summary`, `send_email`, `delete_email`, `move_email`, `mark_email_read`,
   `bulk_delete_emails`, `bulk_move_emails`, `bulk_mark_emails_read`, `get_unsubscribe_info`,
   `unsubscribe_from_email`, `list_calendars`, `get_calendar_events`, `get_calendar_event_details`,
   `create_event`, `update_event`, `delete_event`, `respond_to_event`, `get_contacts`, `search_contacts`,
   `get_contact_details`, `create_contact`, `update_contact`, `delete_contact`, `get_guide`. Two differ from
   what upstream's SDK derives (`..._as_read`): `mark_email_read` and `bulk_mark_emails_read`.

   Both servers register one chain, `WithCalendarMcpSurface()`: the tenant binding (`WithTenantBinding()`),
   the 29 tools, `_meta.ui` (`ui://calendar/view.html`) on the ten reads the view renders, the MCP Apps view
   (`WithCalendarView()`), the `attachment://stash/{id}` resource (`WithEmailAttachmentResource()`),
   upstream's quoted-scalar filter (`WithStringScalarArguments()`), the filter naming a missing required
   argument (`WithNamedMissingArguments()`) and the three prompt classes -- one extension, so a `Program.cs`
   merge conflict cannot silently drop one of them. `_meta.ui` is set where the surface builds each tool,
   because `WithTools<T>()` leaves nowhere to set it. The classes behind `send_email`, `create_event`,
   `create_contact` and `get_calendar_event_details` refuse an empty `subject`, `displayName` or `calendarId`
   themselves (`ToolGuard.RequireNonEmpty`), which the dispatcher used to do.

   **Why it was split** (measured 2026-11-23, Aura's `docs/superpowers/specs/2026-11-23-pim-tool-split-design.md`
   in the Aura repository): the always-loaded slot the one tool was shaped for no longer applies, and its flat
   schema, one `action` enum over every operation's arguments at once, confused the model.

   **Every upstream class the fork modifies** (a sync keeps these hunks; see *Pulling upstream updates*):
   the six event classes (`EventRef`), `SendEmailTool`, `CreateContactTool`, `GetEmailAttachmentTool`, the optional value types
   made nullable (below), the explicit names on all 29, and the deleted `IAccountRegistry.GetAccountsByDomain`.

   - **`send_email` requires `accountId`** (`SendEmailTool.cs`, 2026-11-23). Upstream routes a
     send with no account by the recipient's domain, out of sight of whoever reviews it; told the account
     was optional, Aura's model left it out on every send. The class takes a non-nullable
     `accountId` with no default, which is why the SDK puts it in the schema's `required` list; it is
     the first parameter because C# forbids a required parameter after optional ones. An empty one is
     refused with the McpException the tests pin, and there is no domain routing or first-account
     fallback. `IAccountRegistry.GetAccountsByDomain`,
     whose only caller was that routing, is deleted with its tests and fakes.

   - **`create_event` and `create_contact` require `accountId` too** (`CreateEventTool.cs`, `CreateContactTool.cs`, 2026-11-25).
     Upstream falls back to the first account that permits the write; measured 2026-11-25 on Aura's first
     `create_event`, which left it out and wrote the event to the Microsoft account. The two classes take a
     non-nullable `accountId` with no default (so it is in the schema's `required` list; 4th parameter of
     `create_event`, 2nd of `create_contact`, where C# still allows a required one), a missing, empty or blank
     one is refused by `ToolGuard.RequireAccountId` (the message `send_email` already gave, now shared: name
     `accountId`, call `list_accounts`, ask the user when there is more than one account) before any provider
     is reached, and an unknown account or one without the write permission still fails in
     `ToolGuard.RequireAccountAsync`. `WithNamedMissingArguments()` adds the `list_accounts` hint when the
     missing parameter is `accountId`. The first-account lookup is gone from both classes. The event
     tools that take an `eventRef` (`get_calendar_event_details`, `update_event`, `delete_event`,
     `respond_to_event`) have no `accountId` and are unchanged.
   - **Events are addressed by an opaque reference** (`EventRef.cs`, minted and resolved inside `GetCalendarEventsTool`,
     `CreateEventTool`, `GetCalendarEventDetailsTool`, `UpdateEventTool`, `DeleteEventTool` and `RespondToEventTool`).
     `get_calendar_events` and `create_event` return `eventId = EventRef(accountId, id)`;
     `get_calendar_event_details`, `update_event`, `delete_event` and `respond_to_event` take
     that reference and no `accountId`. Each result echoes the reference, never the provider
     id, and an error quoting the provider id quotes the reference instead.
   - **Required value-type parameters are required by their own schemas.** `isRead` on
     `mark_email_read`/`bulk_mark_emails_read` and `start`/`end` on `create_event` are
     non-nullable with no default, so they are in `required` and a call that leaves one out fails
     binding before the tool runs; nothing invents a value. The SDK alone reports that as "An error
     occurred invoking '<tool>'.", so `WithNamedMissingArguments()` (`Configuration/MissingArgumentReport.cs`,
     upstream's filter from the HTTP `Program.cs`, moved into the surface so stdio answers the same)
     says "Required parameter '<name>' was not provided to '<tool>'.".
   - **Optional value-type parameters are nullable** (2026-11-24; `GetEmailsTool`, `SearchEmailsTool`,
     `GetContactsTool`, `SearchContactsTool`, `GetCalendarEventsTool`, `GetContextualEmailSummaryTool`,
     `CreateEventTool`, and `UpdateEventTool`'s `isAllDay`): every optional number or bool
     (`count`, `unreadOnly`, `includeAttendeeDetails`, `isAllDay` on `create_event`, `countPerAccount`,
     `includeBodyPreview`, `maxSamplesPerCluster`) is `T?` with upstream's default, applied again in
     the body (`count ?? 20`). A model sends `null` for an argument it means to leave out (measured:
     `unreadOnly: null`), and a non-nullable one failed binding. `body`, `bodyFormat` (`send_email`)
     and `method` (`unsubscribe_from_email`) coalesce a `null` the same way.
   - **Attachments come back as a resource link.** `GetEmailAttachmentTool.GetEmailAttachment` always
     stashes and returns a `CallToolResult`: the stash JSON plus a `resource_link` to
     `attachment://stash/<attachmentId>`. The link is served by `EmailAttachmentResource` (tenant from
     the request principal, non-consuming `TryRead`, MIME from the file name when the provider gives
     none or says `application/octet-stream`). The class has no `mode` parameter and no inline path:
     base64 in the result is paid for by the model's context either way, so upstream's inline mode is
     removed, and so is its description's advice to fetch the bytes over HTTP. The per-attachment
     store cap is 25 MiB, fixed; the class takes `IOptions<AttachmentStoreOptions>` so its store-full
     error states the cap and the store's 15-minute TTL instead of upstream's "Try inline mode if the
     file is small." hint.

5. **Health and docs endpoints.** `/health/ready` resolves the account registry rather than
   querying it: accounts are tenant-scoped and an anonymous probe has no tenant.
   OpenAPI/Scalar are mapped in Development only (as upstream).

6. **Model-facing text names what exists here.** Re-authentication messages point at
   `calendar-mcp-cli reauth` or "the client that manages this server's accounts", not at
   upstream's admin UI; `k8s/deployment.yaml` runs `ghcr.io/chetto1983/aura-pim-mcp`.

7. **Three read fixes.** Each corrects what a model reads.
   - `search_emails` on Gmail filters by exact instants (`GmailQuery`, epoch seconds), because a date
     in a Gmail query means midnight Pacific.
   - `get_calendar_events` with no calendar id reads every calendar (`AllCalendars`); naming
     `primary` still reads the default one. A calendar that fails is named in `warnings`
     (`PartialCalendarRead`), and the call fails only when all do. Calendar lists follow their pages
     (`GraphCalendarList`).
   - A mail body is chosen by what its parts mean on Gmail and IMAP: `text/plain` is preferred only inside
     `multipart/alternative`, anywhere else the first part with words in it wins, and a blank part is no
     part. HTML-only mail reaches the model as
     Markdown at the tool edge (`GetEmailDetailsTool` calling `Utilities/MailBody`): `Body` stays
     what the provider holds, and `bodyFormat` says `text` or `markdown`. `MailBody` runs ReverseMarkdown
     after a preprocessing pipeline: `HiddenText` drops what a reader cannot see, with AngleSharp.Css
     computing the cascade, `LayoutTables` flattens tables that only place things, and tracking
     parameters leave links. A Gmail mail's format now comes from the part it was read from.
     `HiddenText` removes what a reader cannot see by the means listed in the plan's *Hidden text*
     table, and the tool says so: other means can get through. Invisible characters go from plain text too.
   - An event's body is read like a mail's, because a stranger's invitation lands on a calendar
     unasked: `GetCalendarEventDetailsTool` calls `MailBody.ForModel`. Google gives a `description`
     no type and says it can hold HTML, so one with a tag in it is `html` (`MailBody.FormatOfUntyped`).
   - `get_contextual_email_summary` converts a body it has. A provider's preview is all it has on a
     list, and nothing here can tell what in it was hidden, so a preview loses its invisible
     characters, is capped, and the tool's `includeBodyPreview` description says it cannot be
     filtered.

8. **The changes contract: `POST /admin/changes` and `POST /admin/items`.** What Aura's mail and
   calendar watch reads, under the same bearer and tenant as `/admin`. It is not an MCP action, so
   the model never sees it, and it reads and changes nothing. The PIM stays stateless: the cursor
   is Aura's. The contract is pinned in Aura's `docs/superpowers/specs/2026-10-31-mail-calendar-watch-design.md`,
   section 5.1, and the behaviour of each provider in the plan `2026-11-01-pim-changes-contract.md`.
   - **`Changes/` holds it all.** `ChangesService` owns what holds for every provider: tenant,
     permission, empty and expired cursors, limits, and naming a failure (`ProviderFailures`, the only
     place that maps a provider's exception). A provider implements `IChangesSource`. A provider with
     no source answers `422 unsupported_resource`, which is CalDAV, ICS and JSON today.
   - **Sources read through seams, not through `IProviderService`.** `IGoogleApiClients` and
     `IGraphApiClients` hand a source an authenticated client; `IGraphApiClients` also hands
     `SendingAddresses` the username a Microsoft account signed in with, read from MSAL's cache without
     calling Graph. `ImapProviderService.Changes.cs` is a partial, because IMAP has one pooled connection
     per account. Each provider gained a small `.Clients.cs`, and `GoogleProviderService`,
     `M365ProviderService` and `OutlookComProviderService` became `partial`.
   - **A failure says nothing about a grant unless it does.** `M365AuthenticationService.GetTokenSilentlyAsync`
     no longer turns every exception into null, and `GoogleProviderService.GetCredentialAsync` no
     longer takes a token endpoint's 5xx for a revoked grant, because `provider_auth` pauses a watch.
   - **An unsigned cursor is untrusted input.** Every field of a position is required by the
     serializer's options, so a damaged one is `invalid_cursor` before a source sees it. A delta link
     passes `GraphLinks.Checked` before it is followed, because `BearerTokenAuthenticationProvider`
     puts the account's token on any host. A cursor is never logged, and neither is a provider's
     exception message, except what an IMAP server itself answered: the verifier's server line (change 9)
     and, when the watch's read fails, `ImapCommandException.ResponseText` or the `ImapProtocolException`
     message, on one line and cut at 200 characters (spec amendment A8).
   - **One mail never blocks a page, and conversion is bounded.** A page is all or nothing and is
     replayed from the same cursor when it fails, so a mail whose body cannot be converted arrives with an
     empty snippet, and `get_email_details` answers its metadata and says the body could not be
     converted, never raw HTML. Every body goes through one cap of 1 MiB, a depth limit, a limit on the
     text CSS variables paste, and a time budget of 500 ms for a snippet and 3 s for the tool; a page
     spends at most 30 s on snippets, then the rest arrive empty. IMAP converts after it releases the
     account's gate.
   - **What a conversion can cost is bounded twice.** First from the mail itself, before the work:
     `MarkupShape` reads the raw text ahead of the parser and refuses what would walk the open elements
     for minutes or recurse in the tokenizer, a parse may allocate 1 KiB per character of the mail with
     a floor of 64 MiB, and `SelectorCost` refuses a stylesheet whose selectors could not be matched
     in time on this mail's real depth and sibling runs. A refusal is `omitted`, with the other fields
     intact. Then at run time, whatever got past that: `ConversionThread` runs every conversion on a
     thread of its own with an 8 MiB stack, and leaves it behind at the budget plus 500 ms. At most two
     run or are left behind at once, past that a conversion is refused at once, and a warning with
     no mail content says so. `ConversionThreadLogging`, a hosted service, gives it its logger.
     The plan's *Bounds on a conversion* says what these two do not prove.
   - **A body nested past what is walked says so.** `MailParts.NoBody`: reading as empty would let a
     stranger make a mail look as if it had nothing to say.
   - **`writtenTo` does not guess.** A NO on Sent reads as gone only if a fresh LIST agrees, and is
     otherwise a failed page, because the cursor would pass those mails. Past `MaxSentCandidates`
     hits with no exact match the answer is null.
   - **Forged input is a 400.** A cursor or item that can never work answers `invalid_cursor` or
     `invalid_items` before a server is asked; a cursor for another `inboxFolder` is expired instead.
     A stored password that cannot be decrypted is `provider_auth`.
   - **Tests reach real servers.** GreenMail and Dovecot run in CI (`.github/workflows/ci.yml`,
     `PIM_TEST_GREENMAIL`, `PIM_TEST_DOVECOT`); under `CI` an unset variable fails the test.

9. **Checking an IMAP account (`Providers/ImapAccountVerifier.cs`).** `IImapAccountVerifier` answers whether a
   mailbox works the way Aura's watch reads it, for the connect and repair flows of Aura's
   `docs/superpowers/specs/2026-11-20-pim-account-connect-repair-design.md`. Upstream has nothing like it.
   - **Two steps, two budgets.** IMAP (12 s): connect, log in, and the watch's own first read of the configured
     `inboxFolder` (`FirstReadAsync`, which `StartAsync` calls too). SMTP (8 s): connect and log in. An IMAP failure
     blocks; an SMTP password refusal blocks (`smtp_auth_failed`, also when the server offers no AUTH at all); an
     SMTP server that cannot be reached or refuses TLS is a warning, because the watch only reads. `VerifyReasons`
     is the one place an exception becomes a reason. A cancelled caller is not a verdict; a step out of time is
     `unreachable` (`smtp_unreachable` for SMTP) whatever MailKit threw when it was cut short. A password SCRAM's
     SASLprep refuses (a control or prohibited character, such as a pasted newline: MailKit throws `ArgumentException`)
     is `auth_failed`/`smtp_auth_failed`, because the person must retype it.
   - **Its input is checked before it runs.** `VerifyAsync` is called only with a username, a password, host names
     a resolver takes (at most 253 characters, a DNS name or an IP address), ports in 1-65535 and a known security
     value (`Admin/ImapProviderInput`, change 11); `ImapAccountConfig.From` reads a port
     outside 1-65535 as absent.
   - **TLS modes.** Provider-config keys `imapSecurity` and `smtpSecurity`: `ssl` is `SslOnConnect`, `starttls`
     is `StartTls` (required, never "when available"), `none` exists for the plaintext test servers. An account
     without the key was stored before it existed and keeps MailKit's `Auto`. `ImapConnections` opens every
     IMAP and SMTP connection, the provider's included.
   - **Private addresses.** `HostGuard` resolves the host once, refuses loopback, RFC 1918, link-local, CGNAT,
     192.0.0.0/24, 198.18.0.0/15, multicast and reserved 224.0.0.0/3, ULA, the unspecified addresses,
     IPv4-compatible `::/96`, and the IPv4-mapped, NAT64 (`64:ff9b::/96`) and 6to4 (`2002::/16`) forms of a refused
     IPv4 address (IANA's special-purpose registries, read 2026-11-23) unless the caller allows them, and hands MailKit the
     socket it connected to a checked address (`ConnectAsync(Socket, host, ...)`, where the host still names the
     server for the certificate check).
   - **The server's line is logged.** A failure logs the account id, step, stage, the exception's type and the
     server's response line (`ImapCommandException.ResponseText`, an SMTP status and its text, or an
     `AuthenticationException`'s message), CR/LF removed and cut at 200 characters; never the exception itself.
     A refused private address has no server line: the log names the host and the address it resolved to.
     `ImapAccountConfig` prints without its password.
   - **Its tests reach real servers too:** GreenMail's SMTP (`PIM_TEST_GREENMAIL_SMTP`) and a second GreenMail
     that checks passwords (`PIM_TEST_GREENMAIL_AUTH_SMTP`), both started by `ci.yml`.

10. **Account passwords are stored encrypted, and the account file is written atomically.** This restores
    upstream's own admin behaviour (its form protected the IMAP password on save,
    `BlazorAdmin/AccountFormModels.cs:216-220` at upstream `3b2762b`), which went with the Blazor UI.
    - **On every save.** `CreateAccount`, `UpdateAccount` and the provider-config PATCH (change 11) store what the
      request sent through `ProviderSecrets.ProtectIncoming`: a non-empty `password` (any case) is always encrypted,
      `ENC:` prefix or not, because a caller can type the prefix. Only a value the PATCH keeps from storage is left
      as it is.
    - **At startup.** `ProtectStoredPasswordsService` runs `ProtectStoredPasswordsAsync` once: it walks the file as
      a JSON tree (never `ParseAccounts`, which throws on a node without `TenantId`), encrypts the plaintext
      passwords of every tenant, writes only when it changed something, logs the count and never a value. A failure
      is logged once and does not stop the host.
    - **`WriteConfigAsync` is atomic.** It writes `appsettings.json.tmp` beside the file, flushed to the disk
      (`Flush(true)`: a power cut must not leave a rename pointing at data that never landed), and moves it over the
      original; a failed move deletes the temp file and leaves the original whole. A temp file that cannot be deleted
      is logged, and the caller hears the write's own failure.
    - **The key ring** is `/app/data/keys` (`ConfigurationPaths.GetDataProtectionKeysDirectory()`), on Aura's
      `aura-pim-data` volume with `appsettings.json`. **Never delete `keys/`**: every `ENC:` value becomes
      unreadable and each account needs its password entered again.
    - **What `ENC:` protects against** is the account file read on its own: a backup, a copy of the volume without
      `keys/`, a log or a support bundle. It does not protect against anyone who can read the volume with `keys/` or
      run code in the container (spec A10 of Aura's account connect/repair design).

11. **An IMAP account is checked when it is saved, can be checked on demand, and can be changed in part
    (`Admin/AccountRepairEndpoints.cs`, `Admin/ImapProviderInput.cs`).** The connect and repair flows of Aura's
    account connect/repair design (amendments A2-A5). `AddAdminServices()` registers what `/admin` needs, for
    `Program.cs` and the tests alike.
    - **Create.** `POST /admin/accounts` answers `409` for an existing id before anything else. For `imap` it then
      refuses keys that differ only in case (`duplicate_key`), a host a resolver would not take (`invalid_host`), a
      port outside 1-65535 (`invalid_port`), `none`
      (`tls_required`) and any other security value but `ssl`/`starttls` (`invalid_security`); an absent
      `imapSecurity`/`smtpSecurity` is taken from the port (993/465 `ssl`, otherwise `starttls`) and stored. The
      check runs on the config as it will be stored, password already encrypted, so a typed `ENC:` password is read
      as typed. A blocking failure is `422 {"error":"verify_failed","step","reason"}` and nothing is stored; a sending
      server that cannot be reached or refuses TLS is stored with `smtpWarning` in the `201`.
    - **Private hosts.** The check on create, and on a PATCH that changes the servers, connects to a private address
      only when the request carries `X-Aura-Allow-Private-Hosts: true`, which Aura sets for an identity holding `identity.create` only.
      That holds for the sending server too: a private SMTP host without the header is `422
      {"step":"smtp","reason":"smtp_unreachable"}` and nothing is stored, because the warning is for a server that
      cannot be reached, not for one this caller may not reach.
    - **The account remembers the permission, and every connection is held to it** (Aura ledger Ruling 25,
      2026-11-23). It follows whoever last chose the servers (Ruling 28). Create, and a PATCH that changes any of
      `imapHost`, `imapPort`, `imapSecurity`, `smtpHost`, `smtpPort`, `smtpSecurity` (a value in another case is no
      change), store `providerConfig.allowPrivateHosts`, `"true"` only when that request carried the header and
      `"false"` otherwise, so a member's server change sets `false` even on an account an administrator made. Any
      other PATCH, a password change among them, is checked with the stored permission and leaves the key as it was,
      absent included: an administrator's password change cannot grant a member's account a private host, and a member
      can repair the password of an administrator's office-server account. The key sits in the provider config, beside
      `imapSecurity`, where `ImapAccountConfig.From` reads it at every connection. Only the server writes it: a create
      or PATCH whose body names it, in any case and with any value, is `400 {"error":"server_owned_key"}`, the way
      `none` is `tls_required` and not silently upgraded. Every connection for a stored account, the watch's and
      the pooled IMAP session and the SMTP of `send_email` alike, is a socket `HostGuard` opened with that
      permission (`ImapConnections` has no other way to connect): the host is resolved once and the checked address
      is the one connected, so a name that resolved publicly, or not at all, when it was saved and points at a
      private address later reaches nothing and sends no password. Such a refusal reads as `provider_transient` for
      the watch (nothing was connected, so a retry costs no login and a name that turns public again reads again) and
      as a non-retryable tool error that does not name the address. The pool reopens a session when the permission
      changes, so an administrator's session does not outlive a member's PATCH.
    - **An account stored before the key has none and counts as allowed.** Until then only the operator could create
      an account (Aura's Governance was administrator-only), and an office mail server on a private address is real.
      Outside the API an IMAP account comes only from editing the account file by hand, and `PUT` stores the provider
      config it is given and adds no key: both are the operator's, and what they store without the key counts as
      allowed too. A key that is there but says anything other than `true` is not allowed.
    - **One source of the permission.** `ImapAccountConfig.AllowPrivateHosts` has no default, and the check and every
      connection read it from the config they are handed (`ImapProviderService.AllowsPrivateHosts`): create and a
      server-changing PATCH write it first, any other PATCH and `verify` hand over the stored one. A guarded connection is bounded by the MailKit client's
      `Timeout` and answers a `TimeoutException`, as MailKit's own connect does.
    - **`POST /admin/accounts/{id}/verify`** checks the account as stored: `200 {"ok":true,"smtpWarning"?}`, `422`,
      `404`, `429`, or `400 not_verifiable` for an account that is not IMAP or whose stored config the check cannot
      run on. A stored `none` is checked as it is: only the operator can have written it, and the watch reads with it.
      For the same reason it is held to the account's stored `allowPrivateHosts` (absent is allowed), not to the
      caller's header (Aura ledger Ruling 27, 2026-11-23): an administrator checking a member's account whose host
      now resolves private gets the refusal its watch gets, and a member checking an administrator's account on an
      office server passes, as its watch reads it. Create and a server-changing PATCH use the header, because the
      header is what they store.
    - **`PATCH /admin/accounts/{id}/provider-config`** with `{"providerConfig":{…}}`, IMAP only (`not_patchable`):
      a key absent or `""` keeps what is stored, a password is never trimmed, a different `username` is
      `username_immutable` (another mailbox is another account), and a changed `imapHost`, `imapPort`, `smtpHost` or
      `smtpPort` without a password is `password_required`, so the stored password never goes to a server it was not
      given to. The merged config passes the create rules (so an account stored with `none` answers `tls_required`
      until the PATCH names `ssl` or `starttls`), is checked, and is written by
      `MergeProviderConfigAsync`, which replaces only `ProviderConfig` and answers `409 {"error":"changed"}` if the
      stored config moved while it was checked. `200` is `PUT`'s body plus `smtpWarning`. `PUT` is not checked.
    - **`PUT /admin/accounts/{id}` is not guarded.** `UpdateAccount` (`Admin/AdminEndpoints.cs`) runs no
      `ForWrite`, no `HostGuard` and no verification: it can store any host, private ones included. It is
      operator-only, and Aura must never forward it.
    - **One check per account every 30 s** (`VerifyThrottle`), on create, verify and PATCH alike:
      `429 {"error":"too_many_attempts"}` without connecting, because each attempt is logins a provider counts.
    - **The pool follows a change.** `ImapProviderService` remembers the host, port, security, username, password
      and private-hosts permission each pooled session was opened with, and reopens it when the registry says
      otherwise; its keys match account ids in any case.

12. **Finding a mailbox's servers from its address (`Discovery/`, `POST /admin/discover`).** Aura's connect screen
    asks before it asks for a password (account connect/repair design, A6 and A7). Upstream has nothing like it.
    - **The route.** `{"email":"…"}` answers `200 {kind, source?, imapHost?, imapPort?, imapSecurity?, smtpHost?,
      smtpPort?, smtpSecurity?, username?, appPassword?: {required, helpUrl?}, passwordFallback?}`, camelCase with
      nulls left out, or `400 {"error":"invalid_email"}`. The address is parsed by MimeKit, lowercased, its domain
      IDN-mapped to ASCII and required to be a DNS name with a dot; a `+tag` is dropped from the username. It reads
      nothing of the tenant's and stores nothing.
    - **Thunderbird's order** (`FindConfig.sys.mjs`/`FetchConfig.sys.mjs`), first hit wins: the preset table
      (`Discovery/presets.json`, embedded; every entry cites the provider page its values were copied from); the
      domain's own autoconfig (`https://autoconfig.<d>/mail/config-v1.1.xml`, then `.well-known`); ISPDB; the MX of
      lowest preference. An MX under `google.com`/`googlemail.com` is Google, under `olc.protection.outlook.com`
      Outlook.com, under any other `outlook.com` Microsoft 365; an MX inside the address's own domain ends at
      `unknown`; otherwise its suffixes, from the next subdomain down to two labels, go through preset, autoconfig
      and ISPDB again. Then `unknown`.
    - **A usable answer** has an IMAP server with a DNS hostname (after `%EMAILADDRESS%`, `%EMAILLOCALPART%`,
      `%EMAILDOMAIN%`), a port in 1-65535, `SSL` or `STARTTLS`, and a password login. OAuth-only and plain answers
      are a miss. The XML is parsed with DTDs prohibited and no resolver.
    - **What leaves the box** is the domain or a suffix of the MX host, nothing else: HTTPS only, no `emailaddress`
      parameter, no redirect followed, no proxy, a 64 KiB body cap, and every connection through `HostGuard` with
      private addresses refused.
    - **Budgets and cache.** 4 s per remote step and 12 s overall, on an injected `TimeProvider`. A step out of time,
      or whose source could not be asked (a 5xx, a refused or broken connection), falls through and is not cached.
      A miss is a status below 500 other than 200, a body that is not a `clientConfig`, a host that does not
      resolve, or a private address; for the domain's own autoconfig, a page that is not a config falls through to
      `.well-known`. DnsClient runs with a 2 s timeout and one retry. The autoconfig and ISPDB XML is cached (hits
      24 h, misses 1 h) in the container's `IMemoryCache`, which `AddCalendarMcpCore()` registers with `SizeLimit`
      1000: anything else that caches there must give its entries a `Size`.
    - **The password fallback is Google's only.** Outlook.com refuses basic authentication, so its preset carries
      `passwordFallback: false` and no servers.

13. **A one-click unsubscribe reaches only a public address, and answers only its status** (`Utilities/
    UnsubscribeExecutor.cs`, 2026-11-23). Upstream POSTs to a mail's `List-Unsubscribe` URL with a plain client that
    follows redirects, and returns the far side's body or the exception's text: whoever can mail the inbox could
    make the PIM read its own network. The `Unsubscribe` client now has `HostGuard.CreatePublicHttpHandler()`, the
    handler discovery uses (no private address, no redirect, no proxy), and a refused or failed POST answers
    `Unsubscribe request returned HTTP <status>` or `Failed to execute one-click unsubscribe` with no
    `ErrorDetails`. The mailto path gives `ProviderErrorFormatter`'s summary, as `send_email` does, not the
    exception's text. Stored ICS/DAV/JSON URLs are still fetched unguarded.

14. **The Sent, Trash and Junk folders are the ones the server has** (`Providers/ImapProviderService.SpecialFolders.cs`,
    2026-11-23). Upstream defaults them to Gmail's names (`[Gmail]/Sent Mail`, `[Gmail]/Trash`, `[Gmail]/Spam`) and
    only logs a failed APPEND, so on any other host a sent mail kept no copy and a delete failed: on Aruba
    (`INBOX.Sent`, `INBOX.Trash`) both sends of 2026-11-23 lost their copy and the cleanup's delete failed, and
    Aruba files nothing itself. Unset, each is now found on the server: the folder marked `\Sent`, `\Trash` or
    `\Junk` (SPECIAL-USE, or Gmail's XLIST), else one whose own name is a common name for the role, compared by
    its last segment so `INBOX.Sent` matches. A setting that names nothing on the server falls back to the same
    lookup. The send's APPEND, `delete_email`, the watch's written-to and the `sentitems`/`trash`/`spam` folder
    aliases share it; a delete with no Trash anywhere is refused and the mail stays. Tested on Dovecot
    (SPECIAL-USE) and GreenMail (none, `.` separator).

## Build / run (sidecar)

```bash
docker build -t calendar-mcp .
docker run -d -p 127.0.0.1:8093:8080 -v calendar-mcp-data:/app/data \
  -e CALENDAR_MCP_OAuth__Issuer=https://auth.example \
  -e CALENDAR_MCP_OAuth__MetadataAddress=https://auth.example/.well-known/oauth-authorization-server \
  -e CALENDAR_MCP_OAuth__Resource=https://calendar.example/ calendar-mcp
```
MCP endpoint `/`, admin REST `/admin`, health `/health` (internal port 8080).

The runtime stage runs `apt-get upgrade`, because the `aspnet:10.0` Ubuntu base trails its own
security fixes (apt 2.8.3 on 2026-11-09, CVE-2026-109518). `aura-publish-image.yml` passes the ISO
week as `APT_WEEK`, so the cached layer is rebuilt once a week. A local `docker build` without the
argument upgrades on its first build and then reuses the cache.

## Pulling upstream updates

```bash
git fetch upstream && git merge upstream/main
```

Resolution rules, in the order the 1.8.3 and 1.9.0 syncs needed them:

- `Components/`, `BlazorAdmin/`, admin-console security, API-key auth: keep deleted.
- `*.csproj`: keep the fork's package versions; add only packages the kept code needs.
- A tool class upstream changed: take upstream's version, keeping the fork's `[McpServerTool(Name = ...)]` (all 29 name themselves; `mark_email_read` and `bulk_mark_emails_read` are the two that differ from upstream's `_as_read`), its short description and its nullable optional value types. Exception: the six event classes (`GetCalendarEventsTool`, `CreateEventTool`, `GetCalendarEventDetailsTool`, `UpdateEventTool`, `DeleteEventTool`, `RespondToEventTool`) -- merge upstream's change but keep the fork's EventRef hunks; and `SendEmailTool` (required `accountId`, no routing), `CreateEventTool` and `CreateContactTool` (required `accountId`, no first-account fallback) and `GetEmailAttachmentTool` (always stashes, returns the `attachment://` link, states the store limits) -- merge upstream's change but keep those hunks; the class tests pin them. `IAccountRegistry.GetAccountsByDomain` stays deleted.
  A tool class upstream adds goes into `CalendarMcpSurface`'s list, under Aura's name (change 4).
- Tests upstream adds for a tool class: keep them. They exercise the tool the fork registers.
- `AccountValidation` stays upstream's; the tenant rules live in `TenantProviderConfig`.
- `Program.cs` (both servers): the fork's auth pipeline and the `.WithCalendarMcpSurface()`
  call must survive -- it is the tools, the view, the attachment resource and the prompts in
  one line, so a conflict resolved by re-adding upstream's `WithTools<...>()` chain would
  silently drop the rest. Take upstream's endpoint hardening. Check that non-conflicting
  hunks did not pull in registrations for dropped types or tool classes (the tests call the
  extension, not `Program.cs`, so only the stdio smoke would see a tool registered twice), and that
  `MapAttachmentEndpoints()` survived too.
- Model-facing messages upstream adds that mention its admin UI: reword (change 6). CLI output
  too: "the client that manages this server's accounts".
- A registration upstream adds to its `Program.cs` tool chain goes into
  `WithCalendarMcpSurface()`, not into `Program.cs` (1.9.0: `WithStringScalarArguments()`; the
  missing-parameter filter: `WithNamedMissingArguments()`).
- An optional value-type parameter upstream adds to a tool: make it `T?` with its default and apply the
  default in the body (change 4). `NullOptionalArgumentTests` fails on one that is not.
- A CLI command upstream adds: `ExecuteAsync(..., CancellationToken)` (the fork's Spectre.Console.Cli),
  and account ids and `TenantId` through `CliTenant`, like the other `add-*` commands.
- Tests upstream adds for `Components/` or `BlazorAdmin/`: drop them with the code they test.
- Tests on MSTest 3 asserts: `ThrowsException[Async]` becomes `ThrowsExactly[Async]` (same
  exact-type check), `[DataTestMethod]` becomes `[TestMethod]`.
- `ImapProviderService` is split into partials (`.cs`, `.Folders.cs`, `.Support.cs`) to stay
  under 600 lines; upstream edits to the tail of their single file land in `.Support.cs` by hand.
  `.Changes.cs` is the fork's own and has no upstream counterpart.
- A provider or `Utilities` file upstream changes near a fork edit: keep these on any conflict,
  because they look like small edits and carry the contract. `GoogleProviderService.cs`:
  `partial`, `GetMessageBody` preferring `text/plain` and answering the format, `GetBodyFromParts`
  skipping attachments and forwarded messages, `MailParts.NoBody`, the `TokenResponseException`
  filter, `Transport`, `Authorize`, `FormatOfUntyped` on an event's description.
  `M365ProviderService.cs` and `OutlookComProviderService.cs`: `partial`, `Transport`,
  `CreateGraphClient`, and `MsalClientAsync`, the one place that reads an account's tenant and client, for the
  token and for `SignedInUsernameAsync` in `.Clients.cs`, so both reach the same MSAL application.
  `M365AuthenticationService.cs`: no catch-all returning null (`NullWhenGrantIsGoneAsync`), and
  `SignedInUsernameAsync` (also on `IM365AuthenticationService`), which `SendingAddresses` reads a Microsoft
  account's sending address from instead of Graph's `/me`, which needs a User.Read scope the accounts do not have.
  `ImapProviderService.Support.cs`: `ExtractBody` with text first, `MailParts.NoBody`.
  `GetEmailDetailsTool.cs` and `GetCalendarEventDetailsTool.cs`: `MailBody.ForModel`.
  `GetContextualEmailSummaryTool.cs`: `PlainTextOf`, which is the only way a body or a preview reaches
  its summary, and the sentence in `includeBodyPreview`'s description. `ServiceCollectionExtensions.cs`: `ConversionThreadLogging`, and the mail discovery registrations (change 12): the
  memory cache, `ILookupClient`, `IMxLookup`, the `Autoconfig` client with its guarded handler, and `MailDiscovery`;
  and the `Unsubscribe` client's guarded handler (change 13), which `UnsubscribeExecutor.cs` relies on: an upstream
  edit there that reads the response body or returns `ex.Message` reopens it.
- An upgrade of AngleSharp or AngleSharp.Css, both pinned, reopens `SelectorCost`, `MarkupShape`, the
  allocation allowance and every stack figure in `MailBody` and `HiddenText`: they are measurements of
  that engine's code. A new pseudo-class reads as infinite cost in `SelectorCost` until it is added, with
  or without brackets: a bare name is cheap only if it is on the list in `PseudoClass`.
- `ImapConnections.cs` (the fork's own; upstream opens connections in `ImapProviderService.cs`, and an upstream
  edit to its `OpenImapAsync`/`OpenSmtpAsync` lands here by hand): the IMAP client is made with
  `ImapNoCodes.NewClient()` in place of `new ImapClient()`, and the security option comes from `imapSecurity`/
  `smtpSecurity` (change 9) instead of `Auto`. The logger reads `NO [NOPERM]` and `NO [CANNOT]`, which MailKit
  takes out of its exceptions.
- A new provider upstream adds answers `422` until it has an `IChangesSource`: a new family goes in
  `ChangesProviders`, and `ChangesRegistrationTests` lists every source.
- Upstream's `IProviderServices.cs`: the fork's provider interfaces also inherit
  `IGoogleApiClients`, `IGraphApiClients` and `IChangesSource`.
- Tests from upstream run against a bound tenant: configs need a `TenantId`, account ids use
  `TenantIdentity.AccountId(...)`.
