"""Settings for the external module.

Loaded from src/external/.env. Field names match the env var names
case-insensitively, so `gmail_address` reads GMAIL_ADDRESS.
"""

from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

# config.py lives at <repo>/src/external/config.py
REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURES_DIR = REPO_ROOT / "shared" / "fixtures"
SCHEMAS_DIR = REPO_ROOT / "shared" / "schemas"


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

    slack_enabled: bool = False
    slack_bot_token: str = ""
    slack_app_token: str = ""
    slack_channel_allowlist: str = ""
    slack_backfill_minutes: int = 0

    @property
    def adapters(self) -> list[str]:
        return [a.strip().lower() for a in self.external_adapters.split(",") if a.strip()]

    @property
    def slack_channels(self) -> set[str]:
        """Empty set means: every public channel the bot has joined."""
        return {c.strip() for c in self.slack_channel_allowlist.split(",") if c.strip()}


def load_settings() -> Settings:
    return Settings()
