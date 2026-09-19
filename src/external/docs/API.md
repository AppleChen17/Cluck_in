# External module HTTP API

The external module fetches messages from Gmail and Slack, normalizes them into
the shared `ExternalMessage` contract, and serves them over localhost HTTP.

It does **not** call the AI engine. The flow is:

```
Gmail ─┐
       ├─► src/external ──HTTP──► src/app (C#) ──HTTP──► src/ai-engine
Slack ─┘     :8100                                          :8000
```

`src/app` polls `GET /messages` here, composes its own `AIRequest`, and calls
`src/ai-engine` separately. The two Python services do not know about each other.

**Base URL:** `http://127.0.0.1:8100`

---

## `GET /messages`

Returns messages that arrived after your cursor.

### Query parameters

| Name | Type | Default | Notes |
|---|---|---|---|
| `cursor` | string | — | Opaque. From a previous response. Omit on the first call. |
| `limit` | int | 100 | 1–500. Values outside that range return `422`. |
| `since` | RFC 3339 | — | Optional **extra** filter on send time. **Not** a delivery cursor. |

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
  ]
}
```

`status` is `degraded` when any adapter has a `lastError` or when no adapter is
configured. Never performs network I/O, so it is safe to poll frequently.

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
committed `shared/fixtures/external-message.*.json` messages. No Gmail account,
no Slack workspace, no tokens. You can write and test the whole C# integration
before any credential exists.

---

## Open item for the team

**Slack thread replies have nowhere contractual to live.** No contract defines a
`threadId`, `conversationId`, or `inReplyTo` field, so `slackThreadTs` sits in
the open `metadata` object. If you need conversation grouping, that needs a
schema change, and `docs/data-contracts.md` (team decision #6) warns that the
strict `additionalProperties: false` objects mean even a new optional field can
be rejected by a consumer built against the older schema. Raise it before
relying on it.
