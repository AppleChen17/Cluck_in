# Chicken module — integration spec for Main / logitech / AI agents

Copy these into the monorepo:

- `src/chicken/` — FastAPI service, state machine, PNG frames
- `shared/schemas/chicken-view.schema.json`
- `shared/schemas/chicken-event.schema.json`
- `shared/fixtures/` — examples only; runtime does not read them

Authoritative contracts: the two schemas above. This file restates them for implementers.

This module does **not** push pixels to the MX Creative Console. Main must fetch PNGs and draw keys via the Logitech SDK. It does **not** run Ollama. Port **8000** is `src/ai-engine`. Port **8001** is this chicken service. Do not reuse 8000.

Sprites are **already composited**. Do not layer desk, nest, stump, egg, or milk at runtime. Just blit the named PNG.

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
| `GET` | `/frames/{filename}` | static PNG (`idle_00.png`, `nest_icon.png`, `house_00.png`, `desk_empty.png`, …) |

Unknown `type` or `mood` → HTTP 400.

---

## Console key mapping

Nine LCD keys, 1–9, left-to-right, top-to-bottom:

```
1 2 3
4 5 6
7 8 9
```

This module owns **4, 5, 6** only.

| Key | When | What to draw |
| --- | --- | --- |
| 4 | `houseIcon` is non-empty (`idle` / `feed`) | `GET /frames/{houseIcon}` — stump + wobbling egg, already composited |
| 4 | `houseIcon` is `""` | **clear the key** |
| 5 | `displayKey` is 5 | `GET /frames/{chicken}` |
| 5 | `displayKey` is 6 | `GET /frames/{deskEmpty}` — empty desk, no milk |
| 6 | `nestIcon` is non-empty | `GET /frames/{nestIcon}` — empty nest, or the chick-in-nest frame when `focused` |
| 6 | `nestIcon` is `""` | **clear the key** (MVP: only unused `tired`) |

Do not own keys 1–3 or 7–9.

---

## Layout by mood

| mood | key 4 | key 5 | key 6 |
| --- | --- | --- | --- |
| `idle` | stump + egg (`house_00`…`house_08`) | chick, no desk | empty nest |
| `feed` | stump + egg (same as idle) | eating chick, no desk | empty nest |
| `start` | **clear** | chick at desk, no milk | empty nest |
| `thinking` | **clear** | chick at desk + question mark, no milk | empty nest |
| `pet` | **clear** | chick at desk, no milk | empty nest |
| `paused` | **clear** | chick at desk **with milk** | empty nest |
| `focused` | **clear** | empty desk, no milk | chick **in** nest |

`displayKey` is **6** only for `focused`. It is **5** for every other MVP mood.

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
  "nestIcon": "nest_icon.png",
  "deskEmpty": "desk_empty.png",
  "houseIcon": "house_00.png",
  "houseFps": 8,
  "feedCount": 0,
  "chickenUrl": "/frames/idle_00.png"
}
```

| Field | Type | Rule |
| --- | --- | --- |
| `mood` | enum | `idle` \| `start` \| `focused` \| `thinking` \| `feed` \| `pet` \| `paused` \| `tired` |
| `chicken` | string | `^[a-z]+_[0-9]{2}\.png$` — draw this file on `displayKey` |
| `fps` | int ≥ 1 | chick advance rate for this mood |
| `loop` | bool | `false` = oneshot. `pet` returns to previous mood; `feed` always returns to `idle` |
| `displayKey` | 5 \| 6 | LCD key for `chicken`. `6` only when `focused` |
| `nestIcon` | string | `nest_icon.png` on key 6 when idle / feed / chick at desk; same as `chicken` when `focused`; empty only if unused |
| `deskEmpty` | const | `desk_empty.png` — empty workstation, no milk; draw on key 5 when `displayKey` is 6 |
| `houseIcon` | string | `house_00.png` … `house_08.png` on key 4 when `idle` / `feed`; **empty string** otherwise (clear key 4) |
| `houseFps` | int ≥ 0 | `8` when key 4 is showing; `0` when blank |
| `feedCount` | int ≥ 0 | remaining feed count |
| `chickenUrl` | string | `/frames/{chicken}` (API only; optional in schema) |
| `frameDir` | string | absolute frames directory |

House cycle when `houseIcon` is showing: `house_00` … `house_07` once each (egg `01_0`–`01_7`), then `house_08` (egg `01_8`) held for **8** ticks, then loop. `house_icon.png` is a copy of `house_00.png`; prefer `house_00.png` … `house_08.png`.

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
| `tick` | `{}` | next frame; if oneshot expires, restore previous mood. When `houseFps > 0`, also advances the house cycle |
| `FOCUS_START` | `{}` | `mood=start` — at desk, no milk; clear key 4 |
| `FOCUS_DEFAULT` | `{}` | `mood=focused` — in nest; empty desk on key 5; clear key 4 |
| `FOCUS_PAUSE` | `{}` | `mood=paused` — at desk with milk; clear key 4 |
| `STOP_FOCUS` | `{}` | `mood=idle` — stump + egg on 4, chick on 5, empty nest on 6 |
| `PET_CHICKEN` | `{}` | oneshot `pet`, then previous mood |
| `FEED_CHICKEN` | `{}` | oneshot `feed`, then `idle`; keys 4 and 6 stay (stump + nest); if `feedCount>0` then `feedCount-=1` |
| `SET_MOOD` | `{ "mood": "<enum>" }` | enter that mood (AI wait: `thinking`) |
| `SET_FEED` | `{ "count": 0 }` | set `feedCount` |

Use the `FOCUS_*` names in the table. `START`, `START_FOCUS`, `PAUSE_FOCUS`, and `RESUME_FOCUS` still work as aliases. Do not send `SESSION_DONE`.

---

## Mood table

| mood | fps | loop | Chick frames | Keys 4 / 6 |
| --- | --- | --- | --- | --- |
| `idle` | 2 | true | `idle_00` … `idle_03` | 4 = stump+egg (`houseFps` 8); 6 = empty nest |
| `feed` | 4 | false | `feed_00` … `feed_17` | same as idle; then back to `idle` |
| `start` | 3 | true | `start_00` … `start_03` | 4 clear; 6 = empty nest |
| `focused` | 2 | true | `focused_00` … `focused_04` | 4 clear; 6 = chick in nest; 5 = empty desk |
| `thinking` | 4 | true | `thinking_00` `thinking_01` | 4 clear; 6 = empty nest |
| `pet` | 4 | false | `pet_00` … `pet_11` | 4 clear; 6 = empty nest; then previous mood |
| `paused` | 3 | true | `paused_00` … `paused_03` | 4 clear; 6 = empty nest; milk on desk |

---

## Main loop (required)

1. Start this service on **8001**. AI engine stays on **8000**.
2. Poll `GET /view` (or use the `POST /event` response).
3. Draw keys from the snapshot:
   - key 4: push `houseIcon`, or **clear** if `houseIcon` is `""`
   - if `displayKey` is 5: push `chicken` to key 5; push `nestIcon` to key 6 (or clear if empty)
   - if `displayKey` is 6: push `deskEmpty` to key 5; push `chicken` to key 6
4. Tick rate:
   - if `houseFps > 0`: every `1/houseFps` seconds (`8` fps while idle / feed)
   - else: every `1/fps` seconds
   - `POST {"type":"tick","payload":{}}` then redraw keys **4, 5, and 6**
   - the service keeps the chick on `fps` even when ticks arrive at `houseFps`
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
- Runtime compositing of desk / nest / stump / egg / milk
