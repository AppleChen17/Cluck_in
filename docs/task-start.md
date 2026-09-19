# Start Task

## Run and try the demo

Requires Windows and .NET 10 SDK (including ASP.NET Core runtime). From the repository root:

```powershell
dotnet run --project src/app/CluckIn.App.csproj
```

In another terminal:

```powershell
cd src/web
npm run dev
```

Open http://localhost:5173 and click **Start Cluck In Development** in the Tasks card.
The frontend loads `GET /api/tasks` and sends JSON to `POST /api/tasks/{taskId}/start`.
Vite proxies `/api` to the WPF process at `http://127.0.0.1:5180`. Keep the desktop
app running. The production frontend needs an equivalent local proxy; `vite preview`
is not configured as an API host. Other dashboard timer/AI widgets remain demo data;
the WPF dashboard and `GET /api/session` show the real focus timer.

```powershell
Invoke-RestMethod -Method Post `
  -Uri 'http://127.0.0.1:5180/api/tasks/task_001/start' `
  -ContentType 'application/json' -Body '{}'
```

Expected response:

```json
{"success":true,"taskId":"task_001","taskName":"Cluck In Development"}
```

The demo tries VS Code at `CLUCK_IN_CODE_PATH`, the standard per-user installation,
then the standard Program Files installation. Set that environment variable before
starting the desktop app if needed. It opens localhost:5173, the repository
https://github.com/AppleChen17/Cluck_in, and https://chatgpt.com. It loads Code.exe /
chrome.exe and localhost / github.com / chatgpt.com whitelists and starts 50 minutes.

## Configure a task before starting it

`PUT /api/tasks/{taskId}` creates or replaces the entire profile. Settings are held
in memory and reset when the desktop app exits. `GET /api/tasks/{taskId}` retrieves
one profile. Paths must be absolute existing `.exe` paths; environment variables
such as `%LOCALAPPDATA%` are expanded when launching. URLs must be absolute HTTP(S).
Invalid launch targets are recorded at startup and do not block the remaining targets.

```powershell
$profile = @{
  id = 'task_001'
  name = 'Cluck In Development'
  description = 'Implement and test Cluck In'
  apps = @("$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe")
  urls = @('http://localhost:5173', 'https://github.com/AppleChen17/Cluck_in', 'https://chatgpt.com')
  allowedApps = @('Code.exe', 'chrome.exe')
  allowedDomains = @('localhost', 'github.com', 'chatgpt.com')
  focusDurationMinutes = 50
} | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri 'http://127.0.0.1:5180/api/tasks/task_001' `
  -ContentType 'application/json' -Body $profile
```

Omit `focusDurationMinutes` or set it to null for no timer. Supplied durations must
be positive integers. Switching to a task without a duration stops any previous
timer. Whitelists replace previous task rules rather than accumulating.

## Flow and ownership

1. `TaskManager` serializes starts and reads a profile through `ITaskRepository`.
2. `SessionManager` sets the current task (including description).
3. `WhitelistManager` snapshots its allowed apps and domains.
4. The previous timer is stopped through `FocusManager`.
5. `DesktopManager` opens each distinct application, then each distinct URL using
   `Process.Start` with `UseShellExecute = true`. URLs use the default browser.
6. `FocusManager` starts the configured duration using the existing shared `TimerManager`.

Each application/URL failure is logged and isolated. A successful API response means
task activation completed; it does not promise every target opened. Matching a running
process by executable path avoids duplicate launches when process inspection is permitted.
Detection is best effort; browser tabs are not deduplicated across repeated starts.
Logs use `ILogger`, Console and Debug providers, with no Windows Event Log permission required.

The WPF app and API share singleton managers through Microsoft DI. State mutations run
on the WPF dispatcher; only foreground-window sampling runs in the background. Selecting
a regular workspace exits the task-specific rules and uses that workspace's rules.

Domain checks match exact hosts and subdomains, not title keywords. The current
`BrowserManager` cannot read browser URLs: it returns null. Task domain whitelists
are loaded, but domain evaluation is neutral until actual URL capture is implemented.
Whitelists evaluate focus; this feature does not block applications or network traffic.

Missing tasks return 404; invalid profiles/JSON return 400; unexpected failures return
500, all with `success: false` and an `error` message. Mutation requests require
`Content-Type: application/json`. The listener binds only to loopback, checks Host
and Origin, and does not enable CORS for arbitrary websites.

Creative Console can send the same POST request. The repository currently has no
Creative Console button implementation to wire directly.

## Verification

```powershell
dotnet run --project tests/CluckIn.App.SmokeTests
cd src/web
npm run build
npm test
```

Task tests cover settings ownership, launch order, failure isolation, task/timer
switching, missing task, invalid duration/path/URLs, domain boundaries, real local
HTTP requests, shared desktop state, error messages and request-origin restrictions.
Tests do not open real applications or browser tabs.

## Files added

- `src/app/Models/TaskProfile.cs`
- `src/app/Interfaces/ITaskManager.cs`
- `src/app/Interfaces/ITaskRepository.cs`
- `src/app/Interfaces/IDesktopManager.cs`
- `src/app/Interfaces/ISessionManager.cs`
- `src/app/Interfaces/IWhitelistManager.cs`
- `src/app/Managers/TaskManager.cs`
- `src/app/Managers/DesktopManager.cs`
- `src/app/Managers/SessionManager.cs`
- `src/app/Managers/WhitelistManager.cs`
- `src/app/Repositories/InMemoryTaskRepository.cs`
- `src/app/Services/TaskApiHost.cs`
- `src/web/src/components/TaskLauncher.tsx`
- `src/web/src/services/taskService.ts`
- `tests/CluckIn.App.SmokeTests/TaskChecks.cs`
- `docs/task-start.md`

## Files modified

- `src/app/App.xaml.cs`
- `src/app/CluckIn.App.csproj`
- `src/app/Interfaces/IFocusManager.cs`
- `src/app/Managers/ContextManager.cs`
- `src/app/Managers/FocusManager.cs`
- `src/app/Managers/WorkspaceManager.cs`
- `src/app/Models/WorkspaceProfile.cs`
- `src/app/Services/DesktopAgentFactory.cs`
- `src/app/Services/DesktopAgentService.cs`
- `src/app/ViewModels/MainViewModel.cs`
- `src/web/src/App.tsx`
- `src/web/vite.config.ts`
- `tests/CluckIn.App.SmokeTests/Program.cs`
