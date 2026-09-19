"""Slack via Socket Mode.

Uses slack_sdk's builtin SocketModeClient, which needs no websocket-client,
aiohttp or websockets extra, and is synchronous -- matching the threading model
the rest of this module uses.

Scopes required on the bot token: channels:history, channels:read, users:read.
The app-level token needs connections:write (that is what makes it xapp-).

Two things that silently produce zero messages:
  * The bot must be /invite'd into every channel. channels:history grants
    nothing for channels the bot has not joined, and message.channels events
    only fire for joined channels. There is no error for this.
  * A bot token cannot read human-to-human DMs. im:history only covers DMs with
    the bot itself. Reading your own DMs needs a user token (xoxp-).

Socket Mode has no backfill: events sent while this process is down are gone.
SLACK_BACKFILL_MINUTES replays recent history on connect to soften that.
"""

import logging
import threading
import time
from datetime import datetime, timezone

import normalize
from adapters.source_adapter import MessageSink, SourceAdapter
from config import Settings
from reply_registry import ReplyTarget
from schemas import ExternalMessage

log = logging.getLogger("external.slack")

# Subtypes that are not a new human message.
_IGNORED_SUBTYPES = frozenset(
    {
        "bot_message",
        "message_changed",
        "message_deleted",
        "channel_join",
        "channel_leave",
        "channel_topic",
        "channel_purpose",
        "channel_name",
        "thread_broadcast",
        "message_replied",
        "file_share_deleted",
    }
)


def should_ignore(event: dict) -> bool:
    if event.get("type") != "message":
        return True
    if event.get("bot_id"):
        return True
    if event.get("subtype") in _IGNORED_SUBTYPES:
        return True
    if not event.get("channel") or not event.get("ts"):
        return True
    return False


def slack_ts_format(epoch: float) -> str:
    """Render an epoch as a Slack ts: seconds, a dot, then exactly 6 digits.

    str(float) produces a variable number of fractional digits. With 7 or more,
    Slack reparses the value with the extra digit shifted into the seconds:
    "1789793604.6410232" is echoed back as "17897936046.410232", a year-2537
    timestamp that matches nothing. conversations_history then returns an empty
    window with ok=true, so the backfill silently finds nothing. Verified
    against the real API.
    """
    return "{:.6f}".format(epoch)


def slack_ts_to_rfc3339(ts: str, tz_mode: str = "local") -> str:
    moment = datetime.fromtimestamp(float(ts), tz=timezone.utc)
    return normalize.to_rfc3339(moment, tz_mode)


def build_message(
    event: dict,
    sender: str,
    tz_mode: str = "local",
    max_chars: int = 2000,
    resolve_user=None,
) -> ExternalMessage:
    """Pure: one Slack message event in, contract-valid ExternalMessage out."""
    channel = event["channel"]
    text = normalize.slack_text_to_plain(event.get("text") or "", resolve_user=resolve_user)
    metadata = {"slackChannel": channel}
    thread_ts = event.get("thread_ts")
    if thread_ts and thread_ts != event["ts"]:
        # No contract field exists for threading; metadata is the only place for
        # it, and consumers must treat it as non-contractual.
        metadata["slackThreadTs"] = str(thread_ts)
    return ExternalMessage(
        # (channel, ts) is Slack's own uniqueness pair; ts alone is not unique
        # across channels.
        id="slack:{}:{}".format(channel, event["ts"]),
        source="slack",
        sender=normalize.clean_label(sender, "Slack user"),
        title=None,
        content=normalize.truncate(text, max_chars),
        timestamp=slack_ts_to_rfc3339(event["ts"], tz_mode),
        unread=True,
        metadata=metadata,
    )


def build_reply_target(event: dict, message_id: str) -> ReplyTarget:
    """Pure: where a reply to this Slack message goes.

    thread_ts falls back to ts, which is what turns a reply to a loose channel
    message into the first message of a new thread rather than another loose
    message beside it.
    """
    ts = str(event["ts"])
    return ReplyTarget(
        message_id=message_id,
        source="slack",
        channel=str(event["channel"]),
        ts=ts,
        thread_ts=str(event.get("thread_ts") or ts),
    )


class SlackAdapter(SourceAdapter):
    name = "slack"

    def __init__(self, cfg: Settings) -> None:
        super().__init__()
        self._cfg = cfg
        self._client = None
        self._web = None
        self._sink: MessageSink | None = None
        self._user_lock = threading.Lock()
        self._user_cache: dict[str, str] = {}

    def start(self, sink: MessageSink) -> None:
        if not self._cfg.slack_bot_token or not self._cfg.slack_app_token:
            raise ValueError("SLACK_BOT_TOKEN and SLACK_APP_TOKEN are required")

        from slack_sdk.socket_mode import SocketModeClient
        from slack_sdk.web import WebClient

        self._sink = sink
        self._web = WebClient(token=self._cfg.slack_bot_token)
        self._client = SocketModeClient(
            app_token=self._cfg.slack_app_token,
            web_client=self._web,
        )
        self._client.socket_mode_request_listeners.append(self._on_request)
        self._client.connect()
        self._mark_connected(True)
        log.info("slack socket mode connected")

        if self._cfg.slack_backfill_minutes > 0:
            threading.Thread(target=self._backfill, daemon=True, name="slack-backfill").start()

    def stop(self) -> None:
        if self._client is not None:
            try:
                self._client.close()
            except Exception:  # noqa: BLE001
                log.exception("failed to close slack socket")
        self._mark_connected(False)

    # -- event handling -------------------------------------------------------

    def _on_request(self, client, req) -> None:
        from slack_sdk.socket_mode.response import SocketModeResponse

        # Ack FIRST, before any parsing or users.info round trip. Slack redelivers
        # un-acked envelopes up to three times within its 3 second budget.
        try:
            client.send_socket_mode_response(SocketModeResponse(envelope_id=req.envelope_id))
        except Exception:  # noqa: BLE001
            log.exception("failed to ack slack envelope")
        try:
            self._handle(req)
        except Exception as exc:  # noqa: BLE001 - never let this escape the listener thread
            self._mark_error("{}: {}".format(type(exc).__name__, exc))
            log.exception("slack handler failed")

    def _handle(self, req) -> None:
        if req.type != "events_api":
            return
        event = (req.payload or {}).get("event") or {}
        if should_ignore(event):
            return
        allowed = self._cfg.slack_channels
        if allowed and event.get("channel") not in allowed:
            return
        self._emit(event)

    def _emit(self, event: dict) -> None:
        message = build_message(
            event,
            sender=self._resolve_user(event.get("user")),
            tz_mode=self._cfg.external_tz,
            max_chars=self._cfg.external_max_content_chars,
            resolve_user=self._resolve_user,
        )
        self._remember_target(build_reply_target(event, message.id))
        if self._sink is not None and self._sink(message):
            self._mark_emitted(message.timestamp)

    def _resolve_user(self, user_id: str | None) -> str:
        """User ID to display name, cached. Requires the users:read scope."""
        if not user_id:
            return "Slack user"
        with self._user_lock:
            cached = self._user_cache.get(user_id)
        if cached is not None:
            return cached

        label = "Slack user {}".format(user_id)
        try:
            profile = self._web.users_info(user=user_id)["user"]
            label = (
                (profile.get("profile") or {}).get("display_name")
                or (profile.get("profile") or {}).get("real_name")
                or profile.get("name")
                or label
            )
        except Exception as exc:  # noqa: BLE001
            # Cache the failure too: a deleted user would otherwise cost one API
            # call per message forever.
            log.warning("users_info failed for %s: %s", user_id, exc)

        with self._user_lock:
            self._user_cache[user_id] = label
        return label

    def _backfill(self) -> None:
        """Replay recent history so a restart does not lose the demo message."""
        oldest = slack_ts_format(time.time() - self._cfg.slack_backfill_minutes * 60)
        try:
            channels = self._web.users_conversations(types="public_channel")["channels"]
        except Exception as exc:  # noqa: BLE001
            log.warning("slack backfill could not list channels: %s", exc)
            return
        allowed = self._cfg.slack_channels
        for channel in channels:
            cid = channel.get("id")
            if not cid or (allowed and cid not in allowed):
                continue
            try:
                history = self._web.conversations_history(channel=cid, oldest=oldest)
            except Exception as exc:  # noqa: BLE001
                log.warning("slack backfill failed for %s: %s", cid, exc)
                continue
            # Oldest first, so buffer order matches arrival order.
            for event in reversed(history.get("messages") or []):
                event = dict(event)
                event.setdefault("type", "message")
                event["channel"] = cid
                if should_ignore(event):
                    continue
                # Dedup in the buffer makes a double delivery harmless.
                self._emit(event)
