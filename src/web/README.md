# Cluck In frontend demo

A React + TypeScript + Vite frontend for the hackathon. The original dashboard is
still a static demo. The Workspace Rules page edits policy through an asynchronous
in-memory service; neither page is connected to the Desktop Agent yet.

## Run

Use Node.js 22.12+ (or a newer supported LTS version) with npm. From the repository root:

```bash
cd src/web
npm install
npm run dev
```

Open the local URL printed by Vite (normally http://localhost:5173).

- Dashboard: `http://localhost:5173/#/dashboard`
- Workspace Rules: `http://localhost:5173/#/workspace-rules`

Use the navigation links to switch pages. Hash navigation needs no extra routing
dependency or server rewrite configuration. The editor remains mounted when hidden
so navigating to the dashboard does not discard drafts.

## Build

```bash
npm run build
npm run preview
```

`npm run build` checks TypeScript and creates `dist/`. `npm run preview` serves that build locally.

## Workspace editing

Select Coding, Meeting, Reading or Writing. Each profile has four editable lists:
allowed applications, allowed title keywords, blocked applications and blocked
title keywords. Enter a value and click Add or press Enter; use the named remove
button on a chip to delete it. Whitespace is trimmed, blank inputs are ignored, and
duplicates within a list are rejected without regard to case. Process names are
shown as stored, such as `code` and `WindowsTerminal`.

Added/removed rules update an independent draft keyed by workspace ID. Switching
workspaces preserves each draft without carrying its rules into another workspace.
The selector marks modified workspaces with Unsaved. Rule order and capitalization
alone do not count as changes because the Desktop Agent treats rules as unordered
and case-insensitive. Text still in an Add input is not a rule until submitted.

Save Changes sends the current full draft to the service and uses its returned
profile as the new saved baseline. Reset discards the current draft and restores
that baseline, without affecting other workspaces. Both are disabled when clean;
inputs and selection are disabled while saving. Save failures retain the draft.
Unsaved rules trigger the browser's standard unload warning where supported.

Saved mock data survives workspace/page navigation for the current browser page
session only. Reloading resets it to samples. There is no localStorage, database,
HTTP connection, C# API change, or change to the Windows app's settings.

## Data and future API

`src/models/WorkspaceProfile.ts` matches the C# concept using camelCase:

```ts
type WorkspaceProfile = {
  id: string;
  name: string;
  allowedApplications: string[];
  allowedWindowKeywords: string[];
  blockedApplications: string[];
  blockedWindowKeywords: string[];
};
```

`src/services/workspaceService.ts` exposes the WorkspaceService interface:

- `getWorkspaces(): Promise<WorkspaceProfile[]>`
- `getWorkspace(id): Promise<WorkspaceProfile>`
- `updateWorkspace(id, workspace): Promise<WorkspaceProfile>`

Its mock owns cloned data and returns copies, so editing a draft cannot mutate saved
state. Replace the exported service with an HTTP implementation of the same interface
when the C# endpoints exist: GET `/api/workspaces`, GET `/api/workspaces/{id}`, and
PUT `/api/workspaces/{id}` with the complete profile as JSON. URL-encode IDs, check
HTTP errors, and return the saved server representation. The React component only
uses the interface; it can also accept an injected service for tests.

Focus evaluation remains in C#; the frontend does not classify activity. Keywords
refer to window/page titles, not URLs. A comment marks a future separate temporary
task allowlist section; no AI behavior or temporary rules are implemented.

## Checks and changed files

```bash
npm test
npm run build
```

The tests use Node's built-in runner and TypeScript stripping (Node 22.12+ with the
provided flag, or a newer supported Node version). They cover all categories,
trimming, duplicate prevention, dirty comparisons, persistence, isolation and IDs.

Manual check: add a Coding rule, switch to Meeting and confirm it is absent, return
to Coding and confirm its draft remains. Reset it; add it again and Save. Switching
away and back should retain the saved rule with Save disabled. Try a duplicate with
different capitalization and verify the list is unchanged. Removing the saved rule
then Reset should restore it. Repeat for each of the four categories.

Created: `src/models/WorkspaceProfile.ts`, `src/services/workspaceService.ts`,
`src/components/WorkspaceRules.tsx`, `src/components/workspaceRulesState.ts`,
`src/workspace-rules.css`, and `tests/workspaceRules.test.mjs`.

Modified: `src/App.tsx` (navigation wrapper; original dashboard retained),
`package.json` (test command), and this README. No new dependencies were added.
