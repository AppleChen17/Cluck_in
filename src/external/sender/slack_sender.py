"""Slack outbound: chat.postMessage and reactions.add.

Reuses the bot token the inbound adapter already needs, but two extra scopes
have to be added to the app before either call works, and Slack's failure for a
missing scope is a `missing_scope` error rather than anything that looks like a
permissions problem:

    chat:write      post messages
    reactions:write add an emoji reaction

docs/slack-app-manifest.yaml has them; an app created from the earlier manifest
must be updated and reinstalled, because scopes are granted at install time.

The outbound half is deliberately independent of SlackAdapter. Sending works
with EXTERNAL_ADAPTERS=fixture and no socket connection at all, which is what
makes the send path testable and demoable on its own.
"""

import logging

from config import Settings
from sender.base import Sender

log = logging.getLogger("external.sender.slack")

# reactions.add wants a short name ("thumbsup"), never the character itself.
# Passing an emoji through unchanged fails with invalid_name, so the handful an
# auto-reply actually reaches for are translated. Anything unrecognized is
# passed through with its colons stripped, which is correct for every other
# short name and is what a caller who knows Slack will send anyway.
_EMOJI_NAMES = {
    "\U0001F44D": "thumbsup",
    "\U0001F44C": "ok_hand",
    "✅": "white_check_mark",
    "\U0001F440": "eyes",
    "\U0001F389": "tada",
    "\U0001F425": "hatching_chick",
    "\U0001F4C5": "calendar",
    "⏰": "alarm_clock",
    "❤️": "heart",
    "❤": "heart",
    "\U0001F64F": "pray",
}


def emoji_to_name(emoji: str) -> str:
    """Normalize whatever the caller sent into a Slack reaction short name."""
    value = (emoji or "").strip()
    if not value:
        return "thumbsup"
    mapped = _EMOJI_NAMES.get(value)
    if mapped:
        return mapped
    # Strip the variation selector some keyboards append before giving up.
    mapped = _EMOJI_NAMES.get(value.rstrip("️"))
    if mapped:
        return mapped
    return value.strip(":")


class SlackSender(Sender):
    source = "slack"

    def __init__(self, cfg: Settings) -> None:
        self._cfg = cfg
        self._web = None

    def available(self) -> bool:
        return bool(self._cfg.slack_bot_token)

    def _client(self):
        if self._web is None:
            # Imported lazily and constructed on first use, exactly as the
            # inbound adapter does, so the module imports without slack_sdk
            # installed and a dry run never needs a token.
            from slack_sdk.web import WebClient

            self._web = WebClient(token=self._cfg.slack_bot_token)
        return self._web

    def deliver(
        self,
        *,
        destination: str,
        body: str,
        thread_ts: str | None = None,
        **_ignored,
    ) -> str:
        response = self._client().chat_postMessage(
            channel=destination,
            text=body,
            thread_ts=thread_ts,
        )
        ts = str(response.get("ts") or "")
        log.info("posted to %s (ts=%s, thread=%s)", destination, ts, thread_ts or "-")
        return ts

    def react(self, *, channel: str, ts: str, emoji: str) -> str:
        """Add a reaction. Not routed through Dispatcher.deliver: a reaction has
        no body, so a preview of it would be meaningless, and it is the one
        outbound action with a natural no-op on repeat (Slack answers
        already_reacted, which is success as far as anyone cares)."""
        name = emoji_to_name(emoji)
        try:
            self._client().reactions_add(channel=channel, timestamp=ts, name=name)
        except Exception as exc:  # noqa: BLE001
            if "already_reacted" in str(exc):
                log.info("reaction %s already on %s/%s", name, channel, ts)
                return name
            raise
        log.info("reacted %s on %s/%s", name, channel, ts)
        return name
