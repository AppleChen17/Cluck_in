# CluckIn Demo Preparation SOP — 2026-09-20

## Current validated demo status

- Key 2: physical key -> real Slack non-urgent -> External -> Main -> AI -> Focus temporary list -> summary popup: **PASS**
- Key 3 Suggestion: physical key -> Suggestion mode -> real Slack urgent -> External -> Main -> AI -> suggested reply popup: **PASS**
- Key 4: physical key -> `SELECT_TASK` -> Main -> browser opens existing Dashboard / TaskLauncher: **PASS**
- `CluckIn.sln`: **build success**
- `CluckInPlugin.csproj`: **build success**

> Key 3 `On` auto-reply path is not the main validated demo path. Use **Suggestion** mode for the demo.
>
> Key 4 currently proves opening the existing Task Dashboard. If the in-memory task repository is empty, the TaskLauncher may correctly show no tasks.

---

## Demo startup order

Keep each long-running service in its own terminal.

### 1. AI Engine — port 8000

Start the team's existing AI Engine command.

Health check:

```powershell
Invoke-RestMethod "http://127.0.0.1:8000/health"
```

Expected: `status = ok`.

### 2. External service — port 8100

```powershell
cd C:\dev\Cluck_in

.\.venv\Scripts\python.exe -m uvicorn app:app `
    --app-dir src\external `
    --port 8100 `
    --workers 1
```

Health check:

```powershell
Invoke-RestMethod "http://127.0.0.1:8100/health" |
    ConvertTo-Json -Depth 10
```

For the current demo, keep:

```text
EXTERNAL_SEND_DRY_RUN=true
```

Slack should report enabled/connected before the live-message demo.

### 3. Main Desktop App — port 5180

```powershell
cd C:\dev\Cluck_in
dotnet run --project .\src\app\CluckIn.App.csproj
```

Check:

```powershell
Get-NetTCPConnection `
    -LocalPort 5180 `
    -State Listen `
    -ErrorAction SilentlyContinue
```

### 4. Web Dashboard — port 5173

Use `npm.cmd` because PowerShell may block `npm.ps1`.

```powershell
cd C:\dev\Cluck_in\src\web

npm.cmd run dev -- --host 127.0.0.1 --port 5173
```

Check:

```powershell
Get-NetTCPConnection `
    -LocalPort 5173 `
    -State Listen `
    -ErrorAction SilentlyContinue |
    Format-Table LocalAddress,LocalPort,OwningProcess
```

Expected address:

```text
127.0.0.1
```

If it only shows `::1`, stop that Vite process and restart with the explicit `--host 127.0.0.1` command above.

Dashboard:

```text
http://127.0.0.1:5173/#/dashboard
```

### 5. Logitech plugin

After all services are ready:

```powershell
Start-Process "loupedeck:plugin/CluckIn/reload"
Start-Sleep -Seconds 3
```

Check plugin log:

```powershell
Get-Content "$env:LOCALAPPDATA\Logi\LogiPluginService\Logs\plugin_logs\CluckIn.log" -Tail 15
```

Expected: plugin loaded and `11 dynamic actions loaded`.

---

## 30-second pre-demo health check

```powershell
Write-Host "===== AI 8000 ====="
Invoke-RestMethod "http://127.0.0.1:8000/health"

Write-Host "`n===== EXTERNAL 8100 ====="
Invoke-RestMethod "http://127.0.0.1:8100/health" |
    ConvertTo-Json -Depth 10

Write-Host "`n===== MAIN 5180 ====="
Get-NetTCPConnection `
    -LocalPort 5180 `
    -State Listen `
    -ErrorAction SilentlyContinue |
    Select-Object LocalAddress,LocalPort

Write-Host "`n===== WEB 5173 ====="
Get-NetTCPConnection `
    -LocalPort 5173 `
    -State Listen `
    -ErrorAction SilentlyContinue |
    Select-Object LocalAddress,LocalPort
```

Target:

```text
AI       8000  OK
External 8100  OK
Main     5180  OK
Web      5173  OK
Plugin loaded OK
```

---

## Recommended live demo flow

### Reset before presentation

Before the real demo, return to Idle and then enter Focus again so the Focus temporary message list starts clean.

Do not restart External between the reset and the live Slack demo unless required, because its message buffer/cursor state is in memory.

### Demo sequence

1. **Key 1 -> Focus**
   - Establish the Main Focus session.
   - Wait briefly for mode transition.

2. **Key 3 -> Suggestion**
   - Confirm the plugin log reports `AI assist mode changed to Suggestion` and `InputEvent sent: type=SET_AI_ASSIST_MODE, status=200`.

3. **Send one urgent Slack message**
   - Explain that the message is received, analyzed for urgency/context, and a suggested response is generated.
   - Allow several seconds for the real External -> Main -> AI path.
   - Expected: `Cluck In - Suggested reply` popup.

4. **Send one non-urgent Slack message**
   - Expected: no immediate interruption.
   - It is stored in the Focus temporary message list after analysis.

5. **Key 2 -> Messages**
   - Expected: AI summary popup containing the non-urgent Focus-session message.
   - Allow several seconds for summarization.

6. **Key 4 -> Task**
   - Expected: browser opens `http://127.0.0.1:5173/#/dashboard`.
   - Demonstrates physical Logitech key -> Main -> existing TaskLauncher.
   - If no tasks exist, an empty list is expected because the current repository is in-memory.

7. **Timer keys**
   - Demonstrate the already integrated countdown/timer behavior separately.

---

## Timing notes

Key 2 and Key 3 are real end-to-end flows and are not instant.

- Key 3 Suggestion: allow several seconds after the Slack message.
- Key 2 summary: allow several seconds after the non-urgent message has been analyzed, plus summary generation.
- Key 4 Dashboard: should be nearly immediate when ports 5180 and 5173 are healthy.

Use the AI-processing explanation while waiting instead of pressing the key repeatedly.

---

## Fast troubleshooting

### Key press log shows HTTP 200 but Dashboard says connection refused

```powershell
Get-NetTCPConnection -LocalPort 5173 -State Listen
```

If Vite is bound only to `::1`, restart it with:

```powershell
npm.cmd run dev -- --host 127.0.0.1 --port 5173
```

### PowerShell blocks npm.ps1

Do not change the machine-wide execution policy just for the demo. Use:

```powershell
npm.cmd run dev -- --host 127.0.0.1 --port 5173
```

### Key 2 returns no messages

Verify that Main was already running when Focus mode was entered, a non-urgent Slack message arrived after entering the current Focus session, and enough time was allowed for analysis before pressing Key 2.

### Key 3 urgent message does not show a suggestion

```powershell
Invoke-RestMethod "http://127.0.0.1:8100/health" |
    ConvertTo-Json -Depth 10

Get-Content "$env:LOCALAPPDATA\Logi\LogiPluginService\Logs\plugin_logs\CluckIn.log" -Tail 15
```

Do not restart every service before checking the exact failure point.

---

## Security / demo hygiene

- Never paste Gmail App Passwords, Slack tokens, or other credentials into slides, projected terminals, logs, or chat.
- Keep secrets only in the local ignored `.env`.
- Keep `EXTERNAL_SEND_DRY_RUN=true` unless the team explicitly decides to demonstrate real sending.
- Close unrelated terminals/windows before presenting.
- Reset the Focus session before the official run so Key 2 does not summarize old debug messages.

---

## Demo-ready checkpoint

```text
Key2 real Slack summary       PASS
Key3 Suggestion real Slack    PASS
Key4 Task Dashboard           PASS
Solution build                PASS
Plugin build                  PASS
```

Next development phase after this checkpoint: **Idle keys**.
