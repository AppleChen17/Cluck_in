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
| 5 | chick at desk (`start` / `paused` / `thinking` / `pet`), **or empty desk when chick is in the nest** | `GET /frames/{chicken}` if `displayKey` is 5; else `GET /frames/{deskEmpty}` |
| 6 | empty nest when chick is at the desk; chick-in-nest when `focused`; **blank when idle/feed** | `GET /frames/{nestIcon}` — skip / clear the key if `nestIcon` is `""` |

When `displayKey` is **6** (`focused`): push `deskEmpty` to key 5 and `chicken` to key 6. When `displayKey` is **5**: push `chicken` to key 5; if `nestIcon` is empty, clear key 6, otherwise push `nestIcon`.

This module does not own keys 1–4 or 7–9.

---

## Frame rules (Main MUST follow)

- Files are **64×128** PNG, RGBA, sprite bottom-aligned. Hearts sit above the chick.
- Draw the **entire** bitmap into the square key. Do **not** crop the top half.
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
  "chickenUrl": "/frames/idle_00.png"
}
```

| Field | Type | Rule |
| --- | --- | --- |
| `mood` | enum | `idle` \| `start` \| `focused` \| `thinking` \| `feed` \| `pet` \| `paused` \| `tired` |
| `chicken` | string | `^[a-z]+_[0-9]{2}\.png$` — draw this file on `displayKey` |
| `fps` | int ≥ 1 | advance rate for this mood |
| `loop` | bool | `false` = oneshot. `pet` returns to previous mood; `feed` always returns to `idle` |
| `displayKey` | 5 \| 6 | LCD key for `chicken`. `6` when the chick is in the nest (`focused`) |
| `nestIcon` | string | `nest_icon.png` when chick is at the desk in a session; same as `chicken` when in nest; **empty string** when `idle` / `feed` |
| `deskEmpty` | const | `desk_empty.png` — empty workstation, no milk; draw on key 5 when chick is in the nest |
| `feedCount` | int ≥ 0 | nest feed count |
| `chickenUrl` | string | `/frames/{chicken}` (API only; optional in schema) |
| `frameDir` | string | absolute frames directory |

`tired` exists in schema and frames; MVP should not send it.

There is **no** `happy` mood and **no** `SESSION_DONE` event.

---

## ChickenEvent

`POST /event` body. Schema: `shared/schemas/chicken-event.schema.json`. `additionalProperties` on the root is false.

```json
{ "type": "FOCUS_DEFAULT", "payload": {} }
```

| `type` | `payload` | Effect |
| --- | --- | --- |
| `tick` | `{}` | next frame; if oneshot expires, restore previous mood |
| `FOCUS_START` | `{}` | `mood=start` — at desk, no milk |
| `FOCUS_DEFAULT` | `{}` | `mood=focused` — in nest (專注預設) |
| `FOCUS_PAUSE` | `{}` | `mood=paused` — at desk, with milk |
| `STOP_FOCUS` | `{}` | `mood=idle` |
| `PET_CHICKEN` | `{}` | oneshot `pet`, then previous mood |
| `FEED_CHICKEN` | `{}` | oneshot `feed`, then `idle`; if `feedCount>0` then `feedCount-=1` |
| `SET_MOOD` | `{ "mood": "<enum>" }` | enter that mood (AI wait: `thinking`) |
| `SET_FEED` | `{ "count": 0 }` | set `feedCount` |

Use the `FOCUS_*` names in the table. `START`, `START_FOCUS`, `PAUSE_FOCUS`, and `RESUME_FOCUS` still work as aliases. Do not send `SESSION_DONE`.

---

## Mood table

| mood | fps | loop | Frames |
| --- | --- | --- | --- |
| `idle` | 2 | true | `idle_00` … `idle_03` — no nest |
| `start` | 3 | true | `start_00` … `start_03` — at desk, no milk, key 5 |
| `focused` | 2 | true | `focused_00` … `focused_04` (07_0–07_4 in nest, key 6) |
| `thinking` | 4 | true | `thinking_00` `thinking_01` — at desk, no milk, key 5 |
| `pet` | 4 | false | `pet_00` … `pet_11` — at desk, no milk |
| `paused` | 3 | true | `paused_00` … `paused_03` — at desk with milk, key 5 |
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
| sit at desk / session ready | `FOCUS_START` |
| pomodoro running (default) | `FOCUS_DEFAULT` |
| pomodoro paused | `FOCUS_PAUSE` |
| pomodoro resumed | `FOCUS_DEFAULT` |
| AI inference in progress | `SET_MOOD` + `{ "mood": "thinking" }` |
| AI done, still focusing | `FOCUS_DEFAULT` |
| focus session ends | `STOP_FOCUS` |
| user pets chicken | `PET_CHICKEN` |
| user feeds chicken | `FEED_CHICKEN` |

---

## Out of scope

- Drawing to Console hardware
- Running or calling Ollama
- Roaming onto other keys
- `happy` / session-complete celebration
