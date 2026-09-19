# Cluck In Windows Desktop App

This WPF app targets .NET 10 for Windows and uses `CluckIn.App` namespaces, with no
external packages. Its desktop
snapshots are local models, not replacements for the shared transport schemas.

## Run

From the repository root:

```powershell
dotnet build CluckIn.sln
dotnet run --project src/app/CluckIn.App.csproj
dotnet run --project src/app-demo/CluckIn.App.Demo.csproj -- --watch
dotnet run --project tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj
# Optional interactive check: opens the dashboard and temporary foreground test window.
dotnet run --project tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj -- --ui
```

The app command opens the WPF dashboard. Select a workspace and click Start Focus
for a 25-minute session. Pause freezes the timer, Resume continues, and Stop ends
the session. Switching workspace changes rules immediately without restarting the
timer. Coding, Reading, Writing and Meeting are sample in-memory profiles.

With Coding selected and focus started, switch to VS Code (focused), Chrome with
GitHub in its title (focused), Chrome with YouTube in its title (distracted), or an
unmatched app/title (neutral). Keep the dashboard visible beside the other window:
clicking Cluck In itself makes Cluck In the foreground app and normally shows Neutral.
Missing browser context displays an em dash. No raw JSON is shown in the dashboard.

The previous console Program.cs is preserved in src/app-demo. Without --watch it
prints one JSON snapshot; --watch prints context/evaluation every two seconds.
Ctrl+C exits. The console demo still samples context and evaluation separately.

## Responsibilities and composition

| Component | Responsibility |
| --- | --- |
| WindowManager | Foreground process name, process ID and window title; all Windows interop lives here. |
| BrowserManager | Recognizes chrome, msedge and firefox, optionally ending in .exe; strips common browser caption suffixes. URL is always null. |
| WorkspaceManager | In-memory profiles and active workspace selection; rejects duplicate IDs and unknown selections. |
| FocusManager | Pure rule evaluation: blocked app, blocked keyword, allowed app, allowed keyword, then neutral. |
| TimerManager | Start, pause, resume, stop and lazy expiry using monotonic elapsed time; injectable TimeProvider. |
| ContextManager | Aggregates the window, browser, workspace and timer snapshot. |
| DesktopAgentService | Public orchestration API, including workspace management and focus lifecycle. |

`DesktopAgentFactory` composes the original managers with shared workspace/timer
instances and adds the desktop sample profiles. App.xaml.cs creates the service,
MainViewModel and MainWindow. The ViewModel calls only DesktopAgentService; the
window contains only bindings, a refresh timer and lifecycle handlers. No focus
rules or Windows APIs exist in the UI layer.

MainViewModel implements INotifyPropertyChanged and uses a small async RelayCommand.
A DispatcherTimer requests a refresh every second. The ViewModel serializes service
calls with a SemaphoreSlim and runs them on a worker task, since foreground-window
interop is synchronous. Await resumes on the dispatcher before publishing bound
properties. Overlapping polls are skipped; user actions queue behind a pending poll.
Polling does not disable the workspace dropdown. Failures show a message and a
neutral evaluation; the next tick retries. No worker threads update WPF controls.

The only change to DesktopAgentService is an optional DesktopContext parameter on
EvaluateFocusAsync. The UI first calls GetContextAsync, then EvaluateFocusAsync with
that snapshot so the displayed window and evaluation agree. Existing parameterless
calls remain supported. Workspace/start/pause/resume/stop methods already existed.

There is no tray support. Closing stops polling, drains pending work, stops Focus
Mode and exits. A TODO in MainWindow.xaml.cs marks future tray hosting.

Focus Mode is enabled while the session is Running or Paused. Pausing freezes
the countdown but preserves the focus intent. Stop or expiry disables Focus Mode;
the workspace stays selected. Expiry is calculated when state is read or paused,
without a background scheduler. Starting again replaces the timer. Invalid timer
transitions are no-ops. Durations must be positive. Session snapshots are immutable.

An unmatched rule returns `IsEvaluated = false`, `IsFocused = false`, with no
distraction. Consumers must check IsEvaluated before treating false as distracted.
The service also returns neutral when Focus Mode is off or no workspace is active.
Direct FocusManager evaluation works independently of mode for previews/tests.
Application matches are exact, case insensitive, and tolerate .exe suffixes;
keyword matches are case-insensitive substrings. Block rules take precedence.
The default Coding profile does not allow Chrome as a whole, so unmatched Chrome
pages remain neutral. Allowing a browser application explicitly would allow all
its pages except those matched by a block rule.

## Limits and future browser adapter

WindowManager returns an empty or partial snapshot when a foreground window is
unavailable, its process exits, or access is denied. On non-Windows systems it
returns an empty snapshot. Detection is a best-effort sample, not an atomic view
of the desktop. Browser titles can be ambiguous, localized, customized or empty;
only common suffixes are removed. Keywords apply to all window titles.

There is no Chrome Extension, URL/history access, content inspection, background
tab monitoring, AI, Logitech integration, persistence, or application automation.
Titles cannot reliably establish a website's domain or whether someone is actually
working. An unrecognized application/page is neutral rather than automatically
classified as productive or distracting.

A future extension-backed adapter would plug in at IBrowserManager and supply
BrowserContext to ContextManager. If fetching tab data requires I/O, evolve that
interface to an async method and await it in ContextManager. Focus rules would
then need explicit URL/domain support. No extension transport is implemented here.

## Desktop Agent files from the initial MVP

Modified existing empty placeholders:

- Models/WorkspaceProfile.cs, Models/FocusSession.cs
- Interfaces/IWorkspaceManager.cs, Interfaces/IFocusManager.cs, Interfaces/IContextManager.cs
- Managers/WorkspaceManager.cs, Managers/FocusManager.cs, Managers/ContextManager.cs

The original Program.cs composed the service and provided the console demo; it now
lives in src/app-demo/Program.cs.

Created:

- Models/ActiveWindowInfo.cs, Models/BrowserContext.cs, Models/DesktopContext.cs, Models/FocusEvaluation.cs
- Interfaces/IWindowManager.cs, Interfaces/IBrowserManager.cs, Interfaces/ITimerManager.cs
- Managers/WindowManager.cs, Managers/BrowserManager.cs, Managers/TimerManager.cs
- Services/DesktopAgentService.cs
- README.md (this file)
- ../../tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj
- ../../tests/CluckIn.App.SmokeTests/Program.cs

## Files changed for the WPF shell

Modified:

- src/app/CluckIn.App.csproj: WinExe, net10.0-windows, UseWPF.
- src/app/Services/DesktopAgentService.cs: evaluate an optional existing snapshot.
- src/app/README.md: desktop usage, architecture and verification.
- tests/CluckIn.App.SmokeTests/CluckIn.App.SmokeTests.csproj: Windows target and WPF references.
- tests/CluckIn.App.SmokeTests/Program.cs: preserve the original checks and invoke new checks.

Moved:

- src/app/Program.cs to src/app-demo/Program.cs, preserving the console implementation.

Created:

- src/app/App.xaml and App.xaml.cs: shared styling and application startup.
- src/app/Services/DesktopAgentFactory.cs: default service composition and sample profiles.
- src/app/ViewModels/MainViewModel.cs and RelayCommand.cs: observable presentation and async commands.
- src/app/Views/MainWindow.xaml and MainWindow.xaml.cs: dashboard, polling and shutdown.
- src/app-demo/CluckIn.App.Demo.csproj: separate console entry point.
- tests/CluckIn.App.SmokeTests/ViewModelChecks.cs: dispatcher, mapping, controls, refresh serialization and error recovery.
- tests/CluckIn.App.SmokeTests/WpfLaunchChecks.cs: opt-in real WPF startup and foreground title integration check.

All existing managers, models, interfaces and original 32 smoke assertions remain
unchanged. The smoke runner adds 32 ViewModel checks. The optional --ui check opens
the actual App/MainWindow and a temporary test-owned window, verifies live title
changes through the real WindowManager and timer, then closes both windows. It
writes a dashboard image to TestResults/wpf/dashboard.png (gitignored).
Real Chrome/Edge/Firefox pages are not opened by that check; browser inference and
UI presentation are covered by deterministic fake-window checks.

Run the smoke runner explicitly: it is not a test-framework project. The demo and
smoke runner are built through their own commands, not added to the main solution.
