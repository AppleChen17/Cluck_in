"""Settings for the external module.

Loaded from src/external/.env. Field names match the env var names
case-insensitively, so `gmail_address` reads GMAIL_ADDRESS.
"""

from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

# config.py lives at <repo>/src/external/config.py
MODULE_DIR = Path(__file__).resolve().parent
REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURES_DIR = REPO_ROOT / "shared" / "fixtures"
SCHEMAS_DIR = REPO_ROOT / "shared" / "schemas"

# Every value EXTERNAL_ADAPTERS accepts. A name outside this set is a typo, and
# a typo'd source is rejected rather than skipped: "slck" would otherwise leave
# the service running with no source at all, answering GET /messages with an
# empty list forever and no indication why.
KNOWN_ADAPTERS = ("fixture", "gmail", "slack")

# .env.example ships the Slack tokens as their bare prefixes, so a copied
# example reads as "configured" while carrying nothing. Treated as unset, which
# turns an opaque invalid_auth at the first API call into a startup error that
# names the variable.
_PLACEHOLDERS = frozenset({"", "xoxb-", "xapp-", "xoxb-...", "xapp-...", "..."})


class ConfigError(RuntimeError):
    """A source was selected explicitly but cannot work as configured.

    Raised at startup, before any adapter is built. Selecting `slack` and
    getting the committed Bob/Alice fixtures instead -- or an empty inbox with
    a warning buried in the log -- is the failure this exists to prevent.
    """


def _unset(value: str) -> bool:
    return value.strip().lower() in _PLACEHOLDERS


class Settings(BaseSettings):
    # env_file must be an absolute path. SettingsConfigDict resolves a relative
    # one against the CWD, and this service is launched from the repo root
    # (uvicorn --app-dir src/external), so ".env" would silently never be found
    # and every setting would quietly fall back to its default.
    model_config = SettingsConfigDict(
        env_file=str(Path(__file__).with_name(".env")),
        env_file_encoding="utf-8",
        extra="ignore",
    )

    external_host: str = "127.0.0.1"
    external_port: int = 8100
    external_adapters: str = "fixture"
    external_buffer_size: int = 500
    external_max_content_chars: int = 2000
    external_tz: str = "local"
    external_debug: bool = False

    gmail_enabled: bool = False
    gmail_imap_host: str = "imap.gmail.com"
    gmail_imap_port: int = 993
    gmail_address: str = ""
    gmail_app_password: str = ""
    gmail_mailbox: str = "INBOX"
    gmail_poll_seconds: int = 30
    gmail_since_days: int = 1
    gmail_max_fetch: int = 25
    # True searches UNSEEN only, which matches "messages you have not dealt with".
    # The catch: opening the mail in Gmail clears that flag and the message
    # silently stops being delivered -- easy to trip over during a demo. Set it
    # false to take everything from the last GMAIL_SINCE_DAYS days instead;
    # dedup by message id stops anything being emitted twice either way.
    gmail_only_unseen: bool = True
    # True delivers only mail that ARRIVES after this process starts, matching
    # Slack, where Socket Mode delivers nothing from before it connected. False
    # takes everything from the last GMAIL_SINCE_DAYS days, which fills the
    # dashboard immediately but shows mail from before the session began.
    gmail_only_since_startup: bool = True
    gmail_smtp_host: str = "smtp.gmail.com"
    gmail_smtp_port: int = 465
    # Shown as the display name on outgoing mail. Empty uses the bare address.
    gmail_from_name: str = ""
    # Empty auto-detects the Sent folder from its \Sent special-use flag, which
    # is the only reliable way: the literal name is localized ("[Gmail]/Sent
    # Mail" in English, "[Gmail]/&Zes-..." in Chinese) and guessing it wrong
    # means outgoing mail never appears in Gmail.
    gmail_sent_mailbox: str = ""
    # SMTP hands the message to Google and forgets it; Gmail's own Sent folder
    # is populated by the Gmail UI, not by SMTP. Appending over IMAP is what
    # makes a sent reply visible in the thread during a demo.
    gmail_append_to_sent: bool = True

    # -- sending --------------------------------------------------------------
    # Sending mail and posting to Slack is outward-facing and cannot be undone,
    # so it is OFF by default. A dry run still validates, routes, and records
    # everything in the outbox -- the whole integration can be built and demoed
    # against it -- it simply does not hand the payload to Gmail or Slack.
    external_send_dry_run: bool = True
    # Comma list of email addresses and Slack channel IDs a real send may target.
    # Empty means no restriction, which is the right setting for nobody. Set it
    # to your own address and the demo channel before flipping the dry run off.
    external_send_allowlist: str = ""
    external_outbox_size: int = 500
    external_reply_registry_size: int = 2000

    slack_enabled: bool = False
    slack_bot_token: str = ""
    slack_app_token: str = ""
    slack_channel_allowlist: str = ""
    slack_backfill_minutes: int = 0
    # Where POST /send/slack posts when the caller names no channel.
    slack_default_channel: str = ""

    # -- calendar -------------------------------------------------------------
    # memory | google. "memory" needs no credentials and keeps events for the
    # lifetime of the process, which is enough to demo the whole flow.
    calendar_backend: str = "memory"
    calendar_id: str = "primary"
    # Empty falls back to src/external/credentials.json and token.json.
    google_credentials_file: str = ""
    google_token_file: str = ""
    # True lets Google email the attendees when an event is created. Off by
    # default for the same reason sending is: it leaves the machine.
    calendar_send_invites: bool = False

    @property
    def adapters(self) -> list[str]:
        return [a.strip().lower() for a in self.external_adapters.split(",") if a.strip()]

    @property
    def send_allowlist(self) -> set[str]:
        """Empty set means: no restriction on where a real send may go."""
        return {t.strip().lower() for t in self.external_send_allowlist.split(",") if t.strip()}

    @property
    def credentials_path(self) -> Path:
        return Path(self.google_credentials_file or (MODULE_DIR / "credentials.json"))

    @property
    def token_path(self) -> Path:
        return Path(self.google_token_file or (MODULE_DIR / "token.json"))

    @property
    def slack_channels(self) -> set[str]:
        """Empty set means: every public channel the bot has joined."""
        return {c.strip() for c in self.slack_channel_allowlist.split(",") if c.strip()}

    # -- source selection ------------------------------------------------------

    def missing_slack_settings(self) -> list[str]:
        """What SLACK_* is missing or still a placeholder, in .env order.

        The token prefixes are checked too. Slack bot tokens are always `xoxb-`
        and app-level tokens always `xapp-`, so a swapped pair is detectable
        here; left alone it fails at connect time as `invalid_auth`, which names
        neither variable.
        """
        problems: list[str] = []
        if _unset(self.slack_bot_token):
            problems.append("SLACK_BOT_TOKEN is not set")
        elif not self.slack_bot_token.startswith("xoxb-"):
            problems.append("SLACK_BOT_TOKEN must be a bot token starting with 'xoxb-'")
        if _unset(self.slack_app_token):
            problems.append("SLACK_APP_TOKEN is not set")
        elif not self.slack_app_token.startswith("xapp-"):
            problems.append(
                "SLACK_APP_TOKEN must be an app-level token starting with 'xapp-' "
                "(Basic Information -> App-Level Tokens, scope connections:write)"
            )
        return problems

    def missing_gmail_settings(self) -> list[str]:
        problems: list[str] = []
        if _unset(self.gmail_address):
            problems.append("GMAIL_ADDRESS is not set")
        if _unset(self.gmail_app_password):
            problems.append("GMAIL_APP_PASSWORD is not set")
        return problems

    def validate_sources(self) -> None:
        """Fail startup when EXTERNAL_ADAPTERS names a source that cannot run.

        Deliberately louder than the calendar, which falls back from `google` to
        `memory` and reports it in GET /health. A calendar fallback costs you
        real events; a message-source fallback would serve the committed
        Bob/Alice fixtures as though they were real Slack traffic, which is
        indistinguishable from the feature working.
        """
        names = self.adapters
        if not names:
            raise ConfigError(
                "EXTERNAL_ADAPTERS is empty. Set it in src/external/.env to one of: "
                + ", ".join(KNOWN_ADAPTERS)
            )
        unknown = [n for n in names if n not in KNOWN_ADAPTERS]
        if unknown:
            raise ConfigError(
                "EXTERNAL_ADAPTERS names {} in src/external/.env, which is not a "
                "message source. Valid values are: {}.".format(
                    ", ".join(repr(n) for n in unknown), ", ".join(KNOWN_ADAPTERS)
                )
            )

        problems: list[str] = []
        if "slack" in names:
            problems += self.missing_slack_settings()
        if "gmail" in names:
            problems += self.missing_gmail_settings()
        if problems:
            raise ConfigError(
                "EXTERNAL_ADAPTERS={} but src/external/.env is incomplete:\n  - {}\n"
                "See src/external/docs/Setup.md. Set EXTERNAL_ADAPTERS=fixture to "
                "run without credentials.".format(
                    self.external_adapters.strip(), "\n  - ".join(problems)
                )
            )


def load_settings() -> Settings:
    return Settings()
