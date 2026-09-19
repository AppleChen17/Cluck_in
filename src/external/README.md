# src/external — message adapters

Fetches messages from Gmail and Slack and normalizes them into the shared
[`ExternalMessage`](../../shared/schemas/external-message.schema.json) contract,
served over localhost HTTP for `src/app` to poll.

**This module does not call the AI engine.** It fetches and normalizes, nothing
more. `src/app` polls `GET /messages` here, composes its own `AIRequest`, and
calls `src/ai-engine` separately.

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

Credentials setup is in [`docs/Setup.md`](docs/Setup.md).

## Tests

```powershell
cd C:\Users\user\Desktop\Cluck_in
.\.venv\Scripts\python.exe -m pytest
```

84 tests, no credentials and no network required. Gmail parsing runs against
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
| `app.py` | FastAPI app: `/health`, `/messages`, `/debug/inject` |
| `buffer.py` | Thread-safe buffer and cursor, shared by the adapter threads and the request threads |
| `normalize.py` | Pure text/timestamp helpers: HTML flattening, quoted-reply trimming, Slack markup, RFC 3339 |
| `schemas.py` | `ExternalMessage` and the HTTP envelope |
| `config.py` | Settings, loaded from `.env` |
| `adapters/gmail_adapter.py` | IMAP polling thread |
| `adapters/slack_adapter.py` | Socket Mode listener |
| `adapters/fixture_adapter.py` | Replays `shared/fixtures`, needs no credentials |
| `scripts/try_llm.py` | Probe, not production code — see below |

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
