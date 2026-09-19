# Cluck In

A digital chicken that lives on a Logitech MX Creative Console and guards your focus time. Hackathon monorepo; each module is owned by one person and runs on its own.

The MVP scope, state machine, key layout and open questions are in [docs/PRODUCT_SPEC_MVP.md](docs/PRODUCT_SPEC_MVP.md). Read that before picking up work.

## Modules

| Directory | Responsibility | Technology | Status |
| --- | --- | --- | --- |
| `src/app/` | Desktop app: focus timer, workspace and task rules, foreground-window and browser-URL sampling, chicken intervention window, HTTP API on `:5180` | C# / WPF / .NET 10 (Windows) | Implemented — see [`src/app/README.md`](src/app/README.md) |
| `CluckInPlugin/` | Logitech MX Creative Console plugin: physical keys, key-face state, Idle/Focus and AI Assist control, posts input events over HTTP | C# / .NET 10 | Implemented — see [`CluckInPlugin/INTEGRATION.md`](CluckInPlugin/INTEGRATION.md) |
| `src/external/` | Gmail (IMAP) and Slack (Socket Mode) normalized into `ExternalMessage`; sends replies back out over SMTP and `chat.postMessage`; reads and writes Google Calendar. On `:8100` | Python / FastAPI | Implemented — see [`src/external/README.md`](src/external/README.md) |
| `src/ai-engine/` | Decision engine over a local Ollama model: `/analyze-message`, `/analyze-task`, on `:8000` | Python / FastAPI | Implemented — see [`src/ai-engine/README.md`](src/ai-engine/README.md) |
| `src/web/` | Dashboard, task launcher, workspace rules editor and intervention prompt, on `:5173` | React + TypeScript + Vite | Task launcher and intervention call the app's API; the timer and AI cards and the workspace rules editor are still mock data |
| `src/app-demo/` | The original console entry point; prints context and evaluation snapshots | C# / .NET 10 | Works, kept for debugging |
| `src/actions/` | Action and automation engine | C# / .NET | Empty class library — not started |
| `src/logitech/` | — | C# / .NET | Empty class library, superseded by `CluckInPlugin/` |

### About the .NET solution

`CluckIn.sln` lists four projects, but only `CluckIn.App` contains code. `CluckIn.External` is an empty placeholder for a module that is now entirely Python, and `CluckIn.Logitech` is an empty placeholder for work that lives in `CluckInPlugin/`. Both are kept because removing them means editing the solution file, which is the most merge-conflict-prone file here.

Three projects are deliberately **outside** the solution, so `dotnet build CluckIn.sln` does not build them — run them by path:

- `CluckInPlugin/src/CluckInPlugin.csproj` (has its own solution, `CluckInPlugin/CluckInPlugin.sln`)
- `src/app-demo/CluckIn.App.Demo.csproj`
- `tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj`

## How the pieces talk

Everything is localhost HTTP with JSON. No two modules share a process.

```text
  MX Creative Console                              Browser :5173
         |                                               |
  CluckInPlugin --POST :8765/input-event--> (nothing)  src/web
                                                         |  /api via Vite proxy
                                                         v
                                                  src/app :5180
                                            /api/tasks  /api/session
                                               /api/intervention

  Gmail --+                                 +--> Gmail (SMTP)
          +--> src/external :8100 -- sends --+--> Slack (chat.postMessage)
  Slack --+              |                   +--> Google Calendar
                         +-- GET /messages, POST /reply, /calendar/*
                                 (no C# consumer yet)

  src/ai-engine :8000 --> Ollama :11434       (no consumer yet)
```

| Port | Process | Notes |
| --- | --- | --- |
| `5173` | `src/web` (Vite) | Proxies `/api` to `127.0.0.1:5180` |
| `5180` | `src/app` HTTP API | Loopback only; checks Host and Origin |
| `8100` | `src/external` | Run with `--workers 1`; the message buffer and the reply registry are per-process |
| `8000` | `src/ai-engine` | `start.py` starts Ollama if it is not already running |
| `8765` | — | Where `CluckInPlugin` posts; only `CluckInPlugin/tools/mock_receiver.py` answers |
| `11434` | Ollama | Started by `src/ai-engine/start.py` |

## Not wired up yet

The modules work. The seams between them mostly do not. In rough priority order:

1. **Logitech to app.** The plugin posts `InputEvent` to `127.0.0.1:8765/input-event`. Nothing in `src/app` listens there — its API is on `:5180` with different routes. The flow has been verified only against `CluckInPlugin/tools/mock_receiver.py`.
2. **Gmail and Slack to app.** `src/external` serves contract-valid messages on `:8100`, but no C# client polls it. It also now accepts replies on `POST /reply` and serves `/calendar/*`, and nothing calls those either — `src/external/scripts/auto_reply_demo.py` drives the whole loop standalone in the meantime.
3. **AI to app.** `src/ai-engine` serves `/analyze-message` and `/analyze-task`, but nothing calls either.
4. **Workspace rules are browser-side only.** `src/web/src/services/workspaceService.ts` edits an in-memory copy; there is no `/api/workspaces` on the C# side, so nothing you change there reaches the rules the desktop app actually enforces.
5. **`src/actions` is empty**, so no `ActionCommand` is ever executed.
6. **No Chrome extension.** Page content cannot be read, so the AI page gatekeeper in §8.1 of the spec does not exist. `src/app` reads the address bar through Windows UI Automation instead, which yields the URL but not what the page says.

## Shared contracts

[`docs/data-contracts.md`](docs/data-contracts.md) holds the ownership table and serialization rules. `shared/schemas/` has 11 JSON Schema contracts:

- Core: `InputEvent`, `ExternalMessage`, `ExternalEvent`, `SessionContext`, `AIRequest`, `AIDecision`, `ActionCommand`, `AppState`
- Task analysis: `TaskTarget`, `TaskAnalyzeRequest`, `TaskDecision`

`shared/fixtures/` has a worked example of each except `TaskTarget`, which only ever appears embedded in a `TaskAnalyzeRequest`.

Agree on a contract change before implementing against it. Chicken `mood` still lacks `thinking` and `ActionCommand` still lacks `BLOCK_PAGE`; both are listed in §11 of the spec.

## Run locally

Prerequisites: Windows, .NET 10 SDK (with the ASP.NET Core runtime), Python 3.10+, Node.js 22.12+, and Ollama for the AI engine.

### Desktop app — `:5180`

```powershell
dotnet build CluckIn.sln
dotnet run --project src/app/CluckIn.App.csproj
```

Opens the WPF dashboard. Pick a workspace, click Start Focus. [`docs/task-start.md`](docs/task-start.md) covers launching a task profile and configuring one over the API.

### Web dashboard — `:5173`

```sh
cd src/web
npm install
npm run dev
```

Needs the desktop app running, since Vite proxies `/api` to `:5180`.

### External adapters — `:8100`

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r src\external\requirements.txt
Copy-Item src\external\.env.example src\external\.env
.\.venv\Scripts\python.exe -m uvicorn app:app --app-dir src\external --port 8100 --workers 1
```

The virtualenv goes at the repository root, not inside the module: the .NET SDK globs `src/external/` recursively and would walk a `.venv` there on every build.

With no credentials configured it serves the committed `shared/fixtures`, so a fresh clone works. Credential setup is in [`src/external/docs/Setup.md`](src/external/docs/Setup.md), the HTTP contract in [`src/external/docs/API.md`](src/external/docs/API.md).

Once real credentials are configured, **Gmail and Slack deliver only what arrives after the service starts** (`SLACK_BACKFILL_MINUTES=0`, `GMAIL_ONLY_SINCE_STARTUP=true`). Start the service first, then send the demo messages — anything sent earlier will not appear. To replay older messages instead, raise `SLACK_BACKFILL_MINUTES` or set `GMAIL_ONLY_SINCE_STARTUP=false`.

### AI engine — `:8000`

```powershell
python -m venv src/ai-engine/.venv
src/ai-engine/.venv/Scripts/python.exe -m pip install -r src/ai-engine/requirements.txt
cd src/ai-engine
.venv\Scripts\python.exe start.py
```

Start it from inside `src/ai-engine`: `start.py` launches `uvicorn app:app` without `--app-dir`, so from the repository root uvicorn cannot find the module.

`start.py` checks whether Ollama is running and starts it if not. The model is set in [`src/ai-engine/config.py`](src/ai-engine/config.py), currently `gemma3:4b`. Open <http://127.0.0.1:8000/docs> for the interactive API.

### Logitech plugin

Build and load it per [`CluckInPlugin/INTEGRATION.md`](CluckInPlugin/INTEGRATION.md). To watch the events it emits without running the rest of the stack:

```powershell
python CluckInPlugin\tools\mock_receiver.py
```

## Tests

Three independent suites. None needs credentials or network access.

```powershell
# Python: src/external, 266 checks
.\.venv\Scripts\python.exe -m pytest

# C#: desktop context, task API, ViewModel, intervention
dotnet run --project tests/CluckIn.App.SmokeTests

# Web: workspace rules state (needs npm install first)
cd src/web; npm test
```

`pytest.ini` at the repository root points pytest at `src/external/tests`, so a bare `pytest` works from anywhere inside the repository: pytest walks up to find the ini file and resolves `testpaths` against it. Run it from outside the repository and it will try to collect that directory instead.

The C# smoke runner is a plain executable rather than a test-framework project, so it is run, not tested. Adding `-- --ui` also opens the real WPF window.

## Working agreement

Each developer owns a module and can work without the others running. Agree on shared contracts before adding a dependency between modules, and prefer extending `shared/schemas/` over passing provider-specific shapes around.

Calendar, automation and the Chrome extension are not started.

## Logitech MX Creative Console

The Logitech integration is maintained under `CluckInPlugin/`.

Current capabilities include physical MX Creative Console input, Idle/Focus mode control, AI Assist state control, semantic event routing, and local Python HTTP integration.

See [`CluckInPlugin/INTEGRATION.md`](CluckInPlugin/INTEGRATION.md) for the current interface, testing instructions, key mappings, and integration status.
