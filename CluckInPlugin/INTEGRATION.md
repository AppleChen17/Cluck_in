# CluckIn Logitech Integration

This document describes the Logitech MX Creative Console integration and how other CluckIn modules connect to it.

## 1. Ownership

The Logitech side is not a single file.

It owns the adapter between the physical MX Creative Console and the rest of CluckIn:

```text
MX Creative Console
-> Logi Options+
-> CluckInPlugin
-> Logitech Actions
-> MainController
-> InputEvent / integration boundary
-> App / Python services
```

The Logitech side owns:

- Physical key input
- Keypad labels and visual state
- Idle / Focus mode interaction
- AI Assist control state
- Task-selection interaction
- Timer-selection interaction
- Converting hardware input into explicit events
- Sending events to the app/backend
- Displaying app state such as countdown values

The Logitech side does not own:

- Gmail / Slack fetching
- Desktop monitoring
- AI worker loops
- AI policy enforcement
- Task database
- Whitelist / blocked-app logic
- Calendar integration
- Pet backend state

---

## 2. Current hardware status

The plugin builds successfully with .NET 10 and loads from the development link.

Verified physical flow:

```text
MX Creative Console
-> Logitech Action
-> MainController
-> semantic routing
```

Verified HTTP flow:

```text
MX Creative Console
-> MainController
-> IntegrationClient
-> HTTP POST
-> Python mock receiver
```

Verified events include:

```text
CHANGE_MODE
FEED_CHICKEN
PET_CHICKEN
AI_ASSIST_SUGGESTION
AI_ASSIST_ON
AI_ASSIST_OFF
```

---

## 3. Two independent states

There are two independent concepts.

### Session mode

```text
Idle
Focus
```

Controlled by Key 1.

### AI Assist level

```text
Off
Suggestion
On
```

Controlled by Key 3 while in Focus.

Do not merge AI Assist into the session mode.

---

## 4. No toggle workflow events

A physical key may cycle or switch state, but cross-module events must be explicit.

Do not use workflow events such as:

```text
TOGGLE_MODE
TOGGLE_AI
```

Do not implement meaning with:

```text
a = !a
```

Instead, send the desired result.

Examples:

```text
CHANGE_MODE { mode: "focus" }
CHANGE_MODE { mode: "idle" }

AI_ASSIST_OFF
AI_ASSIST_SUGGESTION
AI_ASSIST_ON
```

---

## 5. Key mapping

| Key | Focus | Idle | Event behavior |
|---|---|---|---|
| 1 | Change to Idle | Change to Focus | Send once on state change |
| 2 | Show message/popup screen | PET | One-shot PET_CHICKEN |
| 3 | AI Assist Off/Suggestion/On | Inventory / FEED | FEED_CHICKEN; consume only if available |
| 4 | Enter task selector | Pat count / PATS | Idle is read-only |
| 5 | START / PAUSE / RESUME | Chicken animation, no text | Existing Focus behavior |
| 6 | END | Successful feed count / FED | Idle is read-only |
| 7-9 | Focus timer selection/countdown | Cumulative HH / HR, MM / MIN, SS / SEC | Idle is read-only |

---

## 6. Key 1 - Idle / Focus

Key 1 changes the desired session mode.

```text
Idle
-> press Key 1
-> CHANGE_MODE { mode: "focus" }

Focus
-> press Key 1
-> CHANGE_MODE { mode: "idle" }
```

Example:

```json
{
  "type": "CHANGE_MODE",
  "source": "logitech",
  "timestamp": "2026-09-19T14:30:00+08:00",
  "payload": {
    "mode": "focus"
  },
  "metadata": {
    "keyId": 1
  }
}
```

One press causes one event.

---

## 7. Key 2 - One-shot UI action

### Focus

Key 2 opens or shows the relevant message screen / popup.

This is a one-shot UI request.

The current shared InputEvent schema does not yet contain the final event name for this workflow.

Until the team approves a shared event name:

```text
Focus + Key 2
-> handle locally / log as pending contract
-> do not invent a shared schema event
```

Possible future names must be approved by the shared-contract owner, for example:

```text
SHOW_MESSAGES
OPEN_MESSAGE_SCREEN
```

### Idle

Key 2 pets the chicken.

```text
Key 2
-> PET_CHICKEN
```

Each press sends one event.

---

## 8. Key 3 - AI Assist policy

In Focus, Key 3 controls how much authority AI has. In Idle, each press sends
`FEED_CHICKEN` once and displays inventory above `FEED`.

It does not start or stop the AI engine process.

### Off

```text
AI Assist = Off
```

Meaning:

- Background message fetching may continue
- Desktop monitoring may continue
- AI must not automatically send messages
- AI must not automatically execute commands
- AI should not proactively affect the workflow

### Suggestion

```text
AI Assist = Suggestion
```

Meaning:

- AI may analyze context
- AI may generate suggested replies
- AI may generate suggested actions
- AI must not automatically send messages
- AI must not automatically execute commands
- User confirmation is required before side effects

### On

```text
AI Assist = On
```

Meaning:

- AI may analyze context
- AI may automatically reply when app policy allows it
- AI may automatically execute approved work/actions when app policy allows it
- Background work continues in App/Python services

### State transitions

Current UI cycle:

```text
Off
-> Suggestion
-> On
-> Off
```

Each state change sends exactly one explicit event:

```text
AI_ASSIST_SUGGESTION
AI_ASSIST_ON
AI_ASSIST_OFF
```

Do not repeatedly send AI_ASSIST_ON while the system remains On.

---

## 9. Background services

The following are long-running services and should not depend on repeated Logitech key calls.

### Message fetcher

```text
Focus mode
-> background loop
-> Gmail / Slack
-> normalized ExternalMessage
```

### Desktop manager

```text
continuously running
-> reads desktop/app context
-> updates working context
```

### Animation

```text
App state / mode
-> determines visual state
```

### AI engine

AI is triggered by background information and context.

```text
Message Fetcher -----\
                      -> App / Orchestrator
Desktop Manager -----/          |
                                 v
                             AIRequest
                                 |
                                 v
                             AI Engine
                                 |
                                 v
                            AIDecision
                                 |
                                 v
                     apply AI Assist policy
                       /        |        \
                     Off   Suggestion    On
```

The long-running worker belongs to Python/App, not the Logitech Plugin Service.

---

## 10. Key 4 - Task selection with dial

Pressing Key 4 should enter task-selection mode.

```text
press Key 4
-> enter task selector
-> rotate dial to move through tasks
-> confirm selected task
-> send SELECT_TASK once
```

Do not send SELECT_TASK immediately when Key 4 is first pressed.

Recommended final event:

```json
{
  "type": "SELECT_TASK",
  "source": "logitech",
  "timestamp": "2026-09-19T14:30:00+08:00",
  "payload": {
    "currentTask": "Implement Logitech Actions SDK"
  },
  "metadata": {
    "keyId": 4
  }
}
```

The app owns:

```text
currentTask
allowedApps
blockedApps
```

The Logitech plugin does not own whitelist logic.

---

## 11. Keys 7-9 - Timer selection and countdown

Keys 7-9 form a timer UI.

They are not three unrelated backend features.

Expected interaction:

```text
timer area
+ dial
-> choose focus duration
-> confirm
-> START_FOCUS { durationSeconds: ... }
-> App TimerManager owns countdown
-> App exposes remaining time
-> Logitech displays countdown
```

Example:

```json
{
  "type": "START_FOCUS",
  "source": "logitech",
  "timestamp": "2026-09-19T14:30:00+08:00",
  "payload": {
    "durationSeconds": 1500
  },
  "metadata": {
    "input": "timer-dial"
  }
}
```

The shared event model already supports:

```text
START_FOCUS
STOP_FOCUS
PAUSE_FOCUS
RESUME_FOCUS
```

The app-side TimerManager is the authoritative timer.

Logitech should display timer state rather than running a separate authoritative countdown.

---

## 12. Event frequency rule

Use this rule:

```text
Key 1
-> send once when target mode changes

Key 2
-> send once per press

Key 3
-> Focus: send once when AI Assist level changes
-> Idle: send FEED_CHICKEN once per press

Key 4
-> no event while browsing tasks
-> send SELECT_TASK once after confirmation

Timer dial
-> no event while browsing duration
-> send START_FOCUS once after confirmation

Countdown
-> Logitech does not repeatedly send timer events
-> App provides current state for display
```

---

## 13. Shared InputEvent shape

Logitech outbound events should follow the team InputEvent structure:

```json
{
  "type": "...",
  "source": "logitech",
  "timestamp": "...",
  "payload": {},
  "metadata": {}
}
```

Required fields:

```text
type
source
timestamp
payload
```

Use metadata for hardware-specific debugging information such as:

```json
{
  "metadata": {
    "keyId": 3
  }
}
```

Do not put hardware-only fields at the top level if they are not part of the shared schema.

---

## 14. Current contract gaps

Two workflows still need team-approved shared event names.

### Focus + Key 2

```text
show/open message screen
```

No final InputEvent name exists yet.

### AI Assist

Current proposed names are:

```text
AI_ASSIST_OFF
AI_ASSIST_SUGGESTION
AI_ASSIST_ON
```

These are implemented/tested on the Logitech integration path, but they are not yet present in the current shared InputEvent enum.

Do not modify shared schemas from the Logitech branch without team agreement.

---

## 15. Python integration

Current local development endpoint:

```text
POST http://127.0.0.1:8765/input-event
```

Logitech uses `IntegrationClient` to send InputEvent JSON.

The endpoint can later be pointed at the real app/backend.

Recommended environment variable:

```text
CLUCKIN_INPUT_EVENT_URL
```

The Python/App side should accept explicit target-state events rather than inferring toggle state.

---

## 16. Local testing without Logitech hardware

Python teammates do not need the physical MX Creative Console.

Example: simulate Focus mode.

```powershell
$body = @{
    type = "CHANGE_MODE"
    source = "logitech"
    timestamp = (Get-Date).ToString("o")
    payload = @{
        mode = "focus"
    }
    metadata = @{
        keyId = 1
    }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod `
    -Method Post `
    -Uri "http://127.0.0.1:8765/input-event" `
    -ContentType "application/json" `
    -Body $body
```

Example: simulate AI Suggestion mode.

```powershell
$body = @{
    type = "AI_ASSIST_SUGGESTION"
    source = "logitech"
    timestamp = (Get-Date).ToString("o")
    payload = @{}
    metadata = @{
        keyId = 3
    }
} | ConvertTo-Json -Depth 5
```

---

## 17. Logitech-owned files

Current Logitech implementation is spread across multiple files.

```text
CluckInPlugin/
├─ src/
│  ├─ Actions/
│  │  ├─ AutomationCommand.cs
│  │  ├─ CounterCommand.cs
│  │  ├─ MessageCommand.cs
│  │  └─ TaskCommand.cs
│  ├─ Models/
│  │  ├─ CluckInAction.cs
│  │  ├─ CluckInEvent.cs
│  │  └─ InputEventRequest.cs
│  ├─ AIAssistMode.cs
│  ├─ CluckInMode.cs
│  ├─ MainController.cs
│  └─ IntegrationClient.cs
├─ tools/
│  └─ mock_receiver.py
└─ INTEGRATION.md
```

The generated Logitech SDK project also contains plugin bootstrap, helper, package metadata, and project files.

---

## 18. Current verified milestones

### Hardware routing

```text
MX Creative Console
-> MainController
```

PASS.

### Mode switching

```text
Idle <-> Focus
```

PASS.

### AI Assist state

```text
Off -> Suggestion -> On -> Off
```

PASS.

### HTTP integration

```text
Logitech
-> IntegrationClient
-> Python mock receiver
```

PASS.

### Verified events

```text
CHANGE_MODE
FEED_CHICKEN
PET_CHICKEN
AI_ASSIST_SUGGESTION
AI_ASSIST_ON
AI_ASSIST_OFF
```

PASS on the local transport path.

---

## 19. Next implementation work

Only modify `CluckInPlugin/`.

Next priorities:

1. Merge/read the latest app contract safely.
2. Align AI Assist event names with the team contract.
3. Align Focus Key 2 message-screen event with the team contract.
4. Implement Key 4 task-selection state.
5. Connect dial rotation/confirmation to task selection.
6. Emit SELECT_TASK only after confirmation.
7. Implement timer-duration selection.
8. Connect dial rotation/confirmation to timer selection.
9. Emit START_FOCUS only after confirmation.
10. Read app timer state and update Keys 7-9 countdown.
11. Keep App/Python as the owner of background services and timer state.

---

## 20. Git scope

Logitech work should modify only:

```text
CluckInPlugin/**
```

Treat these as read-only unless the owning teammate asks for a change:

```text
shared/**
src/app/**
src/ai-engine/**
src/actions/**
src/external/**
src/web/**
docs/**
```

---

## 21. Short explanation for teammates

> I own the Logitech adapter, not one C# file. The MX Creative Console is already connected to MainController and can send normalized events to Python over HTTP. Key 1 controls Idle/Focus, Key 2 is a one-shot UI/pet action, Key 3 controls the AI Assist policy Off/Suggestion/On and sends one event only when that policy changes, Key 4 will use the dial for task selection, and Keys 7-9 will use the dial for focus-duration selection and countdown display. Background message fetching, desktop monitoring, AI processing, and the authoritative timer stay in App/Python services.


## Teammate chicken preview (Idle verification)

From the repository root:

```powershell
python -m pip install -r src/chicken/requirements.txt
python -B src/chicken/test_states.py
python -B src/chicken/preview.py
```

The existing Tk preview uses `ChickenAnim` and `src/chicken/frames` directly.
It starts in `idle`, looping `idle_00.png` through `idle_03.png` at 2 fps.
The preview's IDLE button sends `STOP_FOCUS` to return to that sequence.
The test script prints state transitions; it is not an assertion-based test suite.

The Tk preview is standalone. Idle Key 5 now uses the existing chicken API:

```powershell
python -m uvicorn api:app --app-dir src/chicken --host 127.0.0.1 --port 8001
```

Keep that service running. `IdleChickenAnimation` sends `SET_MOOD` with
`mood=idle` on Idle entry, forwards local pet/feed actions, and sends `tick`
at the `fps` returned by Python. Python alone selects the frames and handles
one-shot return states. The adapter downloads `/frames/{chicken}` and
invalidates only Key 5. It stops ticking in Focus and cancels on plugin unload.
`CLUCKIN_CHICKEN_URL` can override the API base URL (include the trailing slash).
Do not run another tick producer against the same API instance.

Idle Key 5 has no timer label. Frames use the common API `idleCrop` with
nearest-neighbor scaling, preserving every visible pixel including hearts. No assets are copied into the
plugin. If the API is unavailable, the adapter logs once, retries, and resets
to Idle after reconnecting. It retains the last frame until a new one arrives.

Focus Key 5 still uses the existing desk/progress images and START/PAUSE/RESUME
labels. The chicken view's nest fields are not applied in Idle. Keys 6?9 show
statistics only in Idle; their Focus behavior is unchanged. Existing input events to port 8765 remain unchanged; that receiver
must not also forward pet/feed to this API, or it would deliver each event twice.

Integration test (after building CluckInPlugin):

```powershell
# Supply a Python interpreter with src/chicken/requirements.txt installed.
dotnet run --project CluckInPlugin/tests/IdleChickenIntegration -- python
```

The test launches an isolated chicken API on a free local port, verifies the
real state machine and Key 5 frame updates, and shuts down its API afterward.


## Idle dashboard state and verification

Key 1 displays FOCUS as its destination; Key 2 displays PET. Keys 3/4/6 use
native dynamic command text for a number and FEED/PATS/FED. Keys 7/8/9 use
native text for cumulative HH/HR, MM/MIN, SS/SEC. Until an API snapshot arrives,
statistics show `--` rather than inventing a zero balance. Keys 4 and 6?9 do
nothing when pressed in Idle.

All totals come from Python; the plugin caches only its display snapshot.
See `src/chicken/INTEGRATION.md` for persistence and earning rules. Actual focus
progress requires the desktop app; the PAD countdown mirror alone grants no
credit. The existing port 8765 receiver has not been rewritten for this layout.

Physical check: enter Idle and verify all nine displays. Press PET and check
PATS increases. Complete a desktop focus session to earn one feed. In Idle,
press FEED: inventory decreases, FED increases, and the feed sequence plays.
At zero inventory, another press changes neither count nor animation. Switch
to Focus and verify the existing controls and timer selection still work.
