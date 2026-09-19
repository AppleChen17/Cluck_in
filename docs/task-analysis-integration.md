# Task Analysis integration

## Verified API contract

Read `src/ai-engine/docs/API.md`, `schemas.py`, `app.py`, and
`services/ollama_provider.py` before implementing this integration. No Python files
were changed. The Task Analysis documentation matches the implementation.

- Base URL: http://127.0.0.1:8000
- POST /analyze-task, application/json
- Required target: type (`app` / `web`), name (string).
- Optional nullable target: title, url, identifier.
- Required context: mode (`idle` / `focus` / `auto`), currentTask (string or null).
- Optional context: focusStartedAt, focusDurationSeconds (nonnegative), allowedApps,
  blockedApps, metadata.
- Required response: decision (`allow` / `warn` / `block`), relevance (0..1),
  reason (nonempty string).

The API includes **warn**, although the original integration request emphasized
allow/block. C# allows warn without starting grace or showing Chicken; the reason remains visible.
No window or tab is automatically closed, and neither decision automatically executes
Back to Work.

The API does not define allowedDomains, task ID, a task object or suggestedAction.
Domains stay in the C# deterministic whitelist. Task name and description become
one currentTask string. With no Task, Workspace name, optional description and rules
become this transport string; Session.CurrentTask stays null. No invented fields or fake URLs are sent. Python currently
does not include optional allowedApps/blockedApps in its task-analysis prompt;
C# sends allowedApps as supported context but does not rely on the LLM enforcing it.

API.md specifies no error response contract or timeout. There are no custom Python
exception handlers: validation errors use FastAPI's standard validation response;
uncaught provider/network/JSON validation errors can return HTTP 500. C# treats all
non-success HTTP responses, malformed JSON, invalid decision/relevance/reason,
network errors and timeout as unavailable. Python's Ollama request timeout is 30s.
C# defaults to 35s and never waits for it inside the WPF refresh loop.

## Composition and mapping

ITaskAnalysisClient / TaskAnalysisClient only perform HTTP and response validation.
A named IHttpClientFactory client is registered in DesktopAgentFactory. Settings are
loaded by TaskApiHost from the copied appsettings.json, with environment overrides
such as AiEngine__BaseUrl. No manager contains an AI service URL.

TaskAnalysisMapper maps existing TaskProfile, DesktopContext and WorkspaceProfile:

| Source | API field |
| --- | --- |
| Task Name + Description | context.currentTask |
| Running Focus | context.mode = focus |
| FocusSession StartTime / Duration | focusStartedAt / focusDurationSeconds |
| CurrentWhitelist AllowedApplications | context.allowedApps |
| Native app process name | target.name / identifier, type=app |
| Native window caption | target.title |
| Browser name | target.name, type=web |
| Browser page title / actual URL | target.title / url |
| Missing browser URL | url=null; no domain guessed |

Existing browser detection uses UI Automation. URLs are nullable. The integration
samples current foreground activity; it does not introduce a separate pre-launch
interceptor. Existing task startup still opens its configured Apps/Urls normally.

## Focus behavior

DesktopAgentService remains the coordinator:

1. Focus disabled/paused: skip AI; clear analysis state.
2. Temporary Allow: allow, skip AI. Do not make this temporary context a permanent
   Back to Work target.
3. Deterministic whitelist allows: allow, skip AI.
4. A meaningful Task or Workspace context exists and active process is known: background task analysis.
5. Pending: neutral status, no new intervention; WPF keeps polling and updating timer.
6. allow/warn: FocusEvaluation focused, Source=ai; no whitelist writes or grace period.
7. block: FocusEvaluation distracted; existing FocusManager and InterventionManager
   perform 7-second grace, 15-second cooldown, Chicken display and user actions.
8. Error: Source=fallback; use the original deterministic evaluation. Known violations
   can still trigger Chicken; unknown URLs remain unknown if AI is unavailable.

Rules not allowed by the existing evaluator are eligible for AI review. Chrome
continues to use only URL rules for the deterministic stage. AI semantic analysis
is the explicit second stage and may use page title, including when URL is unknown.

The WPF Workspace Start Focus button preserves CurrentTask and uses its configured
focus duration (25 minutes if unspecified). Pause, Resume and Stop Focus also retain
the task. Workspace selection changes only the generic-session fallback while a task
is selected; the task whitelist and AI context still take precedence.
Use TaskLauncher or POST /api/tasks/task_001/start to initially select/start a task.
After stopping focus, the WPF Start Focus button restarts focus for that same task.
End Task explicitly stops focus, clears CurrentTask and resets intervention/AI state.
It is available as a WPF button and POST /api/session/end-task (JSON body {}).
Without a selected task, Start Focus uses Workspace rules first, then Workspace AI.
Only when neither Task nor Workspace provides meaningful text/rules is AI skipped.
The console demo's manually assembled service also remains whitelist-only.

## Request lifecycle

One current-context analysis is retained. Key includes context source (task/workspace),
Task ID/name/description, Workspace ID/name/description/rules,
focus session, process/window, full URL, title and current workspace rules. A change
cancels/discards the previous request and restarts grace for the new context.
Only a result whose key matches the latest poll can affect focus state. The request
completion itself never updates UI, focus or intervention state.

- Successful result TTL: 45s.
- Failure backoff for unchanged context: 15s.
- Same context in flight: no duplicate requests.
- Task/context change, pause, stop, whitelist or temporary allowance hit: invalidate.
- Full URL (including path/query) participates, so different videos on the same
  domain do not share AI allow decisions.
- This is a single-context cache; returning after switching elsewhere can reanalyze.

Logs report target type, decision enum and exception type. They omit title, URL,
request/response bodies and task description. Named HTTP-client logging is disabled
for this client. Existing application logging outside this integration is unchanged.

## Manual demo

Run commands below from the repository root unless a cd is shown.

### Start services

1. Install Python dependencies (a virtual environment is recommended):

```powershell
cd src/ai-engine
python -m pip install -r requirements.txt
ollama pull gemma3:4b
python start.py
```

Use the model configured in config.py if it differs. start.py starts Ollama when
needed and runs Uvicorn on its default local port 8000. Keep this terminal open.
In another terminal, verify `Invoke-RestMethod http://127.0.0.1:8000/health`.
Health confirms the API is up, not that the model is installed or an inference works.

2. Close the old Cluck In instance, then start the C# app from repository root:

```powershell
dotnet run --project src/app/CluckIn.App.csproj
```

3. Either run `npm run dev` under src/web and click Cluck In Development's Start Task,
or start the task from another terminal:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5180/api/tasks/task_001/start -ContentType application/json -Body '{}'
Invoke-RestMethod http://127.0.0.1:5180/api/session
```

Confirm currentTask.id is task_001 and Focus is running. Task startup opens configured
apps and websites. You can now Stop Focus and Start Focus from WPF without losing
CurrentTask or AI analysis. Pause/Resume preserve it too. Press End Task only when you
want to clear the task; the next Start Focus then uses generic workspace rules.

### Cases

| Case | Steps | Expected |
| --- | --- | --- |
| A whitelist | Switch to VS Code | Focused; no AI request |
| B AI allow | Open a WPF desktop tutorial on YouTube in Chrome; click the page to leave address editing | AI pending, then allow → Focused; no Chicken; YouTube is not added to permanent whitelist |
| C AI block | Open an unrelated entertainment video or unrelated app | AI block, then remain there 7s → Chicken |
| App analysis | Use Notepad with a meaningful window/document title while not allowed by Task | type=app sent; model decision determines outcome |
| D temporary | In C click Allow Temporarily, remain on same app/domain | Focused with temporary reason; no AI request for that temporary allowance, no reminder for 5min |
| Back to Work | First visit VS Code, then trigger C and click Back to Work | Restore recorded allowed window; failures keep prompt actionable |
| E unavailable | Stop Python, then visit a new non-whitelisted context | Error-type log, deterministic fallback; timer/buttons responsive; known violation still follows grace |
| Stale response | Visit an unlisted target, immediately switch to VS Code or stop Focus | Old AI result must not bring back Chicken |
| Cache | Remain on one non-whitelisted page after response | No new request until 45s expiry |

LLM classifications are not guaranteed for a chosen title. Inspect the actual allow,
warn or block log. Automated fakes below make these branches deterministic. After
stopping Python, an already cached allow can remain valid until expiry; use a new
context for the unavailable case. Back to Work for browser targets opens the captured
URL and may create a new tab, as before.

## Automated verification

No Python or Ollama required:

```powershell
dotnet build tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj --no-restore -p:OutputPath=bin/TaskAnalysisValidation/
dotnet tests/CluckIn.App.SmokeTests/bin/TaskAnalysisValidation/CluckIn.App.SmokeTests.dll
```

TaskAnalysisChecks uses a fake ITaskAnalysisClient and an HTTP message handler. It
covers whitelist/temporary bypass, web/app mapping, nullable URLs, allow without
permanent mutation, block grace and warn allowance, failure/backoff, TTL, in-flight deduplication,
task switching, stale completions, pause/no-task behavior, timeout, JSON contract,
invalid responses and HTTP 500. Existing smoke checks remain in the runner.

## Workspace AI regression checks

WorkspaceProfile now has optional Description; Coding defaults to "Software development
and technical work". BuildWorkContext maps Task first, otherwise Workspace name,
description and nonempty rule lists. A name alone yields "Workspace: Coding.";
empty/missing meaningful context skips AI. No synthetic Task is stored.
FocusEvaluation.AiContextSource reports task, workspace or none internally. Only the
existing transport fields go to Python. Logs report source without context contents.

Workspace tests cover whitelist/temporary bypass, allow/warn without intervention,
block grace, context mapping for web/apps, same-context cache, workspace switch,
Task precedence, End Task then Workspace Focus, empty contexts and transport isolation.
To test manually: End Task, select Coding, Start Focus, and browse FastAPI docs.
The Session must retain currentTask=null, while logs show AI context source: workspace.
Both allow and warn should show Focused. Only block may trigger Chicken after grace.
