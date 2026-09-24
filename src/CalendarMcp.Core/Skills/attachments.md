# Attachments

Attachments are the most non-obvious part of Adjutant because the
bytes generally **do not transit through the agent**. Two pathways
exist; pick by direction.

## Outbound (sending a file)

`send_email` accepts `attachments[]` where each item has one of:

- **`{ "attachmentId": "abc..." }`** — preferred. The bytes were
  previously uploaded to the server's attachment store. Single-use:
  consumed when `send_email` succeeds.
- **`{ "name": "x.pdf", "base64Content": "...", "contentType": "..." }`** —
  the agent encodes the file inline. Use only for very small files; the
  total decoded payload per message is capped at **25 MB** by this
  server, and most providers (M365/Outlook.com) cap each individual
  attachment at **3 MB**.

### Getting an `attachmentId` (HTTP server only)

On the HTTP transport, the file is uploaded out-of-band:

```http
POST /attachments              ← multipart/form-data with the file
→ 201 Created
  { "attachmentId": "abc...", "name": "...", "contentType": "...",
    "size": 123456, "expiresAt": "..." }
```

Then in the tool call:

```json
{
  "tool": "send_email",
  "arguments": {
    "to": ["alice@example.com"],
    "subject": "Q4 report",
    "body": "<p>See attached.</p>",
    "attachments": [{ "attachmentId": "abc..." }]
  }
}
```

Single-use: a successful send removes the ID. If the send fails, the ID
remains usable until it expires; re-call `send_email`.

The stdio transport does not have an HTTP upload endpoint, so on stdio
you can only send via inline `base64Content` (small files), or by first
calling `get_email_attachment` to get an ID for forwarding.

## Inbound (reading/forwarding a file)

`get_email_attachment(accountId, emailId, attachmentId)` fetches an
attachment from a received email into the server's attachment store and
returns two things:

```json
{
  "attachmentId": "xyz...",
  "name": "report.pdf",
  "contentType": "application/pdf",
  "size": 4500000,
  "expiresAt": "..."
}
```

plus a resource link, `attachment://stash/xyz...`. A client that needs the
file's content reads that link with `resources/read`; the bytes never
pass through the model. Hand the `attachmentId` to `send_email` to
forward the file. Reading the link does not use up the ID.

## Forwarding flow (the most common pattern)

> **Prompt shortcut:** the `forward_with_attachments` prompt wraps this
> entire flow. Pass `emailId`, `accountId`, `forwardTo`, and an optional
> `note`; the prompt expands to the same steps shown below. Prefer it
> when the host supports MCP prompts.

```
get_email_details(accountId, emailId)
  → response.attachments[]   // each has provider-side attachmentId

For each attachment to forward:
  get_email_attachment(accountId, emailId, attachmentId)
  → response.attachmentId     // server-stash ID (different from provider's)

send_email(
  to=[<forward target>],
  subject="...",
  body="...",
  attachments=[
    { "attachmentId": <first server-stash ID> },
    { "attachmentId": <second server-stash ID> }
  ]
)
```

Two distinct `attachmentId` namespaces exist:

| Source | What it identifies | Where it's used |
|---|---|---|
| `get_email_details.attachments[].attachmentId` | Provider-side ID (Gmail `part-0`, Graph opaque ID) | Input to `get_email_attachment` |
| `get_email_attachment` (stash) / `POST /attachments` | Server-side store ID | Input to `send_email` |

Don't mix them — passing a provider-side ID directly to `send_email`
fails.

## Size limits

| Limit | Value | Enforced by |
|---|---|---|
| Total decoded payload per outbound message | 25 MB | This server |
| Per-attachment cap | 3 MB (M365 / Outlook.com), 25 MB (Google) | Upstream provider |
| Server store per-item | 25 MiB (fixed in this fork) | This server |

Exceeding the total or per-attachment caps results in `McpException`
with a descriptive message — surface it to the user; don't retry blindly.

## Pitfalls

- **Stash IDs are single-use** for sending; consume them by passing to
  `send_email`. Re-stash if needed.
- **IDs expire 15 minutes after the attachment is stashed.** Treat
  them as transient — get-and-use within the same conversation turn
  when possible.
- **The bytes never travel in the tool result.** `get_email_attachment`
  always stashes; a client that needs the content reads the resource
  link with `resources/read`. Forwarding needs only the `attachmentId`.
- **No filename-only attachments.** Every entry must have either
  `attachmentId` (with optional `name` override) or `base64Content`
  with `name`.
