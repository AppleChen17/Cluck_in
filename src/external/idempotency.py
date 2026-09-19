"""Have we already done this, for that message?

The same hazard the outbox guards for replies, one step over: the Gmail poll
re-sees every unread mail forever and a restarted caller replays its cursor, so
"add this meeting to the calendar" fires again for a message that was handled
half an hour ago. A duplicate reply is embarrassing. A duplicate calendar entry
is two identical meetings on a real person's real calendar, and unlike a reply
it is still there tomorrow.

Bounded and thread-safe for the same reasons as ReplyRegistry. Kept separate
from the outbox because that structure ties its dedup to whether a send
succeeded, and creating an event has no equivalent of a dry run to except.
"""

import threading
from collections import OrderedDict


class OnceByKey:
    """A bounded, thread-safe key -> result memo, most recently used last."""

    def __init__(self, maxlen: int = 500) -> None:
        self._lock = threading.Lock()
        self._maxlen = max(1, maxlen)
        self._items: OrderedDict[str, object] = OrderedDict()

    def get(self, key: str):
        if not key:
            return None
        with self._lock:
            found = self._items.get(key)
            if found is not None:
                self._items.move_to_end(key)
            return found

    def remember(self, key: str, value) -> None:
        if not key:
            # No key means nothing to deduplicate on. Storing under "" would
            # make every unkeyed item collide with every other.
            return
        with self._lock:
            self._items[key] = value
            self._items.move_to_end(key)
            while len(self._items) > self._maxlen:
                self._items.popitem(last=False)

    def __len__(self) -> int:
        with self._lock:
            return len(self._items)
