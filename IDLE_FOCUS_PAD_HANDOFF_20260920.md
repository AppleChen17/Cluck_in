# Idle Focus / Feed Counter — WIP Handoff (2026-09-20)

> **WIP — physical Pad integration currently failing**

## Goal

- Idle Keys 7/8/9 should display accumulated focus time.
- Every 5 seconds of valid accumulated focus should grant 1 feed bag.

## What currently works

Feed reward calculation was changed to milestone-based accounting:

```python
reached = totalFocusSeconds // 5
earned = reached - focusFeedRewardsGranted
```

The persistent `focusFeedRewardsGranted` ledger is intended to ensure repeated renders, identical state events, reconnects, and restarts do not grant the same milestone twice.

Legacy saves that do not contain `focusFeedRewardsGranted` initialize it from the already accumulated focus milestones (`totalFocusSeconds // 5`). This deliberately treats historical milestones as processed so upgrading does not suddenly grant historical or duplicate feed rewards.

Deterministic checks passed for:

- 0 sec -> 0 feed
- 4 sec -> 0 feed
- 5 sec -> 1 feed
- 9 sec -> 1 feed
- 10 sec -> 2 feed
- 11 sec -> 2 feed
- 3 sec + stop + 2 sec -> exactly 1 feed total
- 8 sec + stop + 2 sec -> exactly 2 feed total
- 4 sec -> 11 sec -> 2 milestones, with a repeated 11-sec update granting 0 additional feed

Tests also cover persisted reward-ledger restart behavior and a production-class path through the C# timer/reporter, HTTP event, Python chicken API, persisted state, and returned view.

## IMPORTANT — physical Pad test currently FAILS

Observed on the actual Logitech Pad:

- seconds display is stuck at `29`
- accumulated focus time does not increase correctly
- feed count does not increase
- therefore deterministic test PASS does not mean the production runtime path works

Branch status: **WIP — physical Pad integration currently failing**.

## Root cause is NOT confirmed yet

The next person should investigate these possibilities rather than treating any one of them as established:

1. Logitech/plugin runtime may still be loading an old DLL/build.
2. Keys 7/8/9 may still be connected to an old session/countdown timer rather than `totalFocusSeconds`.
3. `totalFocusSeconds` may only be updated through helper/test logic rather than the real production focus tick.
4. Main Program may not be propagating updated focus state to the Plugin.
5. Milestone reward logic may not execute in the live runtime path.
6. A stale/cached `ChickenView` or equivalent view state may be producing the visible `29`.

Some runtime observations and hypotheses were gathered locally, but the failure has not yet been confirmed by a successful physical-device retest. Do not present a hypothesis as the root cause until the complete live path and loaded binaries are verified on the Pad.

## Highest-priority next debug step

Trace the actual live production path end to end:

```text
focus start
-> timer/tick
-> elapsed delta
-> totalFocusSeconds
-> persistence
-> milestone reward
-> feedCount
-> view/event
-> serialization/interface
-> Plugin
-> Key 7/8/9 renderer
```

Especially identify the exact field/expression producing the visible `29` on Key 9. Also verify the exact Main Program executable, chicken service code, and plugin DLL that Logitech has loaded; rebuilding alone does not reload an already-running process.

## Expected final behavior

After 5 valid focus seconds:

- `totalFocusSeconds = 5`
- `focusFeedRewardsGranted = 1`
- `feedCount = 1`
- Idle display = `00:00:05`

After another 5 seconds:

- `totalFocusSeconds = 10`
- `focusFeedRewardsGranted = 2`
- `feedCount = 2`
- Idle display = `00:00:10`

Cross-session behavior:

- focus 3 sec
- stop
- focus 2 sec
- expected exactly 1 feed bag

A normal restart must preserve accumulated time, feed inventory, and the processed milestone ledger without awarding duplicates.

## Regression requirements

Do not break:

- Key 6 animation
- idle chicken animation
- focus start/pause/resume/stop
- existing mode transitions

Do not add a second independent focus timer. The Main Program's accumulated focus state must remain authoritative.

## Relevant implementation areas

- `src/chicken/chicken_statistics.py` — cumulative state, migration, milestone rewards, atomic persistence.
- `src/app/Managers/ProgressTrackingTimer.cs` — observes the authoritative Main Program timer and reports elapsed focused seconds.
- `src/app/Services/ChickenProgressReporter.cs` — durable delivery of `RECORD_FOCUS` events.
- `CluckInPlugin/src/IdleChickenAnimation.cs` — receives `ChickenView.TotalFocusSeconds` and formats Idle time.
- `CluckInPlugin/src/Actions/TimerHourCommand.cs` — Idle Key 7.
- `CluckInPlugin/src/Actions/TimerMinuteCommand.cs` — Idle Key 8.
- `CluckInPlugin/src/Actions/TimerSecondCommand.cs` — Idle Key 9.
- `src/chicken/test_idle_state.py` — deterministic milestone, migration, persistence, and animation checks.
- `tests/CluckIn.App.SmokeTests/ProgressTrackingChecks.cs` — Main Program production-class path checks.
- `CluckInPlugin/tests/IdleChickenIntegration/Program.cs` — plugin rendering and animation integration checks.

## Safety state

The pre-existing unexpected work remains in the untouched stash:

```text
stash@{0}: On main: pre-idle-focus-feed-counter-unexpected-work-20260920
4529f27625f297ccb33066daece68ba37ff9c4f7
```

Do not pop, apply, drop, or modify that stash as part of this feature investigation.
