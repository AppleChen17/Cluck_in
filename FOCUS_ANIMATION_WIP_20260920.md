# CluckIn Focus Animation WIP Handoff

Date: 2026-09-20  
Branch: `feat/idle-pad-dashboard`

## Current target behavior

### Key 1
- Show the **current mode**, not the destination mode.
- Focus mode: `FOCUS`
- Idle mode: `IDLE`
- Pressing Key 1 still toggles between Focus and Idle.

### Focus Key 5 / Key 6 mapping

| Focus state | Key 5 | Key 6 |
| --- | --- | --- |
| Ready | grayscale `desk_empty.png` | color animated `focused_*.png` |
| Running / Countdown | animated `start_*.png` with bottom-to-top grayscale-to-color progress reveal | grayscale `nest_icon.png` |
| Paused | animated `paused_*.png` with progress reveal frozen at current ratio | grayscale `nest_icon.png` |
| Completed / End | grayscale `desk_empty.png` | color animated `focused_*.png` |

Frame sets:
- `start_00.png` ~ `start_03.png`
- `paused_00.png` ~ `paused_03.png`
- `focused_00.png` ~ `focused_04.png`
- `desk_empty.png`
- `nest_icon.png`

## Important constraints

- Do not change the existing Start / Pause / Resume / End timer behavior.
- `MainController` already transitions:
  - Ready -> Running
  - Running -> Paused
  - Paused -> Running
  - Stop -> Ready
  - Natural completion -> Completed -> Ready
- Do not create duplicate timer state or statistics counters.
- Idle Key 5 animation is already physically accepted; do not modify Idle rendering unless required.
- Focus timer Keys 7/8/9 native text UI should remain unchanged.
- Key 8 must retain `TimerMinuteNativeTestCommand`.

## Current implementation direction

`FocusChickenAnimation.cs` is intended to render Focus visuals independently from the chicken service mood routing.

Focus rendering should load the exact frame filenames directly:

- Ready / Completed:
  - Key 5: `desk_empty.png`
  - Key 6: `focused_00..04.png`
- Running:
  - Key 5: `start_00..03.png`
  - Key 6: `nest_icon.png`
- Paused:
  - Key 5: `paused_00..03.png`
  - Key 6: `nest_icon.png`

The renderer should:
- use a common union crop for each animation set so frames do not jitter,
- scale the visible artwork appropriately for the Logitech key,
- draw the full image in grayscale first,
- reveal the original color from bottom to top according to `MainController.FocusProgress`,
- keep the reveal ratio frozen while paused.

## Current known issues

Physical PAD testing has exposed two remaining Focus-animation issues:

1. The **first transition can display the wrong image** before the correct frame appears.
2. Key 6 `focused_*` animation can **play once and then appear to stop** instead of looping continuously.

These are not timer-state issues. `MainController` already changes to `Running` / `Paused` correctly.

Next investigation should focus on:
- whether both `FocusControlCommand` and `FocusStopCommand` subscribe to `FocusChickenAnimation.FrameChanged`,
- whether every animation frame causes `ActionImageChanged()`,
- whether the Focus animation worker continues looping after the final frame,
- whether stale cached frames are briefly rendered during a state transition.

## Git safety

Current Focus changes are still WIP and have not completed physical acceptance.

Therefore:
- push the WIP to `feat/idle-pad-dashboard`,
- merge the latest `origin/main` **into the feature branch**,
- resolve conflicts narrowly,
- build and re-test,
- do **not** merge `feat/idle-pad-dashboard` into `main` yet.

## Acceptance test after merge

Use a 10-second Focus timer.

1. Enter Focus:
   - Key 1 shows `FOCUS`.
   - Key 5 shows grayscale empty desk.
   - Key 6 shows continuously looping color chicken-in-nest animation.
2. Press Key 5:
   - Key 5 immediately changes to `start_*`.
   - Key 6 immediately changes to grayscale empty nest.
   - Key 5 animation loops continuously.
   - Key 5 color reveal moves bottom to top.
3. Press Key 5 again:
   - Key 5 changes to `paused_*`.
   - milk is visible.
   - animation continues.
   - reveal height freezes.
4. Resume:
   - Key 5 returns to `start_*`.
   - reveal continues from previous ratio.
5. Press Key 6 or let timer complete:
   - Key 5 returns to grayscale empty desk.
   - Key 6 returns to continuously looping color `focused_*`.

Only after this physical acceptance should the feature branch be considered ready to merge into `main`.
