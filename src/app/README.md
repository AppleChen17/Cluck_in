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

With Coding selected and focus started, switch to VS Code (focused), Chrome at
github.com (focused), Chrome at youtube.com (distracted), or an
unmatched app/title (distracted). Keep the dashboard visible beside the other window:
clicking Cluck In itself makes Cluck In the foreground app while an active chicken prompt preserves its original target.
Missing browser context displays an em dash. No raw JSON is shown in the dashboard.

The previous console Program.cs is preserved in src/app-demo. Without --watch it
prints one JSON snapshot; --watch prints context/evaluation every two seconds.
Ctrl+C exits. The console demo still samples context and evaluation separately.

## Responsibilities and composition

| Component | Responsibility |
| --- | --- |
| WindowManager | Foreground process name, process ID, window handle and title. |
| BrowserManager | Recognizes chrome, msedge and firefox, optionally ending in .exe; strips common browser caption suffixes. Async sampling reads the foreground address bar through BrowserUrlReader; synchronous calls remain caption-only. |
| WorkspaceManager | In-memory profiles and active workspace selection; rejects duplicate IDs and unknown selections. |
| WhitelistManager | Loads task rules and evaluates allowed/denied/unknown; no UI or desktop operations. |
| FocusManager | Owns focus lifecycle, delegates rule evaluation and decides whether to request intervention. |
| InterventionManager | Grace period, prompt state, cooldown, temporary allowances and user actions. |
| DesktopManager | Opens apps/URLs and restores a previously allowed work window after user action. |
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

DesktopAgentService evaluates a caller-provided context snapshot, updates intervention
state through FocusManager, and routes UI/API actions to InterventionManager. The
factory and DI registration share timer, whitelist and intervention instances.

There is no tray support. Closing stops polling, drains pending work, stops Focus
Mode and exits. A TODO in MainWindow.xaml.cs marks future tray hosting.

Focus Mode is enabled while the session is Running or Paused. Pausing freezes
the countdown but preserves the focus intent. Stop or expiry disables Focus Mode;
the workspace stays selected. Expiry is calculated when state is read or paused,
without a background scheduler. Starting again replaces the timer. Invalid timer
transitions are no-ops. Durations must be positive. Session snapshots are immutable.

An unmatched activity is denied. Missing process information or a missing URL when
domain rules are required returns `IsEvaluated = false`; unknown is not a violation.
Focus Mode off or no workspace also yields a neutral evaluation. Pure rule evaluation
is available independently of mode through WhitelistManager and FocusManager.Evaluate.
Chrome (including chrome.exe, ignoring case) is evaluated exclusively by the current
HTTP(S) URL host against AllowedDomains. App allow/block lists and caption keywords
never affect Chrome. Subdomains match at a dot boundary; suffix-spoof domains do not.
Missing/invalid URLs remain unknown, with no title/app fallback. An empty domain
whitelist denies all known Chrome websites.

Default domain lists:
- Coding: localhost, github.com, stackoverflow.com, chatgpt.com, learn.microsoft.com,
  developer.mozilla.org.
- Reading: wikipedia.org, learn.microsoft.com, developer.mozilla.org.
- Writing: docs.google.com.
- Meeting: meet.google.com, teams.microsoft.com, zoom.us.
- Cluck In Development Task: localhost, github.com, chatgpt.com.

Other applications still use app/title rules. Other recognized browsers retain the
existing evaluation path, including domain checks when configured. Application
matches ignore case and .exe suffixes; title keywords are substrings.

## Chicken Intervention

The desktop refresh loop samples once per second. A continuously denied app/domain
shows a non-activating, topmost WPF chicken after **7 seconds**. Returning to allowed
activity, changing task/workspace, pausing, stopping or expiry dismisses it. Unknown
context clears pending grace. Clicking the WPF chicken preserves the captured offender.
The UI never closes an app or tab. `Peek`, `Nudge`, `Block` are presentation severities;
this version shows `Nudge` and performs no forced blocking.

- **Back to Work** restores the last verified allowed window. A browser target opens
  its captured allowed HTTP(S) URL because a window handle cannot identify its tab.
  No known target disables this action; a closed/changed window or Windows foreground
  activation failure keeps the prompt with an error and permits retry/temporary allow.
- **Allow Temporarily** permits the captured app (or app + exact domain) for 5 minutes,
  without editing TaskProfile or workspace rules. Other domains/apps remain checked.
- Successful actions apply a 15-second cooldown, followed by a fresh grace period.
  Temporary permissions and return targets are session-local and clear on task/focus
  reset; pausing through the desktop controls clears them as well.
- Add to Workspace is optional and is not implemented in this version.

`GET /api/intervention` returns the shared immutable snapshot. JSON
`POST /api/intervention/action` accepts `{ "interventionId": "<id>", "action": 2 }`
for ReturnToWork or action `3` for TemporaryAllow. Only active matching IDs are
accepted (409 otherwise); unsupported actions return 400. The web component only
polls this state and sends actions; WPF owns the existing monitoring loop. API calls
run on the same dispatcher as the desktop loop and retain existing origin/JSON guards.

Deterministic smoke checks use TimeProvider and a fake desktop, covering grace,
recovery, cooldown, temporary expiry/domain isolation, stale actions, session reset,
and return failures. The API integration checks use a random local port.

## Limits and future browser adapter

WindowManager returns an empty or partial snapshot when a foreground window is
unavailable, its process exits, or access is denied. On non-Windows systems it
returns an empty snapshot. Detection is a best-effort sample, not an atomic view
of the desktop. Browser titles can be ambiguous, localized, customized or empty;
only common suffixes are removed. Keywords apply to all window titles.

BrowserUrlReader uses read-only Windows UI Automation to read the foreground browser
address bar. It never sends keys or activates controls. It skips web documents and
focused (currently edited) address fields, verifies foreground window identity/title,
and times out after 800 ms with at most one provider read in flight. Only HTTP(S)
addresses are accepted; scheme-less addresses are normalized to HTTPS.

There is no Chrome Extension, history access, background tab monitoring, AI, Logitech
integration, persistence, or automatic window/tab closing.
Titles cannot reliably establish a website's domain or whether someone is actually
working. An unlisted application is denied; an unavailable browser URL remains unknown.

ContextManager awaits IBrowserManager.GetBrowserContextAsync. BrowserUrlReader is
injected through IBrowserUrlReader, so an extension-backed adapter can replace it
without changing whitelist or intervention logic. The built-in reader recognizes
common English, Traditional Chinese and Simplified Chinese address bar labels and
Firefox/Edge address automation IDs. Browser versions, accessibility settings or other
languages may prevent detection; WPF shows `URL unavailable` instead of guessing.

To verify YouTube: start Cluck In Development, open YouTube in Chrome, click the page
so the address bar is not being edited, and check Current Website in WPF. When it
shows a youtube.com URL, remaining there for 7 seconds triggers the chicken. Returning
to chatgpt.com or github.com dismisses it. Automated BrowserUrlChecks cover URL
normalization and this context-to-intervention path using an injected URL reader.
Actual browser accessibility support must be checked on the running desktop.

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

The smoke runner covers desktop context, task API, ViewModel and intervention behavior. The optional --ui check opens
the actual App/MainWindow and a temporary test-owned window, verifies live title
changes through the real WindowManager and timer, then closes both windows. It
writes a dashboard image to TestResults/wpf/dashboard.png (gitignored).
Real Chrome/Edge/Firefox pages are not opened by that check; browser inference and
UI presentation are covered by deterministic fake-window checks.

Run the smoke runner explicitly: it is not a test-framework project. The demo and
smoke runner are built through their own commands, not added to the main solution.

## Task Analysis integration

Task-started Focus sessions now use the Python Task Analysis API for activity not
allowed by local rules. See [integration contract and demo steps](../../docs/task-analysis-integration.md).
Start/Pause/Resume/Stop Focus retain the selected CurrentTask, including when started
from the WPF Workspace controls. Task rules and AI analysis take precedence over the
workspace fallback. Start Focus uses the task duration or 25 minutes if unspecified.
The WPF Current Task label shows which task is retained. End Task (or JSON POST
`/api/session/end-task`) stops focus and clears the task. Starting with no selected
task creates a generic session using Workspace rules, then Workspace AI on a whitelist
miss. AI allow/warn permit the activity; only block enters grace. Session.CurrentTask
stays null. No meaningful Task or Workspace context means deterministic-only fallback.

## Urgent message WPF alerts

The desktop polls the existing external service (`GET http://127.0.0.1:8100/messages`)
every five seconds and passes unread messages with the current focus context to the
existing AI API (`POST http://127.0.0.1:8000/analyze-message`). Only responses with
`decision: urgent` enter the WPF alert queue. No new classification implementation
or backend endpoint is introduced. Both services must be running for live alerts.
The message service URL can be set through `ExternalMessages:BaseUrl`; AI uses the
existing `AiEngine` settings.

The alert uses the same cream background, gold border, chicken heading and topmost,
non-activating behavior as the disallowed-website intervention. It shows the sender,
source, time, title, content and urgency reason. Urgent alerts take visual precedence
over distraction warnings. Acknowledging advances to the next alert without replying,
marking the source message read, or stopping focus. The distraction warning can return
once the urgent queue is empty. Closing the main app closes both kinds of popup.

IDs are deduplicated for the app's lifetime, including after acknowledgement. The
cursor and queue are in memory, so restarting can replay unread messages. Failed API
requests are retried without advancing the cursor. Up to 100 urgent alerts can wait;
when full, polling resumes delivery after the user acknowledges an alert. Allow/hold
responses do not open this urgent popup. Their deferred delivery is outside this UI.

Smoke checks use fake HTTP responses for urgent/allow/hold, duplicate IDs, cursor
reuse and failed-analysis retries, and render the WPF alert offscreen to
`TestResults/wpf/urgent-message.png`. They do not contact Gmail, Slack or Ollama.

For diagnosis, the dashboard reports which service URL failed and its HTTP status
when available. The 測試緊急通知 button opens a clearly labeled local WPF preview
without either backend. Previewing does not send any messages.

On this machine the external service uses its own repository-root environment:

```powershell
.\.venv-external\Scripts\python.exe -m uvicorn app:app --app-dir src/external --host 127.0.0.1 --port 8100 --workers 1
```

Without `src/external/.env` account configuration this service provides fixture
messages, not real Gmail/Slack messages. The AI service must also be running on
port 8000 for real classification.

Automatic alerts now check external `/health` first. If any enabled adapter is
`fixture`, automatic delivery is disabled and the dashboard explains how to connect
a real source. Remove `fixture` from `EXTERNAL_ADAPTERS` when using real accounts.
At least one enabled, connected Gmail/Slack adapter is required. Explicit local
preview alerts still work without a real account.
