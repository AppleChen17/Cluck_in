# Chicken module — integration spec for Main / logitech / AI agents

Copy these two trees into the monorepo:

- `src/chicken/` — FastAPI service, state machine, PNG frames
- `shared/schemas/chicken-view.schema.json`
- `shared/schemas/chicken-event.schema.json`
- `shared/fixtures/` — examples only; runtime does not read them

Authoritative contracts: the two schemas above. This file restates them for implementers.

This module does **not** push pixels to the MX Creative Console. Main must fetch PNGs and draw keys via the Logitech SDK. It does **not** run Ollama. Port **8000** is `src/ai-engine`. Port **8001** is this chicken service. Do not reuse 8000.

---

## Run

```powershell
pip install -r src/chicken/requirements.txt
python -m uvicorn api:app --app-dir src/chicken --host 127.0.0.1 --port 8001
```

Keep the process running. Base URL: `http://127.0.0.1:8001`

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | `{ "ok": true }` |
| `GET` | `/view` | current `ChickenView` |
| `POST` | `/event` | apply `ChickenEvent`; response is `ChickenView` |
| `GET` | `/frames/{filename}` | static PNG (`idle_00.png`, `nest_icon.png`, …) |

Unknown `type` or `mood` → HTTP 400.

---

## Console key mapping

Nine LCD keys, 1–9, left-to-right, top-to-bottom:

```
1 2 3
4 5 6
7 8 9
```

| Key | Asset | Source field |
| --- | --- | --- |
| 5 | chicken, **or empty desk when `mood=paused`** | `GET /frames/{chicken}` if `displayKey` is 5; else `GET /frames/{deskEmpty}` |
| 6 | empty nest during focus session; chick-in-nest when paused; **blank when idle/feed** | `GET /frames/{nestIcon}` — skip / clear the key if `nestIcon` is `""` |

When `displayKey` is **6** (`mood=paused`): push `deskEmpty` to key 5 and `chicken` to key 6. When `displayKey` is **5**: push `chicken` to key 5; if `nestIcon` is empty, clear key 6, otherwise push `nestIcon`.

This module does not own keys 1–4 or 7–9.

---

## Frame rules (Main MUST follow)

- Files are **64×128** PNG, RGBA, sprite bottom-aligned. Hearts sit above the chick.
- Preserve all visible content, including hearts. Idle consumers may use `idleCrop`, one transparent-padding crop measured across every idle/pet/feed frame. Never crop or rescale each frame independently.
- Scale with **nearest-neighbor** only.
- Do not require 118×118 sources. Fit 64×128 onto the key.

---

## ChickenView

Returned by `GET /view` and `POST /event`. Schema: `shared/schemas/chicken-view.schema.json`.

```json
{
  "mood": "idle",
  "chicken": "idle_00.png",
  "fps": 2,
  "loop": true,
  "frameDir": "<absolute path to src/chicken/frames>",
  "displayKey": 5,
  "nestIcon": "",
  "deskEmpty": "desk_empty.png",
  "feedCount": 0,
  "patCount": 0,
  "successfulFeedCount": 0,
  "totalFocusSeconds": 0,
  "idleCrop": { "x": 7, "y": 76, "width": 49, "height": 52 },
  "chickenUrl": "/frames/idle_00.png"
}
```

| Field | Type | Rule |
| --- | --- | --- |
| `mood` | enum | `idle` \| `focused` \| `thinking` \| `feed` \| `pet` \| `paused` \| `tired` |
| `chicken` | string | `^[a-z]+_[0-9]{2}\.png$` — draw this file on `displayKey` |
| `fps` | int ≥ 1 | advance rate for this mood |
| `loop` | bool | `false` = oneshot. `pet` returns to previous mood; `feed` always returns to `idle` |
| `displayKey` | 5 \| 6 | LCD key for `chicken`. `6` only when `mood=paused` (chick in nest) |
| `nestIcon` | string | `nest_icon.png` during focus session (`focused` / `thinking` / `pet`); same as `chicken` when paused; **empty string** when `idle` / `feed` (no nest on key 6) |
| `deskEmpty` | const | `desk_empty.png` — empty workstation; draw on key 5 when paused |
| `feedCount` | int ≥ 0 | nest feed count |
| `chickenUrl` | string | `/frames/{chicken}` (API only; optional in schema) |
| `frameDir` | string | absolute frames directory |

`tired` exists in schema and frames; MVP should not send it.

There is **no** `happy` mood and **no** `SESSION_DONE` event.

---

## ChickenEvent

`POST /event` body. Schema: `shared/schemas/chicken-event.schema.json`. `additionalProperties` on the root is false.

```json
{ "type": "START_FOCUS", "payload": {} }
```

| `type` | `payload` | Effect |
| --- | --- | --- |
| `tick` | `{}` | next frame; if oneshot expires, restore previous mood |
| `START_FOCUS` | `{}` | `mood=focused` |
| `PAUSE_FOCUS` | `{}` | `mood=paused` (chick loops in nest on key 6) |
| `RESUME_FOCUS` | `{}` | `mood=focused` |
| `STOP_FOCUS` | `{}` | `mood=idle` |
| `PET_CHICKEN` | `{}` | oneshot `pet`, then previous mood |
| `FEED_CHICKEN` | `{}` | With inventory: consume one, count a successful feed, play `feed`, return to `idle`. At zero: no animation or count change. |
| `SET_MOOD` | `{ "mood": "<enum>" }` | enter that mood (AI wait: `thinking`) |
| `SET_FEED` | `{ "count": 0 }` | set `feedCount` |

Use the uppercase `type` values in the table. Do not send `SESSION_DONE`.

---

## Mood table

| mood | fps | loop | Frames |
| --- | --- | --- | --- |
| `idle` | 2 | true | `idle_00` … `idle_03` — no nest |
| `focused` | 3 | true | `focused_00` … `focused_03` |
| `thinking` | 4 | true | `thinking_00` `thinking_01` |
| `pet` | 4 | false | `pet_00` … `pet_11` |
| `paused` | 2 | true | `paused_00` … `paused_04` (07_0–07_4 in nest, key 6) |
| `feed` | 4 | false | `feed_00` … `feed_17` — idle oneshot, no nest |

---

## Main loop (required)

1. Start this service on **8001**. AI engine stays on **8000**.
2. Poll `GET /view` (or use the `POST /event` response).
3. If `displayKey` is 5: push `chicken` to key 5; if `nestIcon` is empty, clear key 6, else push `nestIcon` to key 6. If `displayKey` is 6: push `deskEmpty` to key 5, `chicken` to key 6.
4. Every `1/fps` seconds, `POST {"type":"tick","payload":{}}` and redraw `displayKey`.
5. Session / input mapping:

| App event | Chicken `type` |
| --- | --- |
| focus session starts | `START_FOCUS` |
| pomodoro paused mid-session | `PAUSE_FOCUS` |
| pomodoro resumed | `RESUME_FOCUS` |
| AI inference in progress | `SET_MOOD` + `{ "mood": "thinking" }` |
| AI done, still focusing | `START_FOCUS` or `SET_MOOD` + `{ "mood": "focused" }` |
| focus session ends | `STOP_FOCUS` |
| user pets chicken | `PET_CHICKEN` |
| user feeds chicken | `FEED_CHICKEN` |

---

## Out of scope

- Drawing to Console hardware
- Running or calling Ollama
- Roaming onto other keys
- `happy` / session-complete celebration


## Persistent Idle statistics and food economy

The API owns the single canonical inventory/statistics store at
`%LOCALAPPDATA%/CluckIn/chicken-state.json` on Windows. Override with
`CLUCKIN_CHICKEN_STATE_PATH`. Run one API worker per state file. The standalone
preview keeps temporary in-memory demo state and does not consume saved inventory.

- `feedCount`: remaining inventory (the existing field, now persistent).
- `patCount`: cumulative accepted `PET_CHICKEN` actions, counted when started.
- `successfulFeedCount`: cumulative feeds that consumed an item.
- `totalFocusSeconds`: cumulative actual desktop-timer seconds, excluding pauses.
- `idleCrop`: API-only shared rectangle for all idle/pet/feed frames with up to
  two pixels of transparent margin. Original PNGs are unchanged.

`RECORD_FOCUS` accepts `sessionId` (stable string), `seconds` (non-negative integer)
and `completed` (boolean). Only progress beyond the session's prior maximum is
added. First completion awards one feed item, as specified in PRODUCT_SPEC_MVP.
Repeated/older receipts cannot duplicate time or rewards, even after restart.
Early-stopped sessions count actual time but earn no feed. Fractional seconds are
rounded down per session. Historical totals before this store existed cannot be
reconstructed and start at zero. There is no second C# inventory or total counter.

The desktop app observes its existing timer through `ProgressTrackingTimer`.
`ChickenProgressReporter` queues unacknowledged measurements durably in
`%LOCALAPPDATA%/CluckIn/focus-outbox.json`, retrying when the API becomes available.
`CLUCKIN_FOCUS_OUTBOX_PATH` isolates test delivery state. This observer does not
change timer transition rules. The Logitech countdown mirror does not award credit.

The Idle PAD layout uses native text for FOCUS, PET, inventory/FEED, pats/PATS,
successful feeds/FED, and cumulative HH/HR, MM/MIN, SS/SEC. Key 5 remains the
teammate animation. This Idle layout supersedes the earlier preview key layout;
Focus controls are unchanged.

Tests:

```powershell
python -m pip install -r src/chicken/requirements-test.txt
python -B src/chicken/test_states.py
python -B -m unittest discover -s src/chicken -p test_idle_state.py -v
```
