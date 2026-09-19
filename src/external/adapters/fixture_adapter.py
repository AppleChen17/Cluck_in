"""Replays the committed shared/fixtures messages. Needs no credentials.

This is what makes EXTERNAL_ADAPTERS=fixture (the default) useful: anyone who
clones the repo can run the service and get contract-valid ExternalMessages, so
the C# side can be written and tested before a single token exists.
"""

import json

from config import FIXTURES_DIR
from reply_registry import target_from_message
from schemas import ExternalMessage

from adapters.source_adapter import MessageSink, SourceAdapter

_FIXTURE_FILES = ("external-message.gmail.json", "external-message.slack.json")


class FixtureAdapter(SourceAdapter):
    name = "fixture"

    def start(self, sink: MessageSink) -> None:
        for filename in _FIXTURE_FILES:
            path = FIXTURES_DIR / filename
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                message = ExternalMessage.model_validate(raw)
            except Exception as exc:  # noqa: BLE001 - one bad fixture must not kill startup
                self._mark_error(f"{filename}: {type(exc).__name__}: {exc}")
                continue
            # Recovered from the message, not from a provider payload: a
            # fixture never went through Gmail or Slack. Good enough to let the
            # credential-free path exercise replying too.
            self._remember_target(target_from_message(message))
            if sink(message):
                self._mark_emitted(message.timestamp)
        self._mark_connected(True)

    def stop(self) -> None:
        self._mark_connected(False)
