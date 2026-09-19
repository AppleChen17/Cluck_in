# Cluck In data contracts

These initial JSON Schema contracts describe transport/domain data, not database records. The `app` module owns application state and orchestration. Adapters normalize their input; AI classifies messages; actions executes app-issued commands; UI modules consume snapshots. No transport, endpoint, business logic, persistence, or generated language models are implemented here.

## Ownership

| Contract | Producer | Consumer | Represents |
| --- | --- | --- | --- |
| [InputEvent](../shared/schemas/input-event.schema.json) | logitech / web / system | app | A user or system input, such as starting focus or selecting a task. |
| [ExternalMessage](../shared/schemas/external-message.schema.json) | external | app | A normalized Gmail or Slack message. |
| [ExternalEvent](../shared/schemas/external-event.schema.json) | external | app | A normalized timed Google Calendar event. |
| [SessionContext](../shared/schemas/session-context.schema.json) | app | ai (inside AIRequest) | A snapshot of the user's mode, task, and optional focus settings. |
| [AIRequest](../shared/schemas/ai-request.schema.json) | app | ai | One ExternalMessage and one SessionContext, composed with `$ref`. |
| [AIDecision](../shared/schemas/ai-decision.schema.json) | ai | app | A bounded classification and scores associated with a message ID. |
| [IntentAnalyzeRequest](../shared/schemas/intent-analyze-request.schema.json) | app | ai | One ExternalMessage, one SessionContext, and the current instant. **Proposed — see "Pending" below.** |
| [IntentDecision](../shared/schemas/intent-decision.schema.json) | ai | app | What the sender wants, and the meeting time extracted from the message. **Proposed — see "Pending" below.** |
| [ActionCommand](../shared/schemas/action-command.schema.json) | app | actions | An instruction to execute; it does not report success or update app state itself. |
| [AppState](../shared/schemas/app-state.schema.json) | app | web / logitech | A UI snapshot with session information, counts, and chicken status. |

`system` means an app-side system input, not a new module. Although AIRequest embeds an ExternalMessage, external adapters communicate with app, not directly with AI.

## Data flow

```text
Logitech/Web/System
    ↓
InputEvent
    ↓
app

External
    ↓
ExternalMessage / ExternalEvent
    ↓
app + SessionContext
    ↓
AIRequest (message + context)
    ↓
AI
    ↓
AIDecision
    ↓
app
    ↓
ActionCommand
    ↓
actions

app
 ↓
AppState
 ↓
web / logitech
```

ExternalEvent currently supplies app context/display information; AIRequest classifies messages only. Calendar-to-AI requests are not defined.

## Serialization and validation

- Use JSON Schema Draft 2020-12. Properties use camelCase. Mode, source, mood, and decision values are lowercase; input and command types use UPPER_SNAKE_CASE. Enum matching is case-sensitive.
- Timestamps use `format: "date-time"`: RFC 3339 date-times (an ISO 8601 profile), including seconds and `Z` or an explicit UTC offset. Date-only and timezone-less strings are not valid timestamps. Enable format validation explicitly in your validator; some tools treat `format` only as an annotation.
- Durations, remaining seconds, counts, and chicken energy are integers. Scores are numbers in the inclusive range 0–1. Energy is 0–100; durations and counts cannot be negative.
- Contract objects reject undeclared properties. Optional `metadata` objects on every contract, plus chicken state, allow normalized extension fields. `payload` is required for inputs/commands and accepts any JSON object, including `{}`. Metadata and payload values may be any JSON value. Neither is a place for raw provider response objects or credentials; schemas cannot automatically detect such content.
- Required fields are listed below. Omitted optional fields mean information was not supplied. `null` is accepted only where explicitly declared. No schema defaults are inserted into received data.
- Schema `$id` values use `https://cluck-in.example/schemas/v1/`. These are identifiers on a reserved example domain, not hosted endpoints. Register all eight local schemas by `$id` in a validator's local resource registry; resolve relative `$ref` values against those IDs without fetching the network. AppState reuses SessionContext's mode, task, and start-time definitions.
- C#, Python, and TypeScript implementations should preserve wire names and enum values when serializing. Schema validation does not enforce timestamp ordering, message ID correlation across documents, or state transitions; app/adapters must eventually check those relationships.

## Fields and semantics

| Contract | Required fields | Optional fields |
| --- | --- | --- |
| InputEvent | type, source, timestamp, payload | metadata |
| ExternalMessage | id, source, sender, content, timestamp, unread | title, metadata |
| ExternalEvent | id, source, title, startTime, endTime | description, location, attendees, metadata |
| SessionContext | mode, currentTask | focusStartedAt, focusDurationSeconds, allowedApps, blockedApps, metadata |
| AIRequest | message, context | metadata |
| AIDecision | messageId, decision, relevance, urgency, reason | metadata |
| IntentAnalyzeRequest | message, context, now | metadata |
| IntentDecision | messageId, intent, confidence, reason | startTime, endTime, title, metadata |
| ActionCommand | type, timestamp, payload | metadata |
| AppState | mode, currentTask, focusRemainingSeconds, heldMessages, urgentMessages, upcomingEvents, chicken, lastUpdatedAt | focusStartedAt, metadata |

### Inputs and commands

Input sources are `logitech`, `web`, and `system`. Initial input types are `START_FOCUS`, `STOP_FOCUS`, `PAUSE_FOCUS`, `RESUME_FOCUS`, `CHANGE_MODE`, `SELECT_TASK`, `FEED_CHICKEN`, and `PET_CHICKEN`.

Command types are `SHOW_NOTIFICATION`, `OPEN_APP`, `OPEN_URL`, `HOLD_MESSAGE`, `RELEASE_MESSAGES`, `START_FOCUS`, and `END_FOCUS`. Input `STOP_FOCUS` and command `END_FOCUS` deliberately retain the requested vocabulary. Only app decides whether an input results in a command; there is no automatic one-to-one mapping.

The fixture payloads suggest `focusDurationSeconds` for START_FOCUS and `title`, `message`, and `messageId` for SHOW_NOTIFICATION. These are examples, not type-specific required fields. Agree on payload shapes before implementing handlers. A command name alone does not authorize actions to take ownership of the session or message queue.

### Messages and events

Messages initially support `gmail` and `slack`; events support `google-calendar`. Adding providers requires an explicit enum/schema update. IDs are opaque strings, stable and unique across sources within their entity type; fixture IDs use provider prefixes. AIDecision.messageId must exactly match the classified ExternalMessage.id. Account-level namespacing is left for adapter design.

Senders and attendees are normalized human-readable strings, not provider user objects. Attendees may use display names or email addresses; they are not reliable identity keys. An absent attendees field means unknown; an empty array means no attendees. Message content is plain text and may be empty. A missing or null message title means no title. Event description/location may also be null. Non-null identifiers, titles, labels, tasks, and decision reasons must be nonempty strings.

Calendar endTime is exclusive and must be later than startTime; that comparison requires application validation. All-day/date-only events, recurrence, and provider-specific details are deferred.

### Session and UI state

Modes are `idle`, `focus`, and `auto`. currentTask is a task label or null when nothing is selected. focusStartedAt is the original session start time, null for no focus session, or omitted when timing is not supplied. focusDurationSeconds is the planned total duration. Optional application lists use normalized labels; omitted means unspecified and an empty array means no explicit entries, without implying any enforcement policy.

AppState.focusRemainingSeconds is calculated by app; zero means no active countdown or an exhausted timer. Message/event fields are counts, not arrays. urgentMessages counts items awaiting attention, not cumulative history. upcomingEvents uses an app-selected time window that is not yet standardized. The snapshot is not a delta/event stream.

Chicken mood initially supports `idle`, `focused`, `happy`, and `tired`; energy is an integer from 0 to 100. Mood is independent of session mode. No mood transitions, feeding behavior, or energy rules are defined.

PAUSE_FOCUS and RESUME_FOCUS are accepted input names only. Pause behavior is not fully modeled: app may retain remaining seconds while paused, but these snapshots do not include a paused flag. Agree on that addition and timer semantics before implementing pause/resume.

### AI decisions

- `urgent`: recommend immediate attention.
- `allow`: eligible for normal delivery.
- `hold`: recommend deferred delivery.

Relevance measures relation to the current task; urgency measures time sensitivity. Thresholds and mappings to actions are intentionally undefined. `reason` is explanatory text; consumers branch on `decision`, not natural-language parsing. App remains responsible for choosing and issuing actions.

The existing React demo uses display/mock values such as `Focus`, `Focused`, and `SHOW_NOW`. They are not the wire values defined here. Leave the demo unchanged for now and agree on explicit display mapping when connecting it. AppState currently does not include message bodies, an AIDecision, or recent action history, so additional UI data contracts may be needed later.

## Fixtures

All fixtures are under [shared/fixtures](../shared/fixtures/). AIRequest embeds the same Slack message and session context used in their standalone fixtures. AIDecision and ActionCommand refer to the same message ID. At 11:33, the 25-minute focus session started at 11:30 has 1,320 seconds remaining. Dashboard counts illustrate a snapshot rather than a complete event history.

| Fixture | Schema |
| --- | --- |
| input-event.start-focus.json | input-event.schema.json |
| external-message.slack.json | external-message.schema.json |
| external-message.gmail.json | external-message.schema.json |
| external-event.calendar.json | external-event.schema.json |
| session-context.focus.json | session-context.schema.json |
| ai-request.message.json | ai-request.schema.json |
| ai-decision.urgent.json | ai-decision.schema.json |
| action-command.notification.json | action-command.schema.json |
| intent-analyze-request.message.json | intent-analyze-request.schema.json |
| intent-decision.meeting.json | intent-decision.schema.json |
| intent-decision.no-time.json | intent-decision.schema.json |
| app-state.focus.json | app-state.schema.json |

To validate, load all eight schemas into a Draft 2020-12 validator's local registry, check their schema syntax, then validate each fixture against the matching schema with date-time format checks enabled. No validator dependency is added to application modules.

### Intent classification (proposed, not agreed)

`IntentAnalyzeRequest` / `IntentDecision` answer a question the existing AI
contracts do not. `AIDecision` returns `urgent` / `allow` / `hold`: whether a
message is worth interrupting the user for. Automatically answering a message
needs a different answer: what the sender wants. The two are independent — a
meeting invitation can be worth holding until focus ends *and* worth putting on
the calendar immediately.

`intent` has two values in this draft. `meeting_invite` means the sender states
a specific, already-decided time. `other` is everything else, including a
message that only asks to find a time; an `asking_availability` value was
drafted and deliberately left out of the first cut, and adding it later is an
enum entry rather than a redesign.

Three details in these schemas are not stylistic, and each cost real debugging:

- **`now` is required on the request.** A model has no clock. Asked to resolve
  "next Tuesday" without being told today's date, it picks a plausible one.
- **`startTime` and `endTime` are nullable, and null is a real answer.**
  `meeting_invite` with a null `startTime` means "this is about a meeting and I
  could not tell when". Consumers must not substitute a guess. A meeting on the
  wrong day is worse than a meeting nobody scheduled.
- **`messageId` is injected by the caller, not generated.** A small model asked
  to reproduce `slack:C08ABCDEF:1789788720.000200` drops a digit, and
  AIDecision's rule that the id must match exactly then fails at runtime.

`confidence` exists so the consumer can refuse to act. Writing to a calendar is
harder to undo than showing a notification, so the same threshold does not
suit both.

**Status: proposed by the `src/external` work, not yet agreed.** The schemas and
fixtures are committed and validated so the shape can be reviewed concretely,
and nothing implements them yet. §11 of `PRODUCT_SPEC_MVP.md` lists three
pending contract changes; this is a fourth. The related scope point is that
Slack, Google Calendar and automatic replies are all out of scope in §3 of that
spec, and `src/external` now implements them — also a team decision, and the
spec has been left untouched rather than edited by one contributor.

## Team decisions before implementation

1. Finalize input/command payload fields and validation, including task selection, mode changes, message release scope, and app/URL targets.
2. Agree on timer pause/resume representation, duration defaults, and valid mode transitions. No paused state is encoded yet.
3. Define ID namespacing across accounts, request correlation for repeated classification, command outcomes, and retry/idempotency behavior if needed.
4. Define AI score thresholds, delivery policy, queue ownership details, urgent-item acknowledgement, and the upcoming-event time window. App owns application state throughout.
5. Confirm chicken mood vocabulary and display rules, plus a future attendee identity representation and all-day calendar event handling.
6. Choose transport and validation libraries later. Agree on compatibility/versioning before changing enums or fields: strict objects mean even new optional fields can be rejected by older consumers. Breaking changes should get a new schema version/ID and coordinated consumer changes; this draft does not add a version field to each message.
