"""Thread-safe message buffer sitting between the adapters and the HTTP layer.

Three independent thread sources touch this structure:
  * slack_sdk's SocketModeClient dispatches listeners on its own ThreadPoolExecutor
  * the Gmail adapter polls on its own threading.Thread
  * uvicorn serves sync endpoints on Starlette's anyio worker threadpool

Every public method therefore takes the lock for its whole body.

Delivery tracking uses a server-assigned monotonic sequence rather than a
timestamp watermark. `timestamp` is the message's SEND time (the contract says
so), which is not monotonic in arrival order: a delayed email arrives with a
send time older than something already delivered, and a `since=max(timestamp)`
watermark would swallow it forever. Second-granular Gmail dates also tie.
"""

import itertools
import re
import threading
import uuid
from collections import deque

from schemas import ExternalMessage

_CURSOR_RE = re.compile(r"^v1:([0-9a-f]{1,32}):(\d+)$")


def encode_cursor(epoch: str, seq: int) -> str:
    return f"v1:{epoch}:{seq}"


def parse_cursor(cursor: str | None) -> tuple[str | None, int]:
    """Return (epoch, seq). A None epoch means 'unusable — start from the top'."""
    if not cursor:
        return None, 0
    m = _CURSOR_RE.match(cursor.strip())
    if not m:
        return None, 0
    return m.group(1), int(m.group(2))


class MessageBuffer:
    def __init__(self, maxlen: int = 500):
        self._lock = threading.Lock()
        self._items: deque[tuple[int, ExternalMessage]] = deque(maxlen=maxlen)
        # Bounded dedup history. Gmail re-SEARCHes UNSEEN every poll and we never
        # mark mail read, so the same messages come back on every single tick;
        # without this the same 5 emails would be re-emitted 120 times an hour.
        self._seen_order: deque[str] = deque(maxlen=maxlen * 4)
        self._seen: set[str] = set()
        self._counter = itertools.count(1)
        self.epoch = uuid.uuid4().hex[:8]

    def add(self, msg: ExternalMessage) -> bool:
        """Append a message. Returns False if its id was already seen."""
        with self._lock:
            if msg.id in self._seen:
                return False
            if len(self._seen_order) == self._seen_order.maxlen:
                self._seen.discard(self._seen_order[0])
            self._seen_order.append(msg.id)
            self._seen.add(msg.id)
            self._items.append((next(self._counter), msg))
            return True

    def read_after(
        self,
        seq: int,
        limit: int,
        since: str | None = None,
    ) -> tuple[list[ExternalMessage], int, bool]:
        """Return (messages, last_seq, has_more) for everything with seq > `seq`.

        Non-destructive and idempotent: the same cursor always returns the same
        batch, so a dropped HTTP response costs nothing and the client just
        retries. `since` is an ADDITIONAL filter on the send timestamp, never a
        delivery cursor.

        """
        with self._lock:
            pending = [(s, m) for s, m in self._items if s > seq]
            if since:
                pending = [(s, m) for s, m in pending if m.timestamp >= since]
            window = pending[:limit]
            has_more = len(pending) > len(window)
            last = window[-1][0] if window else seq
            return [m for _, m in window], last, has_more

    def latest_seq(self) -> int:
        with self._lock:
            return self._items[-1][0] if self._items else 0

    def stats(self) -> dict:
        with self._lock:
            return {"buffered": len(self._items), "seen": len(self._seen), "epoch": self.epoch}
