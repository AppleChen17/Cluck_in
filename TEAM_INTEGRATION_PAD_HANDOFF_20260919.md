# CluckIn Team Integration + PAD Handoff
Date: 2026-09-19
Repo: `C:\dev\Cluck_in`

## Current stable decision

### Keys 5 / 6
Freeze as-is.

- Key 5 keeps the current START / PAUSE / RESUME behavior.
- Key 5 remains the visual progress indicator with gradual coloring.
- Key 6 keeps END behavior.
- Do not modify Keys 5 / 6 during this merge.

### Keys 7 / 8 / 9
Final hackathon direction:

- Use Logitech native text rendering.
- Do not use `GetCommandImage()` for 7 / 8 / 9.
- Do not use the thin text progress bar.
- Do not use dynamic colored text for 7 / 8 / 9.
- Selected field is represented by uppercase unit:
  - Key 7: `HR`
  - Key 8: `MIN`
  - Key 9: `SEC`
- Unselected:
  - `hr`
  - `min`
  - `sec`

Reason:
Native text gives the best size and centering. Custom dynamic images caused small/upward-shifted text. The 7 / 8 / 9 progress experiment is intentionally abandoned because Key 5 already provides a clear visual progress indicator.

## Key 8 rendering issue and resolution

The original `TimerMinuteCommand` continued to render with stale small/cyan appearance even after:

- source was changed back to native `GetCommandDisplayName()`
- active `.ict` files were removed
- old `.ict` files were renamed to `.disabled`
- plugin DLL rebuilt successfully
- `CluckInPlugin.link` verified to point to:
  `C:\dev\Cluck_in\CluckInPlugin\bin\Debug\`

A fresh action identity was then created:

```text
TimerMinuteNativeTestCommand
display name: Timer Minute Native Test
```

This fresh action rendered correctly with Logitech native text.

Conclusion:
The old `TimerMinuteCommand` action identity had stale per-action/profile appearance metadata in Options+.

### Current verified Key 8 workaround
Use `Timer Minute Native Test` on Key 8 in Options+.

Expected:
- unselected: `25 / min`
- selected: `25 / MIN`
- normal large centered native text
- no custom image
- no thin progress bar

Do not reintroduce the old custom `.ict` experiments.

## Disabled old ActionIcon files

The old experimental user-level ActionIcons were renamed to `.disabled`.

Known examples:

```text
$CluckIn___Loupedeck.CluckInPlugin.TimerMinuteCommand.ict.disabled
$CluckIn___Loupedeck.CluckInPlugin.TimerSecondCommand.ict.disabled
```

They should remain disabled.

Do not restore them into active `.ict` files unless intentionally reproducing the old experiment.

## Build

Authoritative plugin build:

```powershell
cd C:\dev\Cluck_in

dotnet build .\CluckInPlugin\src\CluckInPlugin.csproj
```

Expected output:

```text
CluckInPlugin net10.0 successful
CluckInPlugin\bin\Debug\bin\CluckInPlugin.dll
```

Reload:

```powershell
Start-Process "loupedeck:plugin/CluckIn/reload"
```

## Pre-merge hardware checklist

Before merging to main, verify on the PAD:

```text
Key 5: START / PAUSE / RESUME works
Key 5: gradual visual progress still works
Key 6: END works
Key 7: native centered text; HR when selected
Key 8: NEW native test action; MIN when selected
Key 9: native centered text; SEC when selected
7/8/9: no thin progress bar
7/8/9: no cyan custom-image box
7/8/9: countdown values still update
```

## Safe commit

Do not commit:
- `bin/`
- `obj/`
- downloaded `.zip`
- screenshots
- temporary PowerShell scripts
- `.ict.disabled` profile files from `%LOCALAPPDATA%`
- handoff files unless intentionally tracking documentation

Check first:

```powershell
cd C:\dev\Cluck_in

git status -sb
git diff --check
```

Review the actual source diff:

```powershell
git diff -- `
    CluckInPlugin/src/Actions/TimerHourCommand.cs `
    CluckInPlugin/src/Actions/TimerMinuteCommand.cs `
    CluckInPlugin/src/Actions/TimerMinuteNativeTestCommand.cs `
    CluckInPlugin/src/Actions/TimerSecondCommand.cs
```

Stage only the stable source files that are actually part of the final state:

```powershell
git add `
    .\CluckInPlugin\src\Actions\TimerHourCommand.cs `
    .\CluckInPlugin\src\Actions\TimerMinuteCommand.cs `
    .\CluckInPlugin\src\Actions\TimerMinuteNativeTestCommand.cs `
    .\CluckInPlugin\src\Actions\TimerSecondCommand.cs
```

If `TimerTextProgress.cs` is now unused, do not stage new edits to it just for this merge.

Commit:

```powershell
git commit -m "fix: stabilize Logitech timer field controls"
```

## Merge to main

Fetch first:

```powershell
git fetch origin --prune
```

Make sure the current feature branch is clean:

```powershell
git status -sb
```

Then switch to main:

```powershell
git checkout main
git pull --ff-only origin main
```

Merge:

```powershell
git merge --no-ff feature/logitech-countdown-ui `
    -m "merge: Logitech countdown UI"
```

Check:

```powershell
git status -sb
git diff --check
git log --oneline --decorate -12
```

Build main after merge:

```powershell
dotnet build .\CluckInPlugin\src\CluckInPlugin.csproj
```

Reload and do one final PAD smoke test.

Only after that passes:

```powershell
git push origin main
```

## Important merge rule

Do not use:
- `git push --force`
- blanket `git checkout --ours`
- blanket `git checkout --theirs`

If main moved or a conflict appears, stop and resolve file-by-file.

## Remaining cleanup after hackathon-critical merge

Not required before the stable merge unless there is time:

1. Remove or rename the old `TimerMinuteCommand` to avoid showing two minute actions in Options+.
2. Replace the temporary display name `Timer Minute Native Test` with a final user-facing name, but keep a fresh class/action identity if needed so Options+ does not reuse stale metadata.
3. Clean the accidental README literal line:
   ```text
   '@ | Add-Content .\README.md
   ```
4. Package `.lplug4` for teammate/demo PC.
5. Prepare/import a consistent Options+ profile if needed.

The current priority is a stable, demoable main branch.
