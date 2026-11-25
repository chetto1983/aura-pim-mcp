# Accounts

Every tool in Adjutant operates against one or more configured
accounts. Understanding the account model is prerequisite to anything
else you do here.

## Discover accounts first

Call **`list_accounts`** at the start of any new session. It returns:

```json
{
  "accounts": [
    {
      "accountId": "work-m365",
      "provider": "microsoft365",
      "displayName": "Work (M365)",
      "domains": ["lhotka.net"],
      "capabilities": [
        { "name": "calendar", "readOnly": false },
        { "name": "email",    "readOnly": false },
        { "name": "contacts", "readOnly": false }
      ]
    },
    {
      "accountId": "family-cal",
      "provider": "ics",
      "displayName": "Family Calendar",
      "domains": [],
      "capabilities": [
        { "name": "calendar", "readOnly": true }
      ]
    }
  ]
}
```

Use these fields to decide what's possible before calling other tools:

- **`accountId`** — the only identifier other tools accept. Pass it
  verbatim; don't reformat or guess.
- **`provider`** — informs behavior (Gmail uses labels, M365 uses folders,
  ICS is read-only, etc.). See `providers` for details.
- **`domains`** — the email domains this account "owns." `send_email`
  does not pick an account from them: it always needs the `accountId`.
- **`capabilities`** — which categories the account supports and whether
  they are read-only. Always check this before calling a write tool.

## Provider type values

The `provider` field returned by `list_accounts` uses these canonical
strings:

| Value | Description |
|---|---|
| `microsoft365` | Microsoft 365 / Entra ID (Graph) |
| `google` | Google Workspace / Gmail |
| `outlook.com` | Consumer Outlook.com / Hotmail (Graph) |
| `imap` | IMAP + SMTP (unattended mailboxes) |
| `dav` | CalDAV + CardDAV (iCloud, Nextcloud, Fastmail, custom; aliases `caldav` / `carddav`) |
| `ics` | Subscribed iCalendar URL (calendar only, read-only) |
| `json` | JSON file (read-only; calendar + optional email/contacts) |

## Always pass `accountId` explicitly

The reads accept `accountId` as optional. `send_email`, `create_event` and
`create_contact` require it: a call without it (or with a blank one) is
refused and nothing is written, so take the id from `list_accounts`, or ask
the user which account when there is more than one. Pass `accountId`
explicitly for the other tools that write data too (`delete_email`, etc.).

The exceptions where omitting `accountId` is fine:

- `get_emails`, `search_emails`, `list_calendars`, `get_calendar_events`,
  `get_contacts`, `search_contacts` — these fan out across all enabled
  accounts when `accountId` is omitted, which is often what you want.
- `get_contextual_email_summary` — always fans out across all accounts.

The calendar fan-outs (`list_calendars`, `get_calendar_events`)
automatically skip accounts that lack a `calendar` capability (e.g.
email-only IMAP accounts), so those never produce errors or warnings.
Targeting such an account explicitly via `accountId` returns no events
plus an actionable warning.

## Capability checking before write operations

Before calling a write tool (e.g. `create_event`), confirm the chosen
account isn't read-only for that category:

```text
capability = account.capabilities.find(c => c.name === "calendar")
if !capability || capability.readOnly == true:
  pick a different account (or surface an error to the user)
```

This avoids calling `create_event` against an `ics` or `json` account,
which will fail at the provider layer with a less helpful message.

## Choosing the account for `send_email`

`send_email` requires `accountId`; a call without it is refused and nothing
is sent. The server never picks a sender from the recipient's domain. Call
`list_accounts` and pass the account's `accountId`. When the user has more
than one account and has not said which to send from, ask which.

## Multi-account fan-out

`get_emails`, `search_emails`, and `get_contextual_email_summary` query
all accounts in parallel and merge results, sorting newest-first. Each
returned email carries its own `accountId` — use that to call
`get_email_details`, `move_email`, etc. Never assume the original
`accountId` you might have used for filtering; always echo it back from
the result.

## Disabled accounts

Accounts can be marked disabled through the admin API. `list_accounts`
returns only enabled accounts. If a previously-known `accountId` stops
working, re-call `list_accounts` to refresh.
