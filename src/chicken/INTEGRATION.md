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
| 5 | current chicken frame | `GET /frames/{chicken}` |
| 6 | nest (empty nest, `00_3`) | `GET /frames/{nestIcon}` plus overlay `feedCount` if Main wants a number |

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
  "nestIcon": "nest_icon.png",
  "feedCount": 0,
  "chickenUrl": "/frames/idle_00.png"
}
```

| Field | Type | Rule |
| --- | --- | --- |
| `mood` | enum | `idle` \| `focused` \| `thinking` \| `feed` \| `pet` \| `tired` |
| `chicken` | string | `^[a-z]+_[0-9]{2}\.png$` — key 5 filename |
| `fps` | int ≥ 1 | advance rate for this mood |
| `loop` | bool | `false` = oneshot (`pet`, `feed`); auto-returns to previous mood when ticks run out |
| `nestIcon` | const | always `nest_icon.png` |
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
| `STOP_FOCUS` | `{}` | `mood=idle` |
| `PET_CHICKEN` | `{}` | oneshot `pet`, then previous mood |
| `FEED_CHICKEN` | `{}` | oneshot `feed`, then previous mood; if `feedCount>0` then `feedCount-=1` |
| `SET_MOOD` | `{ "mood": "<enum>" }` | enter that mood (AI wait: `thinking`) |
| `SET_FEED` | `{ "count": 0 }` | set `feedCount` |

Use the uppercase `type` values in the table. Do not send `SESSION_DONE`.

---

## Mood table

| mood | fps | loop | Frames |
| --- | --- | --- | --- |
| `idle` | 2 | true | `idle_00` … `idle_03` |
| `focused` | 3 | true | `focused_00` … `focused_03` |
| `thinking` | 4 | true | `thinking_00` `thinking_01` |
| `pet` | 4 | false | `pet_00` … `pet_11` |
| `feed` | 4 | false | `feed_00` … `feed_17` |

---

## Main loop (required)

1. Start this service on **8001**. AI engine stays on **8000**.
2. Poll `GET /view` (or use the `POST /event` response).
3. Push `chicken` to key 5, `nestIcon` to key 6.
4. Every `1/fps` seconds, `POST {"type":"tick","payload":{}}` and redraw key 5.
5. Session / input mapping:

| App event | Chicken `type` |
| --- | --- |
| focus session starts | `START_FOCUS` |
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
