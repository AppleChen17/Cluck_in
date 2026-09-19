"""Base class for message sources.

Deliberately push-based, mirroring the LLMProvider ABC in src/ai-engine so this
module reads as part of the same codebase.

Why push and not `fetch(since) -> list`: Slack Socket Mode is fundamentally a
push transport. A pull interface would force the Slack adapter to keep its own
internal buffer, leaving us with two buffers and two dedup sets. With a sink,
the Gmail poll thread and the Slack socket threads both just call buffer.add().
"""

import threading
from abc import ABC, abstractmethod
from collections.abc import Callable

from schemas import AdapterStatus, ExternalMessage

MessageSink = Callable[[ExternalMessage], bool]


class SourceAdapter(ABC):
    name: str = "source"

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._connected = False
        self._last_event_at: str | None = None
        self._last_error: str | None = None
        self._emitted = 0

    @abstractmethod
    def start(self, sink: MessageSink) -> None:
        """Begin delivering messages to `sink`. Must not block."""

    @abstractmethod
    def stop(self) -> None:
        """Stop delivering and release resources. Must be idempotent."""

    def health(self) -> AdapterStatus:
        with self._lock:
            return AdapterStatus(
                name=self.name,
                enabled=True,
                connected=self._connected,
                lastEventAt=self._last_event_at,
                lastError=self._last_error,
                emitted=self._emitted,
            )

    # -- helpers for subclasses, all thread-safe -------------------------------

    def _mark_connected(self, connected: bool) -> None:
        with self._lock:
            self._connected = connected

    def _mark_error(self, message: str | None) -> None:
        with self._lock:
            self._last_error = message

    def _mark_emitted(self, when: str) -> None:
        with self._lock:
            self._emitted += 1
            self._last_event_at = when
