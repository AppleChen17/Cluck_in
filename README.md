# Cluck In

A digital chicken that lives on a Logitech MX Creative Console and guards your focus time. This repository is a hackathon monorepo with independently owned modules; most of them are still scaffolds.

The MVP scope, module responsibilities, state machine, key layout, and open questions are defined in [docs/PRODUCT_SPEC_MVP.md](docs/PRODUCT_SPEC_MVP.md). Read that first before picking up work.

## Modules

| Directory | Responsibility | Technology | Status |
| --- | --- | --- | --- |
| `src/app/` | Main application, session state, mode switching, and controller | C# / .NET | Scaffold — prints a startup message |
| `src/logitech/` | Logitech Creative Console adapter | C# / .NET | Empty class library |
| `src/external/` | Gmail, Slack, and Calendar adapters | C# / .NET | Empty class library |
| `src/actions/` | Action and automation engine | C# / .NET | Empty class library |
| `src/ai-engine/` | AI decision engine and HTTP API | Python / FastAPI | Skeleton — `/health`, `/analyze-message`, `/draft-reply` backed by `MockProvider` |
| `src/web/` | Dashboard UI | React + TypeScript + Vite | Skeleton — static mock dashboard, no backend calls |

The .NET solution (`CluckIn.sln`) contains one console app and three empty class libraries. The Python and web modules run independently of it.

`src/browser/` (a Chrome extension for the AI page gatekeeper) is proposed in the MVP spec but not scaffolded yet, and has no owner — see §4 and §14 of the spec.

## Shared contracts

- [`docs/data-contracts.md`](docs/data-contracts.md) — ownership, data flow, and serialization rules.
- `shared/schemas/` — 8 cross-module JSON Schema contracts: InputEvent, ExternalMessage, ExternalEvent, SessionContext, AIRequest, AIDecision, ActionCommand, AppState.
- `shared/fixtures/` — example JSON conforming to those contracts.

The MVP needs four additions to these contracts (`/analyze-page`, `BLOCK_PAGE`, a `thinking` chicken mood, and the app ↔ browser WebSocket messages). They are listed in §11 of the spec and should be agreed on before implementation starts.

## Run locally

Prerequisites: .NET 10 SDK, Python 3.10+, Node.js 22.12+.

### .NET

From the repository root:

```sh
dotnet build CluckIn.sln
dotnet run --project src/app/CluckIn.App.csproj
```

The console app prints a scaffold startup message and exits.

### AI engine

```powershell
python -m venv src/ai-engine/.venv
src/ai-engine/.venv/Scripts/python.exe -m pip install -r src/ai-engine/requirements.txt
src/ai-engine/.venv/Scripts/python.exe -m uvicorn app:app --app-dir src/ai-engine --reload
```

On macOS/Linux, use `src/ai-engine/.venv/bin/python` instead. Open <http://127.0.0.1:8000/docs> for the interactive API documentation. The service currently returns mock responses; see [`src/ai-engine/README.md`](src/ai-engine/README.md) and [`src/ai-engine/docs/Setup.md`](src/ai-engine/docs/Setup.md) for connecting a local Ollama model.

### Web dashboard

```sh
cd src/web
npm install
npm run dev
```

Open the local URL printed by Vite (normally <http://localhost:5173>). All data is static mock data in `src/web/src/App.tsx`.

## Working agreement

Each developer owns a module. Agree on shared contracts before adding dependencies between modules. No Gmail, Slack, Calendar, Logitech SDK, Ollama, or automation integration is wired up yet — `tests/` is also still empty.

@'

## Logitech MX Creative Console

The Logitech integration is maintained under `CluckInPlugin/`.

Current capabilities include physical MX Creative Console input, Idle/Focus mode control,
AI Assist state control, semantic event routing, and local Python HTTP integration.

See [`CluckInPlugin/INTEGRATION.md`](CluckInPlugin/INTEGRATION.md) for the current interface,
testing instructions, key mappings, and integration status.
'@ | Add-Content .\README.md

## Logitech MX Creative Console

The Logitech integration is under `CluckInPlugin/`.

See `CluckInPlugin/INTEGRATION.md` for:
- key mappings
- timer controls
- InputEvent format
- local testing
- current integration status