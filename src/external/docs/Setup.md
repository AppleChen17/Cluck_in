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
EXTERNAL_ADAPTERS=gmail,slack
SLACK_ENABLED=true
SLACK_BOT_TOKEN=xoxb-...
SLACK_APP_TOKEN=xapp-...
SLACK_CHANNEL_ALLOWLIST=
SLACK_BACKFILL_MINUTES=10
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
