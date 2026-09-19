# Cluck In

A hackathon monorepo with five independently owned modules. This initial scaffold contains runnable entry points only; integrations and business logic are not implemented.

## Modules

| Directory | Responsibility | Technology |
| --- | --- | --- |
| `src/app/` | Main application, session state, mode switching, and controller | C# / .NET |
| `src/logitech/` | Logitech Creative Console adapter | C# / .NET |
| `src/external/` | Gmail, Slack, and Calendar adapters | C# / .NET |
| `src/ai/` | AI decision engine and HTTP API; future Ollama connection | Python / FastAPI |
| `src/actions/` | Action and automation engine | C# / .NET |

The .NET solution contains a console app and three empty class libraries. Initial shared data contracts are defined in [docs/data-contracts.md](docs/data-contracts.md); module implementations are not connected yet.

## Run locally

Prerequisites: .NET 10 SDK and Python 3.10+.

From the repository root:

```sh
dotnet build CluckIn.sln
dotnet run --project src/app/CluckIn.App.csproj
```

The console app prints a scaffold startup message and exits.

For the AI API (PowerShell):

```powershell
python -m venv src/ai/.venv
src/ai/.venv/Scripts/python.exe -m pip install -r src/ai/requirements.txt
src/ai/.venv/Scripts/python.exe -m uvicorn api:app --app-dir src/ai --reload
```

On macOS/Linux, use `src/ai/.venv/bin/python` instead. Open <http://127.0.0.1:8000/docs> for FastAPI's default documentation page. No application endpoints exist yet.

## Shared work

- `shared/schemas/`: initial cross-module JSON Schema contracts.
- `shared/fixtures/`: example JSON data conforming to those contracts.
- `tests/`: future test projects and integration tests.
- `docs/`: project notes and developer documentation.

Each developer can work within their module. Agree on shared contracts before adding dependencies between modules. No Gmail, Slack, Calendar, Logitech SDK, Ollama, or automation implementation is included.
