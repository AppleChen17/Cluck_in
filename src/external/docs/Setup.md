# Credentials setup

Everything here goes into `src/external/.env`, which is gitignored.
Start from `.env.example`.

---

## Gmail (IMAP + app password)

### 1. Check whether your account can issue app passwords

Open <https://myaccount.google.com/apppasswords>.

- **A password generator appears** — you are good, continue to step 2.
- **"Your account isn't eligible for this setting"** — something below is
  blocking it, in rough order of likelihood:

  | Cause | Check | Fix |
  |---|---|---|
  | 2-Step Verification is off | <https://myaccount.google.com/signinoptions/twosv> | Turn it on. App passwords only exist when 2SV is on. |
  | 2SV uses only a passkey or security key | Same page | Add SMS or Google Authenticator as a second method. |
  | Advanced Protection Program | <https://myaccount.google.com/advanced-protection/> | App passwords are permanently blocked. Use OAuth instead. |
  | Google Workspace admin policy | Ask your admin | Use a personal `@gmail.com`, or OAuth. |
  | Family Link supervised account | — | Use a different account. |

  If none of these apply, app passwords are unavailable for that account and the
  fallback is the Gmail API with the `gmail.readonly` scope, which is unaffected
  by app-password policy. That is a different adapter and is not implemented
  yet — raise it before relying on it.

### 2. Generate the password

Name it `Cluck In` and press Create. Copy the 16 characters — Google shows them
as four groups of four and **only shows them once**.

### 3. Fill in `.env`

```ini
EXTERNAL_ADAPTERS=gmail
GMAIL_ENABLED=true
GMAIL_ADDRESS=you@gmail.com
GMAIL_APP_PASSWORD=abcd efgh ijkl mnop
```

Spaces in the password are stripped automatically, so paste it as displayed.

Personal Gmail and Google Workspace differ only in `GMAIL_ADDRESS`; the host,
port and mechanism are identical.

### What this adapter does and does not do

- Polls `INBOX` every `GMAIL_POLL_SECONDS` (default 30) for `UNSEEN` mail from
  the last `GMAIL_SINCE_DAYS` days.
- **Never marks anything as read.** It opens the mailbox with `EXAMINE` so the
  server refuses to set flags, and fetches with `BODY.PEEK[]` rather than
  `BODY[]`. Both safeguards are deliberate; the failure mode is silently marking
  a real inbox as read, which cannot be undone.
- Because nothing is marked read, the same messages come back on every poll.
  Deduplication by message ID in the buffer is what makes this correct.
- An app password grants access to the whole mailbox. Revoke it from the same
  Google page when the project is done.

---

## Slack (Socket Mode bot)

Socket Mode means no public URL and no ngrok — the bot opens an outbound
WebSocket to Slack.

### 1. Create a workspace you control

<https://slack.com/create>. Creating your own free workspace is strongly
preferred over using a company or school one: you are the admin, so installing
an app needs nobody's approval, and no real colleague's messages are sent to a
language model.

Create a channel, for example `#cluck-in-demo`.

### 2. Create the app from the manifest

<https://api.slack.com/apps> → **Create New App** → **From an app manifest** →
pick your workspace.

The editor in that dialog opens on the **JSON** tab, so the least fiddly route
is to select everything in it and paste
[`slack-app-manifest.json`](slack-app-manifest.json). To use
[`slack-app-manifest.yaml`](slack-app-manifest.yaml) instead, switch to the
**YAML** tab first — the two are equivalent, and the YAML one carries comments
explaining each entry.

Then Next → Create.

The manifest sets all three bot scopes, the `message.channels` event
subscription and Socket Mode in one step. Skip straight to step 3.

> The dialog's default template has `"socket_mode_enabled": false`. The manifest
> sets it to `true`, which is what removes the need for a public URL or ngrok.

<details>
<summary>Doing it by hand instead</summary>

**Create New App → From scratch**, name it `Cluck In`, pick the workspace. Then:

**Socket Mode** → toggle **Enable Socket Mode** on.

**OAuth & Permissions** → **Scopes** → **Bot Token Scopes**:

| Scope | Why |
|---|---|
| `channels:history` | Read messages in public channels |
| `channels:read` | Look up channel metadata |
| `users:read` | Turn `U0123ABCD` into a readable display name |

Without `users:read` the adapter still works, but `sender` falls back to
`Slack user U0123ABCD`.

**Event Subscriptions** → toggle **Enable Events** on. With Socket Mode enabled
there is no Request URL to fill in. Under **Subscribe to bot events**, add
**`message.channels`**, then Save Changes.

</details>

### 3. Generate the app-level token

A manifest cannot create tokens, so this step is manual either way.

**Basic Information** → **App-Level Tokens** → **Generate Token and Scopes**.
Name it `cluck-in-socket`, add the **`connections:write`** scope, Generate.

Copy it — it starts with `xapp-`.

### 4. Install

Left sidebar → **Install App** → **Install to Workspace** → Allow.
Copy the **Bot User OAuth Token** — it starts with `xoxb-`.

### 5. Invite the bot into the channel

In `#cluck-in-demo`:

```
/invite @Cluck In
```

> **This step is the one people skip.** `channels:history` grants nothing for
> channels the bot has not joined, and `message.channels` events only fire for
> joined channels. There is no error message — the adapter connects
> successfully, reports healthy, and receives nothing at all.

### 6. Fill in `.env`

```ini
EXTERNAL_ADAPTERS=slack
SLACK_BOT_TOKEN=xoxb-...
SLACK_APP_TOKEN=xapp-...
SLACK_CHANNEL_ALLOWLIST=
SLACK_BACKFILL_MINUTES=10
```

> **`EXTERNAL_ADAPTERS` is the only switch that selects a source.**
> `SLACK_ENABLED` is read by nothing — setting it without putting `slack` in
> `EXTERNAL_ADAPTERS` leaves you on the fixture adapter, looking at Bob and
> Alice. The service warns about exactly that at startup.
>
> Use `gmail,slack` for both. Do not leave `fixture` in the list for a demo: the
> committed example messages would appear in `GET /messages` next to the real
> ones.

Both tokens are required. If either is missing — or still the bare `xoxb-` /
`xapp-` prefix from `.env.example`, or the two are swapped — the service
**refuses to start** and names the variable. It does not fall back to fixture
data, because a fallback here is indistinguishable from Slack working.

Confirm it on the first line of the log:

```
Message source: slack
```

`SLACK_CHANNEL_ALLOWLIST` takes channel IDs (`C08ABCDEF`), not names. Leave it
empty to accept every public channel the bot has joined.

`SLACK_BACKFILL_MINUTES` replays that much recent history when the adapter
connects. Socket Mode has no backfill of its own, so without it a restart loses
every message sent while the process was down.

### What a bot token cannot do

It **cannot read direct messages between two people**. The `im:history` scope
only covers DMs with the bot itself. Reading your own DMs requires a user token
(`xoxp-`), which authorizes the app to act as you — a much broader grant and a
different app configuration.

---

## Google Calendar (OAuth)

### Why this one cannot use an app password

App passwords are an IMAP and SMTP mechanism. Google Calendar has no IMAP, its
CalDAV endpoint stopped accepting basic auth years ago, and the read-only secret
iCal URL cannot create anything. Reading *and writing* a calendar means the
Calendar API, and the Calendar API means OAuth. There is no shortcut, and it is
worth knowing that before spending an hour looking for one.

**You can skip this entirely.** `CALENDAR_BACKEND=memory` (the default) keeps
events in the process, which is enough to develop against and enough to demo the
whole flow — the API shapes and the tests are identical either way. The only
thing you lose is events actually appearing in your real Google Calendar.

### 1. In the Google Cloud console — about ten minutes, once

At <https://console.cloud.google.com>:

1. Create a project. Any name.
2. **APIs & Services → Library** → search "Google Calendar API" → **Enable**.
3. **APIs & Services → OAuth consent screen**:
   - User Type: **External**
   - Fill in app name, support email, developer email
   - **Add yourself under "Test users"**
   - **Leave it in "Testing".** Do not submit for verification. Review takes
     weeks and buys nothing here; `PRODUCT_SPEC_MVP.md` §12 says the same about
     the Gmail side.
4. **Credentials → Create credentials → OAuth client ID → Desktop app**.
   Download the JSON and save it as `src/external/credentials.json`.

`credentials.json` and `token.json` are both already gitignored.

### 2. Authorize this machine

```powershell
cd C:\Users\user\Desktop\Cluck_in
.\.venv\Scripts\python.exe src\external\scripts\setup_google_oauth.py
```

A browser opens. Sign in as the account whose calendar this is. It will warn
**"Google hasn't verified this app"** — that is exactly what "Testing" means.
**Advanced → Continue.**

The script writes `src/external/token.json` and then makes one real call to
prove the token works, printing your next seven days of events.

### 3. Fill in `.env`

```ini
CALENDAR_BACKEND=google
CALENDAR_ID=primary
CALENDAR_WORKDAY_START=09:00
CALENDAR_WORKDAY_END=18:00
# true lets Google email the attendees when an event is created.
CALENDAR_SEND_INVITES=false
```

### The seven-day expiry

**While the consent screen stays in "Testing", Google expires refresh tokens
after seven days.** One morning every calendar call starts failing with
`invalid_grant`. Nothing is broken — run `setup_google_oauth.py` again. The
error message from this module says so, because the raw Google one does not.

For a hackathon that trade is strictly better than the verification queue. It is
worth re-running the script the morning of a demo regardless.

### What the backend does and does not do

- **All-day events are skipped.** They have no time and no offset, the
  `ExternalEvent` contract requires a full date-time, and `docs/data-contracts.md`
  defers all-day handling. Coercing one to midnight would block the whole day.
- Two scopes are requested: `calendar.events` (read and write events) and
  `calendar.readonly`, which nothing uses any more — it was needed by
  `freebusy.query`, which went with the availability feature. It is kept only
  so an already-issued `token.json` stays valid; narrowing the list invalidates
  it and forces everyone to re-authorize. Drop it at the next scope change.
  Deliberately not the full `calendar` scope, which would also allow deleting
  calendars. None of them can reach your Gmail, Drive or contacts.
- **Changing the scope list invalidates an existing `token.json`.** Delete it
  and re-run the setup script; the error message from this module says so.
- Revoke it any time at <https://myaccount.google.com/permissions>.

---

## Sending

Outbound needs nothing new for Gmail: the **same app password** works for
`smtp.gmail.com` as for `imap.gmail.com`. Slack needs **two extra scopes** and a
reinstall, because scopes are granted at install time.

### 1. Slack: add the scopes and reinstall

At <https://api.slack.com/apps> → your app → **OAuth & Permissions** → Bot Token
Scopes, add:

| Scope | Needed by |
|---|---|
| `chat:write` | `POST /reply`, `POST /send/slack` |
| `reactions:write` | `POST /react` |

Then **Reinstall to Workspace** at the top of that page. Without the reinstall
the scopes are listed but not granted, and calls fail with `missing_scope` —
which does not look like a permissions error at first glance.

An app created from the current `docs/slack-app-manifest.yaml` already has them.

### 2. Decide where it is allowed to speak, then turn it on

```ini
# Start here. Everything is routed and recorded; nothing leaves the machine.
EXTERNAL_SEND_DRY_RUN=true

# Before you ever set that to false, fill this in.
EXTERNAL_SEND_ALLOWLIST=you@gmail.com,C08ABCDEF

GMAIL_FROM_NAME=Cluck In
SLACK_DEFAULT_CHANNEL=C08ABCDEF
```

The allowlist is the guard that matters. An AI is choosing the words and a loop
is choosing the moment; an empty allowlist means both of them can reach anyone
who has ever emailed you. Put your own address and the demo channel in it, and
nothing else, until you have watched it behave.

`GET /outbox` shows what was sent, or would have been. Read it after every
rehearsal.

### 3. Rehearse, then go live

```powershell
# still EXTERNAL_SEND_DRY_RUN=true
Invoke-RestMethod http://127.0.0.1:8100/outbox | ConvertTo-Json -Depth 5
```

When the previews look right, set `EXTERNAL_SEND_DRY_RUN=false` and restart. The
first real send should be to yourself.

### What the Gmail sender does

- Sends over SMTP with SSL on port 465.
- A reply carries `In-Reply-To` and `References`, so Gmail threads it instead of
  showing an unrelated mail whose subject starts with "Re:".
- Files a copy in your **Sent** folder over IMAP afterwards. SMTP does not do
  this — the Gmail UI and the Gmail API write that folder, not the SMTP server —
  so without the copy a reply is genuinely delivered and invisible in your own
  account, which during a demo is indistinguishable from a failure.
- Finds that folder by its `\Sent` special-use flag rather than by name, because
  the name is localized per account language.
- Filing the copy is best effort and happens **after** the send, so a failure
  there is never reported as a failure to send.

---

## Verifying

```powershell
cd C:\Users\user\Desktop\Cluck_in
.\.venv\Scripts\python.exe -m uvicorn app:app --app-dir src\external --port 8100 --workers 1
```

In another terminal:

```powershell
Invoke-RestMethod http://127.0.0.1:8100/health | ConvertTo-Json -Depth 5
```

Every configured adapter should show `connected: true` and `lastError: null`.

Then send yourself an email and post in `#cluck-in-demo`, and within
`GMAIL_POLL_SECONDS` both should appear:

```powershell
Invoke-RestMethod "http://127.0.0.1:8100/messages?limit=10" | ConvertTo-Json -Depth 6
```

Check that the Gmail `title` is readable text rather than `=?UTF-8?B?...?=`,
that both timestamps carry an offset like `+08:00`, and that the Slack `sender`
is a display name rather than `U0123ABCD`.

### Sending and the calendar

`/health` now reports both:

```powershell
(Invoke-RestMethod http://127.0.0.1:8100/health).sending
(Invoke-RestMethod http://127.0.0.1:8100/health).calendar
```

`sending.ready` should list the transports that have credentials, and
`calendar.backend` should say `google` once the token exists — if it still says
`memory` after you set `CALENDAR_BACKEND=google`, the token was not found and
the service logged a warning at startup saying so.

Reply to a message you actually received, taking its id from `/messages`:

```powershell
$json = '{"messageId":"<paste an id here>","body":"Testing the reply path."}'
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8100/reply `
  -ContentType 'application/json; charset=utf-8' `
  -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
```

> As with `/debug/inject`: passing a plain string to `-Body` in Windows
> PowerShell 5.1 encodes it as Latin-1, mangling every Chinese character before
> it leaves your machine. Always convert to `[byte[]]`.

With the dry run on you get `delivered: false, dryRun: true` and an entry in
`/outbox`. That is the whole path working — the only step left is the provider
call.

And the calendar:

```powershell
Invoke-RestMethod "http://127.0.0.1:8100/calendar/events?withinDays=7" | ConvertTo-Json -Depth 5
```

The events you see should be the ones on your real calendar, with offsets like
`+08:00`. If the list is empty and your calendar is not, check `calendar.backend`
in `/health` — `google` with no token silently falls back to `memory`.
