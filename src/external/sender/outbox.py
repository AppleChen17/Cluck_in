"""A record of everything sent, or that would have been sent.

Three jobs, and the third is the one that matters:

  1. It is what GET /outbox serves, so the dashboard can show "the chicken
     replied to this" without the sender having to push anywhere.
  2. It makes EXTERNAL_SEND_DRY_RUN useful. A dry run is fully routed and
     recorded; only the final provider call is skipped. The whole integration
     can be built, tested and rehearsed against it.
  3. It makes replying idempotent per message id. This is not a nicety. The
     Gmail adapter never marks mail read, so it re-fetches the same UNSEEN
     message on every poll; the buffer dedups, but a restarted orchestrator
     replays its cursor and would ask to reply again. Without this, one incoming
     email becomes a reply every 30 seconds, to a real person, forever.

Idempotency is keyed on the message being replied TO, not on request content,
because "reply to this message" is the operation that must happen at most once.
"""

import threading
import uuid
from collections import OrderedDict, deque

import normalize
from schemas import SendResult

_PREVIEW_CHARS = 140


class Outbox:
    def __init__(self, maxlen: int = 500) -> None:
        self._lock = threading.Lock()
        self._records: deque[SendResult] = deque(maxlen=maxlen)
        # Separate from _records and larger: a reply must stay deduped for
        # longer than it stays on the dashboard.
        self._replied: OrderedDict[str, SendResult] = OrderedDict()
        self._replied_max = max(maxlen * 4, 100)

    @staticmethod
    def new_id() -> str:
        return "out-" + uuid.uuid4().hex[:12]

    @staticmethod
    def preview(body: str) -> str:
        return normalize.truncate(normalize.collapse_whitespace(body), _PREVIEW_CHARS)

    def already_replied(self, message_id: str) -> SendResult | None:
        """The earlier result for this message, or None.

        Returns a copy with duplicate=True so the caller can hand it straight
        back: the client learns nothing new was sent, and still gets the id of
        what was.
        """
        with self._lock:
            previous = self._replied.get(message_id)
        if previous is None:
            return None
        return previous.model_copy(update={"duplicate": True})

    def record(self, result: SendResult) -> SendResult:
        with self._lock:
            self._records.append(result)
            if result.inReplyTo and not result.error:
                # A failed send is NOT remembered as a reply: the message still
                # has no answer, and a retry must be allowed. A dry run IS
                # remembered -- during a rehearsal you want to see each message
                # answered once, exactly as it would be for real.
                self._replied[result.inReplyTo] = result
                self._replied.move_to_end(result.inReplyTo)
                while len(self._replied) > self._replied_max:
                    self._replied.popitem(last=False)
        return result

    def recent(self, limit: int = 50) -> list[SendResult]:
        with self._lock:
            items = list(self._records)
        return list(reversed(items))[:limit]

    def stats(self) -> dict:
        with self._lock:
            return {"recorded": len(self._records), "replied": len(self._replied)}
