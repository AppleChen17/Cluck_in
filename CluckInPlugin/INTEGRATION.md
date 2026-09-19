# CluckIn Logitech Integration

This document defines the current Logitech button behavior and the HTTP input-event contract shared with the Python/app side.

## Transport

The plugin sends JSON with HTTP `POST` to:

```text
http://127.0.0.1:8765/input-event
```

Override the endpoint with the environment variable:

```text
CLUCKIN_INPUT_EVENT_URL
```

Every emitted event uses this envelope:

```json
{
  "type": "CHANGE_MODE",
  "source": "logitech",
  "timestamp": "2026-09-19T20:00:00+08:00",
  "payload": {
    "mode": "focus"
  },
  "metadata": {
    "keyId": 1
  }
}
```

The machine-readable contract is `shared/schemas/input-event.schema.json`.

## Button map

| Control | Focus mode | Idle mode | HTTP event |
| --- | --- | --- | --- |
| Key 1 | Switch to Idle | Switch to Focus | `CHANGE_MODE` |
| Key 2 | Messages | Feed chicken | Focus: pending shared contract; Idle: `FEED_CHICKEN` |
| Key 3 | Cycle AI assist: Off -> Suggestion -> On -> Off | Pet chicken | Focus: `SET_AI_ASSIST_MODE`; Idle: `PET_CHICKEN` |
| Key 4 | Task selection placeholder | Task selection placeholder | Not emitted yet |
| Key 5 | Start / Pause / Resume focus | Ignored | `START_FOCUS`, `PAUSE_FOCUS`, `RESUME_FOCUS` |
| Key 6 | End active focus session | End active focus session | `STOP_FOCUS` only while active |
| Key 7 | Select Hours field | Select Hours field | Local only |
| Key 8 | Select Minutes field | Select Minutes field | Local only |
| Key 9 | Select Seconds field | Select Seconds field | Local only |
| Roller | Adjust selected H/M/S field | Same local timer editor | Local only |
| Roller press/reset | Reset duration to 25:00 | Same | Local only |

The roller uses 8 raw ticks per logical step. Timer editing uses total seconds, so carry/borrow is natural, for example `00:00:59 + 1 sec -> 00:01:00`.

## Event contract

### Change mode

```json
{
  "type": "CHANGE_MODE",
  "source": "logitech",
  "payload": {
    "mode": "focus"
  },
  "metadata": {
    "keyId": 1
  }
}
```

`payload.mode` is `focus` or `idle`.

### AI assist

Key 3 uses one event type with a three-state enum:

```json
{
  "type": "SET_AI_ASSIST_MODE",
  "source": "logitech",
  "payload": {
    "mode": "suggestion"
  },
  "metadata": {
    "keyId": 3
  }
}
```

Allowed values:

- `off`: AI assistance disabled.
- `suggestion`: AI may generate suggestions, but should not automatically execute actions.
- `on`: AI assistance enabled according to the Python-side policy.

Python should treat `payload.mode` as an enum and reject unknown values.

### Chicken actions

Idle Key 2:

```json
{
  "type": "FEED_CHICKEN",
  "source": "logitech",
  "payload": {},
  "metadata": {
    "keyId": 2
  }
}
```

Idle Key 3:

```json
{
  "type": "PET_CHICKEN",
  "source": "logitech",
  "payload": {},
  "metadata": {
    "keyId": 3
  }
}
```

### Focus timer control

Start:

```json
{
  "type": "START_FOCUS",
  "source": "logitech",
  "payload": {
    "focusDurationSeconds": 1500
  },
  "metadata": {
    "keyId": 5
  }
}
```

Pause:

```json
{
  "type": "PAUSE_FOCUS",
  "source": "logitech",
  "payload": {},
  "metadata": {
    "keyId": 5
  }
}
```

Resume:

```json
{
  "type": "RESUME_FOCUS",
  "source": "logitech",
  "payload": {},
  "metadata": {
    "keyId": 5
  }
}
```

Stop:

```json
{
  "type": "STOP_FOCUS",
  "source": "logitech",
  "payload": {},
  "metadata": {
    "keyId": 6
  }
}
```

## Current boundaries

- Focus-mode Key 2 (`Messages`) is visible in the Logitech UI but does not emit an HTTP event yet. Do not depend on a `SHOW_MESSAGES` event until the shared contract is finalized.
- Key 4 task selection is a placeholder in the Logitech plugin and does not emit an input event yet.
- Keys 7-9 and the roller are local timer-editor controls and intentionally do not emit HTTP events.
- The Logitech countdown is currently a local display mirror. The desktop app remains authoritative for the real focus session state.
