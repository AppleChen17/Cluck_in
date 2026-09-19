"""Where a reply has to go, keyed by the ExternalMessage id.

Replying to a message needs things the contract does not carry and should not
carry: the sender's actual email address, the RFC 822 Message-ID and References
chain, the Slack channel and thread_ts. `ExternalMessage.sender` is a display
name by design ("Senders and attendees are normalized human-readable strings,
not provider user objects" -- docs/data-contracts.md), and widening the contract
to carry routing data would push provider detail into every consumer.

So the adapters record it here as they emit, and the caller replies with the one
identifier it already has:

    POST /reply { "messageId": "gmail:abc@mail.gmail.com", "body": "..." }

Two consequences worth knowing, both documented in docs/API.md:

  * This is in memory, like the message buffer. After a restart the service can
    still serve messages (the buffer replays) but no longer knows how to reply
    to the ones it has not re-fetched. POST /send/gmail and /send/slack take an
    explicit address or channel and are the escape hatch.
  * Nothing here is ever exposed on GET /messages. An email address is personal
    data that the contract deliberately does not publish.
"""

import threading
from collections import OrderedDict
from dataclasses import dataclass


@dataclass(frozen=True)
class ReplyTarget:
    """How to reach the author of one message. One source, so most fields are None."""

    message_id: str
    source: str

    # gmail
    address: str | None = None
    rfc_message_id: str | None = None
    references: str | None = None
    subject: str | None = None

    # slack
    channel: str | None = None
    ts: str | None = None
    thread_ts: str | None = None

    def destination(self) -> str:
        """The human-readable thing a real send would go to, for the allowlist."""
        return (self.address or self.channel or "").strip()


class ReplyRegistry:
    """Bounded, thread-safe, insertion-ordered id -> ReplyTarget map.

    Bounded for the same reason the buffer's dedup set is: the Gmail poll never
    marks mail read, so it re-fetches the same UNSEEN messages every 30 seconds
    forever. An unbounded dict would grow without limit over a long session.
    """

    def __init__(self, maxlen: int = 2000) -> None:
        self._lock = threading.Lock()
        self._maxlen = max(1, maxlen)
        self._targets: OrderedDict[str, ReplyTarget] = OrderedDict()

    def remember(self, target: ReplyTarget) -> None:
        with self._lock:
            # move_to_end keeps the most recently seen message alive rather than
            # the one first inserted; during a long focus session the newest
            # messages are the ones anyone replies to.
            self._targets[target.message_id] = target
            self._targets.move_to_end(target.message_id)
            while len(self._targets) > self._maxlen:
                self._targets.popitem(last=False)

    def get(self, message_id: str) -> ReplyTarget | None:
        with self._lock:
            return self._targets.get(message_id)

    def __len__(self) -> int:
        with self._lock:
            return len(self._targets)


def target_from_message(message) -> ReplyTarget | None:
    """Best-effort routing recovered from an ExternalMessage alone.

    The adapters record a far better target as they emit, straight from the
    provider payload. This is the fallback for messages that never went through
    an adapter -- the committed fixtures and POST /debug/inject -- so that the
    fixture-only demo path can still exercise replying.

    Slack needs nothing extra: the contract id IS "slack:<channel>:<ts>", which
    is exactly what chat.postMessage and reactions.add want. Gmail has no such
    luck -- `sender` is a display name by contract, never an address -- so a
    hand-written message must supply one in `metadata.replyToAddress`.
    """
    if message.source == "slack":
        parts = message.id.split(":")
        if len(parts) >= 3 and parts[1] and parts[2]:
            channel = (message.metadata or {}).get("slackChannel") or parts[1]
            thread_ts = (message.metadata or {}).get("slackThreadTs") or parts[2]
            return ReplyTarget(
                message_id=message.id,
                source="slack",
                channel=str(channel),
                ts=parts[2],
                thread_ts=str(thread_ts),
            )
        return None

    address = (message.metadata or {}).get("replyToAddress")
    if not address:
        return None
    rfc_message_id = message.id[len("gmail:") :] if message.id.startswith("gmail:") else ""
    return ReplyTarget(
        message_id=message.id,
        source="gmail",
        address=str(address),
        rfc_message_id="<{}>".format(rfc_message_id) if rfc_message_id else None,
        subject=message.title,
    )
