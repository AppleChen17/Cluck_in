# External module HTTP API

The external module fetches messages from Gmail and Slack, normalizes them into
the shared `ExternalMessage` contract, serves them over localhost HTTP, and
sends replies back out again. It also reads and writes Google Calendar.

It does **not** call the AI engine, and it does **not** decide what to say. The
flow is:

```
         inbound                                    outbound
Gmail ─┐                                   ┌──► Gmail (SMTP)
       ├─► src/external ──► src/app (C#) ──┤
Slack ─┘     :8100      ◄──     │      ────┴──► Slack (chat.postMessage)
                                │
                                └──► src/ai-engine :8000
```

`src/app` polls `GET /messages` here, composes its own `AIRequest`, calls
`src/ai-engine` separately, and — when it decides an answer is warranted — posts
that answer back to `POST /reply` here. The two Python services still do not
know about each other.

**Base URL:** `http://127.0.0.1:8100`

| | Endpoint | |
|---|---|---|
| **in** | [`GET /messages`](#get-messages) | what arrived |
| **out** | [`POST /reply`](#post-reply) | answer one of them, routed by its id |
| | [`POST /send/gmail`](#post-sendgmail) | a fresh email |
| | [`POST /send/slack`](#post-sendslack) | a fresh Slack post |
| | [`POST /react`](#post-react) | an emoji reaction (Slack only) |
| | [`GET /outbox`](#get-outbox) | what was sent, or would have been |
| **cal** | [`GET /calendar/events`](#get-calendarevents) | what is coming up |
| | [`POST /calendar/events`](#post-calendarevents) | put a meeting on the calendar |
| | [`GET /health`](#get-health) | |

---

## Read this before calling anything that sends

**Sending is off by default.** `EXTERNAL_SEND_DRY_RUN=true` in `src/external/.env`
means every send endpoint validates the request, resolves the destination,
records it in the outbox, and returns `200` — and hands nothing to Gmail or
Slack. Build and rehearse the whole integration against that. `GET /health`
reports the flag under `sending.dryRun`.

**Branch on `delivered`, never on the status code.** A dry run is a success: the
request was valid and fully routed. So is a provider failure, which comes back
as `200` with `delivered: false` and an `error` string, because a reply that
could not be sent belongs on the dashboard rather than in a `500`.

| | `delivered` | `dryRun` | `error` |
|---|---|---|---|
| Dry run | `false` | `true` | `null` |
| Sent | `true` | `false` | `null` |
| Provider refused | `false` | `false` | the reason |
| Already answered | as the first attempt | | `duplicate: true` |

**Replying is idempotent per message, and creating an event is idempotent per
`fromMessageId`.** This is not a nicety. Gmail mail is never marked read, so the
poll re-sees the same message every 30 seconds, and a restarted client replays
its cursor. Without these guards one incoming email becomes a reply every thirty
seconds and one Slack message becomes a new calendar entry on every restart.

**Set `EXTERNAL_SEND_ALLOWLIST` before turning the dry run off.** When it is not
empty, a real send may only go to the addresses and channel IDs listed there;
anything else is a `403`. It is the guard that stops an auto-reply loop from
writing to your supervisor because a model misread one sentence.

---

## `GET /messages`

Returns messages that arrived after your cursor.

### Query parameters

| Name | Type | Default | Notes |
|---|---|---|---|
| `cursor` | string | — | Opaque. From a previous response. Omit on the first call. |
| `limit` | int | 100 | 1–500. Values outside that range return `422`. |
| `since` | RFC 3339 | — | Optional **extra** filter on send time. **Not** a delivery cursor. Must carry an explicit offset (`Z` or `±hh:mm`); a naive or malformed value returns `422` rather than being ignored. Compared as an instant, so any offset works. |

### Response

```json
{
  "messages": [
    {
      "id": "gmail:abc123@mail.example.com",
      "source": "gmail",
      "sender": "Alice Chen",
      "title": "Demo checklist",
      "content": "Please review the Cluck In demo checklist before the rehearsal.",
      "timestamp": "2026-09-19T11:31:00+08:00",
      "unread": true
    }
  ],
  "cursor": "v1:58690bf1:127",
  "count": 1,
  "hasMore": false,
  "replayed": false
}
```

Each item in `messages` validates against
[`shared/schemas/external-message.schema.json`](../../../shared/schemas/external-message.schema.json).
The envelope around it does not — `cursor`, `count`, `hasMore` and `replayed`
are transport concerns, not contract fields.

### How to poll

```csharp
// Store the cursor. Send it back verbatim. Never parse it.
var url = $"http://127.0.0.1:8100/messages?limit=100";
if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";

var envelope = await http.GetFromJsonAsync<MessagesResponse>(url);
foreach (var message in envelope.Messages) { /* compose an AIRequest */ }
cursor = envelope.Cursor;   // persist this
```

Poll every few seconds. There is no long-polling or WebSocket.

### Four things worth knowing

**1. The cursor is opaque. Do not parse it, and do not substitute a timestamp.**

The format is `v1:<epoch>:<seq>` today and may change. More importantly, a
timestamp cannot work as a cursor here: `timestamp` is the message's *send*
time, which is not monotonic in arrival order. A delayed email arrives with a
send time older than something already delivered, so a `since=max(timestamp)`
watermark would swallow it permanently. Gmail's `Date:` header is also only
second-granular, so two messages in the same second tie.

**2. Reads are idempotent. Retrying is free.**

The same cursor always returns the same batch. Nothing is consumed or marked
delivered server-side, so a dropped HTTP response costs nothing — just retry
with the same cursor. You only advance by using the new cursor from a response
you actually received.

**3. `replayed: true` means the service restarted.**

The buffer is in memory. On restart it gets a new epoch, and your old cursor
refers to sequence numbers that no longer mean anything. Rather than returning
an empty list forever — which is how a naive integer cursor fails silently for
hours — the service replays what it has and sets `replayed: true`.

You will therefore see messages you have already processed. Dedup on
`message.id`, which is stable across restarts, and treat `replayed` as a signal
to expect that.

**4. `metadata` is absent, not null, when there is nothing to report.**

`ExternalMessage.metadata` is declared `"type": "object"` in the schema and does
not accept `null`. When present it currently carries:

| Key | Source | Meaning |
|---|---|---|
| `slackChannel` | slack | The channel ID the message came from. |
| `slackThreadTs` | slack | Present only on thread replies. **Non-contractual** — see below. |

`title`, by contrast, **is** nullable and will be an explicit `null` for every
Slack message (Slack has no subject line).

---

## `POST /reply`

Answer a message this service delivered. **This is the endpoint the auto-reply
feature is built on.**

```jsonc
{
  "messageId": "gmail:abc123@mail.example.com",  // required, from GET /messages
  "body": "我週二下午都有空。",                     // required
  "subject": null,                               // gmail only; null => "Re: <original>"
  "inThread": true                               // slack only; false posts loose in the channel
}
```

You pass only the id. Everything needed to actually deliver the reply — the
sender's email address, the RFC 822 `Message-ID` and `References` chain, the
Slack channel and `thread_ts` — is looked up server-side from a registry the
adapters fill in as they emit.

**Why it is not in the message you were given.** `ExternalMessage.sender` is a
display name, by contract: *"Senders and attendees are normalized human-readable
strings, not provider user objects"* (`docs/data-contracts.md`). An email address
therefore cannot be recovered from a delivered message, and publishing one in
every message would push personal data and provider detail into every consumer.

### Response — `SendResult`

```json
{
  "id": "out-6f2a1c9b40d7",
  "source": "gmail",
  "target": "alice@example.com",
  "delivered": false,
  "dryRun": true,
  "duplicate": false,
  "inReplyTo": "gmail:abc123@mail.example.com",
  "providerId": null,
  "preview": "我週二下午都有空。",
  "timestamp": "2026-09-19T11:45:12+08:00",
  "error": null
}
```

`providerId` is the outgoing `Message-ID` for Gmail and the message `ts` for
Slack, on a real send only.

### Errors

| Status | Meaning |
|---|---|
| `404` | No route known for that id. See below. |
| `403` | A real send to a destination outside `EXTERNAL_SEND_ALLOWLIST`. |
| `503` | The transport has no credentials configured. |
| `422` | Empty body, or an unrecognized field (the models are `extra="forbid"`). |

**A `404` after a restart is expected.** The reply registry is in memory, like
the message buffer. After a restart `GET /messages` still replays what it has
(`replayed: true`), but those messages can no longer be routed by id. Use
`POST /send/gmail` or `POST /send/slack` with an explicit destination instead —
that is what they are for.

### Threading

Gmail replies carry `In-Reply-To` and `References` pointing at the original, so
Gmail shows them inside the conversation rather than as a new mail that happens
to start with "Re:". A copy is also filed in your Sent folder over IMAP —
without that step the reply is genuinely delivered but invisible in your own
account, which during a demo is indistinguishable from a failure.

Slack replies default to the thread. `"inThread": false` posts them loose in the
channel instead.

---

## `POST /send/gmail`

A fresh email that answers nothing. **Not deduplicated** — two identical calls
send two emails.

```json
{ "to": ["bob@example.com"], "subject": "Cluck In demo", "body": "...", "cc": null }
```

Returns a `SendResult`. Only `to[0]` is used as the allowlist destination; the
rest are ordinary recipients.

## `POST /send/slack`

```json
{ "channel": "C08ABCDEF", "text": "...", "threadTs": null }
```

An omitted or empty `channel` falls back to `SLACK_DEFAULT_CHANNEL`; with
neither, `422`. Returns a `SendResult`.

## `POST /react`

```json
{ "messageId": "slack:C08ABCDEF:1789788720.000200", "emoji": "👍" }
```

Acknowledging a proposed meeting time with a thumbs up is a real answer and a
much cheaper one than a sentence, which is why it has its own endpoint.

`emoji` takes either a Slack short name (`thumbsup`, `:thumbsup:`) or the
character itself (`👍`), which is translated for you — `reactions.add` rejects
the character with `invalid_name`. An unrecognized value is passed through with
its colons stripped, which is correct for custom emoji.

Gmail has no equivalent and returns `422` rather than pretending.

```json
{ "messageId": "slack:...", "emoji": "thumbsup", "delivered": true, "dryRun": false }
```

Repeating it is harmless: Slack answers `already_reacted`, which is treated as
success.

## `GET /outbox`

Everything sent, newest first, **including dry runs** — which is the point. It
is what a dashboard shows to prove the chicken answered something, and what you
watch during a rehearsal to see what it would have said.

```json
{ "sent": [ ], "count": 2 }
```

`?limit=` defaults to 50, max 500. In memory, and lost on restart.

---

## `GET /calendar/events`

`?withinDays=` (default 7, max 60). Items validate against
[`shared/schemas/external-event.schema.json`](../../../shared/schemas/external-event.schema.json).

```json
{ "events": [ ], "count": 1, "backend": "google" }
```

**All-day events are omitted.** Google gives them as a bare date with no time
and no offset; the contract requires a full date-time, and
`docs/data-contracts.md` defers all-day handling explicitly. Coercing one to
midnight would put a fake nine-hour block on the calendar and make the whole day
look busy.

## `POST /calendar/events`

```jsonc
{
  "title": "Cluck In 進度會議",
  "startTime": "2026-09-22T15:00:00+08:00",   // RFC 3339, explicit offset
  "endTime": "2026-09-22T16:00:00+08:00",     // strictly later
  "description": null,
  "location": null,
  "attendees": ["bob@example.com"],
  "fromMessageId": "slack:C08ABCDEF:1789788720.000200"
}
```

`201` with the created event in an `EventsResponse`. `422` for a timestamp
without an offset, an unparseable one, or an end that is not after the start —
checked here rather than at the provider, so the error names which field is
wrong.

**`fromMessageId` is the idempotency key, and you want to pass it.** A second
create carrying the same one returns the original event with `"duplicate": true`
and creates nothing. Without it, a client that replays its cursor after a
restart puts a second identical meeting on a real calendar, and unlike a
duplicate reply that one is still there tomorrow. It is also recorded in the
event's `metadata` so a dashboard can say which message caused the entry; it is
never sent to Google.

Attendees are only invited by email when `CALENDAR_SEND_INVITES=true`. Entries
without an `@` are dropped — Google rejects a bare display name — while the
contract allows either spelling.

---

## `GET /health`

```json
{
  "status": "ok",
  "epoch": "58690bf1",
  "buffered": 2,
  "adapters": [
    {
      "name": "gmail",
      "enabled": true,
      "connected": true,
      "lastEventAt": "2026-09-19T11:31:00+08:00",
      "lastError": null,
      "emitted": 12
    }
  ],
  "sending": {
    "dryRun": true,
    "allowlisted": 0,
    "ready": ["gmail", "slack"],
    "replyTargets": 14,
    "outbox": 3,
    "calendarEvents": 1
  },
  "calendar": { "backend": "memory", "available": true }
}
```

`status` is `degraded` when any adapter has a `lastError` or when no adapter is
configured. Never performs network I/O, so it is safe to poll frequently.

`sending.dryRun` is the first field to look at when a reply did not arrive:
everything else can be configured perfectly and nothing leaves the machine while
it is true. `sending.ready` lists the transports that have credentials; it says
nothing about whether sending is enabled. `sending.allowlisted` is a count, not
the list — the destinations themselves are not published here.

`calendar.backend` is what is **actually** in use, which is not always what was
asked for: `CALENDAR_BACKEND=google` with no token falls back to `memory` and
reports it here rather than refusing to start.

A comparison of `epoch` against the epoch inside your stored cursor tells you
whether a restart happened, without having to make a `/messages` call.

---

## `POST /debug/inject`

Pushes a hand-written `ExternalMessage` straight into the buffer, so you can
test your polling loop without any real Gmail or Slack traffic. Returns `404`
unless `EXTERNAL_DEBUG=true` in `src/external/.env`.

```powershell
$json = '{"id":"slack:C1:1.0","source":"slack","sender":"Bob","title":null,' +
        '"content":"Production build failed.","timestamp":"2026-09-19T11:32:00+08:00","unread":true}'
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8100/debug/inject `
  -ContentType 'application/json; charset=utf-8' `
  -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
```

> Passing a plain string to `-Body` in Windows PowerShell 5.1 encodes it as
> Latin-1, which mangles every Chinese character before it leaves your machine.
> Always convert to `[byte[]]` as above.

---

## Developing against this without credentials

Set `EXTERNAL_ADAPTERS=fixture` (the default) and the service serves the
committed `shared/fixtures/external-message.*.json` messages. Sending stays in
its default dry run, and `CALENDAR_BACKEND=memory` keeps events in the process.
No Gmail account, no Slack workspace, no Google project. You can write and test
the whole C# integration — both directions — before any credential exists.

One gap to know about: **the committed fixtures cannot be replied to.** The
Slack fixture id is `slack:msg-001`, which carries no channel, and the Gmail
fixture has no address to answer (`sender` is a display name by contract). So
`POST /reply` returns `404` for them.

`scripts/seed_demo_messages.py` exists for that: it injects three messages whose
ids and metadata do route, so the full loop works with no credentials at all.
See the module README.

---

## Open item for the team

**Slack thread replies have nowhere contractual to live.** No contract defines a
`threadId`, `conversationId`, or `inReplyTo` field, so `slackThreadTs` sits in
the open `metadata` object. If you need conversation grouping, that needs a
schema change, and `docs/data-contracts.md` (team decision #6) warns that the
strict `additionalProperties: false` objects mean even a new optional field can
be rejected by a consumer built against the older schema. Raise it before
relying on it.
