# src/external — messages in, replies out

Two directions.

**Inbound:** fetches messages from Gmail and Slack and normalizes them into the
shared [`ExternalMessage`](../../shared/schemas/external-message.schema.json)
contract, served over localhost HTTP for `src/app` to poll.

**Outbound:** sends the reply `src/app` decided on — back into the same mail
thread or the same Slack thread — and reads and writes Google Calendar, which
is what lets "when are you free?" be answered without a person.

**This module does not call the AI engine, and it does not decide what to say.**
`src/app` polls `GET /messages` here, composes its own `AIRequest`, calls
`src/ai-engine` separately, and posts the answer back to `POST /reply` here.

Sending is **off by default**: `EXTERNAL_SEND_DRY_RUN=true` routes and records
every send and hands nothing to a provider, so the whole integration can be
built and rehearsed before anything leaves the machine. See
[`docs/API.md`](docs/API.md) before calling anything that sends.

## Why there is Python next to a .csproj

The repo originally planned this module in C#. It is Python because the Gmail
and Slack tooling is far better there (Gmail needs no library at all — `imaplib`
and `email` are standard library), and because it keeps this module in the same
language as `src/ai-engine`.

Interop is plain localhost HTTP + JSON, which `src/app` already has to speak to
reach `src/ai-engine`. From C# it is one more `HttpClient` call. See
[`docs/API.md`](docs/API.md).

`CluckIn.External.csproj` is intentionally left in place and empty. Removing it
would mean editing `CluckIn.sln` — two `Project` lines, twelve configuration
rows, and a nesting entry — in the file most likely to produce merge conflicts
in a five-person repo. An empty class library costs a fraction of a second of
`dotnet build` and nothing else. Retire it after the demo.

## Running it

The virtualenv lives at the **repo root**, not in this directory. The .NET SDK
globs a project directory recursively, so a `.venv` here would make every
`dotnet build` walk ten thousand files.

```powershell
cd C:\Users\user\Desktop\Cluck_in
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r src\external\requirements.txt
Copy-Item src\external\.env.example src\external\.env   # then fill it in
```

```powershell
.\.venv\Scripts\python.exe -m uvicorn app:app --app-dir src\external --port 8100 --workers 1
```

> **Use `--workers 1` and do not add `--reload`.**
>
> Multiple workers are separate processes, which means separate in-memory
> buffers — `src/app` would see messages appear and disappear depending on which
> worker answered — and two Slack socket connections delivering every event
> twice. `--reload` additionally wipes the buffer and its epoch on every file
> save, which invalidates every cursor the client is holding.

With no configuration the service runs the `fixture` adapter and serves the
committed example messages, so it works on a fresh clone with no credentials.

## Configuration

Every setting lives in `src/external/.env`; see
[`.env.example`](.env.example) for the full list.
`EXTERNAL_ADAPTERS` selects sources: a comma list of `gmail`, `slack`, `fixture`.
It is the only switch that does — `SLACK_ENABLED` and `GMAIL_ENABLED` are read
by nothing and only earn you a warning. Selecting `slack` or `gmail` without
their credentials, or naming a source that does not exist, **stops the service
starting** with an error naming the missing variable; it never quietly serves
the fixtures instead. Startup logs `Message source: slack` so you can see which
one you got. Details in [`docs/API.md`](docs/API.md#choosing-the-message-source).

`CALENDAR_BACKEND` selects `memory` (no credentials) or `google`.

Credentials setup is in [`docs/Setup.md`](docs/Setup.md), including why the
calendar is the one thing here that cannot use an app password.

### Verified against real accounts

Everything below was exercised end to end against a real Gmail account, a real
Slack workspace and a real Google Calendar, not only against the test suite:

| | |
|---|---|
| Slack Socket Mode in, `chat.postMessage` and `reactions.add` out | works |
| Gmail IMAP in, SMTP out, copy filed in Sent | works |
| Reply threading (`In-Reply-To` on a real send) | works |
| `events.list`, `events.insert` | works |
| One reply per message, one event per `fromMessageId` | both triggered and held |

One thing that only showed up once real credentials were involved, and that no
test would have caught, is written up under **Known limitations** below: a
calendar whose own timezone setting is wrong displays every event at the wrong
hour while storing the right instant. (A second, `freebusy` needing a scope the
other calls do not, went away with the availability feature — the scope is still
requested so existing tokens stay valid.)

## Tests

```powershell
cd C:\Users\user\Desktop\Cluck_in
.\.venv\Scripts\python.exe -m pytest
```

270 tests, no credentials and no network required. Gmail parsing runs against
canned RFC 822 bytes and Slack against canned event payloads, so the whole
normalization surface — MIME multipart, Big5, RFC 2047 subjects, HTML
flattening, quoted-reply trimming, Slack markup, event filtering — is covered
without an account.

The most important of them is `test_schema_conformance.py`, which validates
every message this module produces against the real schema file with date-time
format checking enabled. That is what catches `title: ""`, `sender: ""`,
`metadata: null`, a naive timestamp, or a stray extra key — all of which a
strict C# deserializer would otherwise reject at runtime.

## Layout

| File | Purpose |
|---|---|
| `app.py` | FastAPI app: every endpoint |
| `buffer.py` | Thread-safe buffer and cursor, shared by the adapter threads and the request threads |
| `normalize.py` | Pure text/timestamp helpers: HTML flattening, quoted-reply trimming, Slack markup, RFC 3339 |
| `schemas.py` | `ExternalMessage`, `ExternalEvent` and the HTTP envelopes |
| `config.py` | Settings, loaded from `.env` |
| `reply_registry.py` | Where a reply goes, keyed by message id — the routing the contract deliberately does not carry |
| `idempotency.py` | Bounded "have we already done this for that message?" memo |
| `adapters/gmail_adapter.py` | IMAP polling thread |
| `adapters/slack_adapter.py` | Socket Mode listener |
| `adapters/fixture_adapter.py` | Replays `shared/fixtures`, needs no credentials |
| `sender/base.py` | The dry run, the allowlist, and the one path every outbound message takes |
| `sender/outbox.py` | What was sent or would have been; one reply per message, ever |
| `sender/gmail_sender.py` | SMTP send, RFC 822 threading, filing a copy in Sent |
| `sender/slack_sender.py` | `chat.postMessage` and `reactions.add` |
| `gcal/memory_calendar.py` | In-process calendar, needs no credentials |
| `gcal/google_calendar.py` | Google Calendar API v3 |
| `scripts/setup_google_oauth.py` | One-off OAuth, see `docs/Setup.md` |
| `scripts/seed_demo_messages.py` | Demo scaffolding — see below |
| `scripts/auto_reply_demo.py` | Demo glue, not production code — see below |
| `scripts/try_llm.py` | Probe, not production code — see below |

`gcal` rather than `calendar`: this module uses flat imports
(`uvicorn --app-dir src/external`), so a package named `calendar` here would
shadow the standard library module of that name for the whole process.

## Demoing the auto-reply loop

Two scripts, neither of them production code and neither imported by anything.
Together they show the whole feature **with no credentials at all**.

```powershell
# terminal 1 - the service. EXTERNAL_DEBUG=true in .env is required.
.\.venv\Scripts\python.exe -m uvicorn app:app --app-dir src\external --port 8100 --workers 1

# terminal 2
.\.venv\Scripts\python.exe src\external\scripts\seed_demo_messages.py
.\.venv\Scripts\python.exe src\external\scripts\auto_reply_demo.py --once
```

### `scripts/seed_demo_messages.py`

Injects three messages through `POST /debug/inject`.

Why not the committed fixtures? **They cannot be replied to.** The Slack fixture
id is `slack:msg-001`, which carries no channel, and the Gmail fixture has no
address to answer — `ExternalMessage.sender` is a display name by contract. The
seeded messages carry ids and metadata that route, so `POST /reply` works.

The three are chosen deliberately:

| | The chicken |
|---|---|
| "我們約在下週二下午三點開會" | reacts 👍, adds the entry, says which time |
| "附件是上週的會議紀錄，有空再看" | **does nothing** — a meeting with no time |
| "下週一開始咖啡機移到二樓" | **does nothing** |

Two of the three produce nothing, and that is the point. The second is the
sharper one: it talks about a meeting and still gets no calendar entry, because
it names no time. An assistant that answers everything is not trustworthy, and
one that invents a time it did not read is worse.

`--reply-to you@gmail.com` to point the email at yourself for a live run.

### `scripts/auto_reply_demo.py`

The loop, in a straight line:

```
GET /messages            what arrived
  classify intent        local model, or keywords if it is not running
telling you about a meeting:
  POST /react                    a thumbs up is a real answer
  POST /calendar/events          put it on the calendar
  POST /reply                    say which time went on it
```

**This is not a stand-in for `src/app`.** The real product decides in the C#
state machine, with the focus session and the current task in hand, and calls
the same endpoints. This exists so the feature can be seen working and rehearsed
before that side is ready.

Two things it does on purpose, both worth copying into the C# side:

- **The confirmation quotes the time read back from the created event**, not the
  time it asked for. That catches a timezone mistake of our own as well as a bad
  extraction by the model.
- **An extracted meeting time is validated before use.** No offset, unparseable,
  or in the past means no calendar entry and a line saying so. Models reach for
  the current year and last week's weekday; a meeting on the wrong day is worse
  than no meeting. Verified on real messages: four of five times were extracted
  correctly ("下午兩點四十五" → 14:45, "下午一點半" → 13:30, "10/24 15:00" →
  15:00), and "下週二下午三點" became 13:00 once, from the same input that gave
  15:00 on another run at `temperature: 0`. Treat every extracted time as
  unverified.
- **It says which time it wrote, and does not offer to correct it.** Showing the
  inference is what lets a person catch a bad one. Offering to fix it would be a
  lie today: a reply in that thread is classified as a brand new message with no
  memory of the first, so "no, make it 4pm" produces a SECOND calendar entry.
  Thread context and a PATCH endpoint come before that invitation does.

`--no-llm` forces the keyword classifier, which is crude but never fails to
start. It is there so the demo degrades to something visible instead of dying on
stage. It also cannot extract a time, so a meeting invite gets the reaction and
no calendar entry — visible in the output, not silent.

With `EXTERNAL_SEND_DRY_RUN=true` (the default) nothing is sent; watch
`GET /outbox` for what it would have said.

## What `ai-engine` still needs

The demo script talks to Ollama directly instead of going through
`src/ai-engine`, and that is not a shortcut — `ai-engine` cannot answer the
question. `/analyze-message` returns `urgent` / `allow` / `hold`, which is about
whether to interrupt you. Auto-reply needs a different one: *is this person
telling me a meeting time, or not?*

So one endpoint is missing. **The contract for it is now committed** as
[`intent-analyze-request.schema.json`](../../shared/schemas/intent-analyze-request.schema.json)
and [`intent-decision.schema.json`](../../shared/schemas/intent-decision.schema.json),
with three worked fixtures and 51 checks in
[`tests/test_shared_contracts.py`](tests/test_shared_contracts.py). It is
**proposed, not agreed** — see the "Intent classification" section of
[`docs/data-contracts.md`](../../docs/data-contracts.md). Nothing implements it
yet; the shape is committed so it can be reviewed concretely rather than
described.

In that module's own style:

```python
class IntentAnalyzeRequest(BaseModel):
    message: ExternalMessage
    context: SessionContext
    now: datetime                    # required, see below

class IntentDecision(BaseModel):
    messageId: str                   # injected by the caller, never generated
    intent: Literal["meeting_invite", "other"]
    confidence: float                # 0-1; the caller sets its own threshold
    reason: str
    # meeting_invite only; null means "about a meeting, but I cannot tell when"
    startTime: datetime | None = None
    endTime: datetime | None = None
    title: str | None = None
```

`endTime` rather than a duration, so the value passes straight into
`POST /calendar/events` and `ExternalEvent` without unit arithmetic.

### Four things that cost time to find

**`now` must be in the request.** The model does not know what day it is and
will confidently pick one. The demo passes `Now: 2026-09-19T22:10+08:00
(星期六)` at the top of the user prompt.

**Ollama's `format` needs a FLAT schema.** Its JSON-Schema-to-grammar converter
does not handle `$ref` or `$defs` reliably, so `shared/schemas/*.json` cannot be
handed to it directly. It also ignores `minimum` / `maximum`, so numeric bounds
have to be clamped in Python afterwards.

**Field order is generation order.** Putting `reason` before `intent` makes the
model justify before it commits to a label. On a 3b model that is a free quality
gain; on a large one it costs nothing.

**Never ask the model to echo an id back.** A 3b model asked to reproduce
`slack:C08ABCDEF:1789788720.000200` drops a digit. The caller injects it.

### Worth deciding at the same time

Whether `startTime` belongs in the response at all.

Extracting the time is where this goes wrong. Measured on real messages:
`下午兩點四十五` → 14:45, `下午一點半` → 13:30, `10/24 15:00` → 15:00 — and
`下週二下午三點` → 13:00 on one run and 15:00 on another, from an identical
prompt at `temperature: 0`.

Parsing Chinese time expressions in code instead would fix that, and is faster.
Measured on this machine with `qwen2.5:3b-instruct`:

| the model outputs | time |
|---|---|
| reason + startTime + duration + title (69 tokens) | 3.95 s |
| the label alone (12 tokens) | 0.83 s |
| a keyword pre-filter, no model call at all | 0.0029 ms |

Generation is per-token, so a shorter answer is a proportionally faster one. A
deterministic parser therefore buys correctness and roughly 5x at once, and the
failure mode changes from *a confidently wrong time* to *no time, and it says
so* — which is the difference between a meeting on the wrong day and a meeting
nobody scheduled.

That parser belongs next to the classification, in `ai-engine`, not here.
`src/external` does not decide anything and should not start.

### And routing through `ai-engine` costs nothing

Measured on this machine: one localhost HTTP round trip is **6.2 ms**, one
classification is **3.9 s**. Going `app → ai-engine → app` adds two hops, about
12 ms, to a 3.9 second operation — **0.3%**, which is below the run-to-run noise
of the model itself.

There is no latency argument for `src/external` calling a model directly, and it
will not. Nor is there one for `src/app` skipping `ai-engine`.

## `scripts/try_llm.py`

A standalone probe that feeds real fetched messages to a local Ollama model and
prints what it decided and how long it took. Nothing imports it and it adds no
runtime dependency (standard-library `urllib` only).

It exists to answer one question for whoever owns `src/ai-engine`: **is
`gemma3:1b` good enough to classify our messages, which are in Chinese?**
`PRODUCT_SPEC_MVP.md` section 12 calls model capability the make-or-break risk of
the MVP and says to measure it on day one.

```powershell
.\.venv\Scripts\python.exe src\external\scripts\try_llm.py --fixtures
.\.venv\Scripts\python.exe src\external\scripts\try_llm.py --model qwen2.5:3b-instruct --repeat 3
```

Requires Ollama: `winget install --id Ollama.Ollama -e`, then
`ollama pull qwen2.5:3b-instruct`. The Windows installer already runs a
background service on port 11434, so do not also run `ollama serve` — check with
`Invoke-RestMethod http://127.0.0.1:11434/api/tags` first.

Findings from the first run are written up in
[`docs/llm-findings.md`](docs/llm-findings.md). The short version: use
`qwen2.5:3b-instruct` rather than `gemma3:1b`, and address Ollama as
`127.0.0.1`, never `localhost` — the latter costs 2 seconds per request.

## `scripts/check_machine.py`

A single self-contained file for answering "would another machine be fast
enough?". Copy it anywhere, install Ollama, `ollama pull qwen2.5:3b-instruct`,
and run it with any Python 3.9+ — no repository, no virtualenv, no dependencies.

```powershell
python check_machine.py
```

It times the `localhost` versus `127.0.0.1` round trip, classifies five fixed
Chinese messages, and breaks the result into load / prompt evaluation /
generation, so a slow number can be attributed rather than guessed at. Compare
the **tokens per second** figure: generation is memory-bandwidth-bound, so that
is what changes between machines.

## Known limitations

- **Socket Mode has no backfill.** Slack events sent while this process is down
  are lost. `SLACK_BACKFILL_MINUTES` replays recent history on connect.
- **The buffer is in memory.** A restart means a new epoch, and clients get a
  full replay flagged with `replayed: true`. Dedup on `id`.
- **Gmail polls; it does not push.** Python 3.13's `imaplib` has no IDLE
  support, so `GMAIL_POLL_SECONDS` (default 30) bounds the latency.
- **Mail is never marked read.** Two independent safeguards enforce this, which
  also means the same unread messages are re-fetched every poll; dedup by `id`
  in the buffer is what makes that correct.
- **A bot token cannot read human-to-human Slack DMs.** `im:history` covers only
  DMs with the bot itself. Reading your own DMs needs a user token (`xoxp-`).
- **Sending is off by default and that is the most likely reason a reply did not
  arrive.** Check `sending.dryRun` in `/health` first.
- **Google Calendar displays events in the calendar's own timezone, not the
  event's.** An event created correctly at `18:00+08:00` shows as `10:00` on a
  calendar whose timezone setting is UTC. The stored instant is right and the
  API round trip is right; only the grid is wrong, and nothing in this module
  can fix it -- the setting is in Google Calendar, under the gear icon. Worth
  checking before a demo, because "the AI put the meeting at 10am" is a
  convincing-looking bug that is not a bug.
- **Gmail is up to `GMAIL_POLL_SECONDS` behind.** Measured at ~40s end to end on
  a 30s poll. Slack is a push and arrives in seconds. Do not read a quiet
  minute as a failure.
- **The reply registry is in memory too.** After a restart `GET /messages`
  replays what it has, but those messages can no longer be answered by id;
  `POST /reply` returns a `404` saying so, and `/send/gmail` and `/send/slack`
  with an explicit destination are the way through.
- **One reply per message and one calendar entry per `fromMessageId`, and both
  memos are bounded.** Answering something from long enough ago that it has
  fallen out of the memo would send a second reply. The bounds are 4x the outbox
  size and the outbox size respectively.
- **Google Calendar needs OAuth, and a Testing-mode refresh token expires after
  seven days.** Re-run `scripts/setup_google_oauth.py`. There is no app-password
  path for the calendar — see `docs/Setup.md` for why.
- **All-day calendar events are skipped**, matching `docs/data-contracts.md`,
  which defers them.
- **Nothing here decides what to say.** If the replies read badly, that is the
  caller's prompt, not this module.
