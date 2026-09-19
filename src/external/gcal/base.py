"""The calendar backend interface, mirroring adapters/source_adapter.py.

Two implementations: `google` talks to the real Google Calendar API, `memory`
keeps events in the process. The memory one exists for the same reason
FixtureAdapter does -- a fresh clone with no credentials still runs, still
serves contract-valid ExternalEvents, and can still demo the whole flow.

Everything here deals in aware datetimes. Naive datetimes are what turns a
scheduling bug into a meeting nobody attends, and the ExternalEvent contract
rejects a timestamp without an explicit offset anyway.
"""

from abc import ABC, abstractmethod
from datetime import datetime

from schemas import ExternalEvent


class CalendarError(Exception):
    """The backend could not answer. Surfaced as HTTP 503."""


class CalendarBackend(ABC):
    name: str = "calendar"

    @abstractmethod
    def events(self, start: datetime, end: datetime) -> list[ExternalEvent]:
        """Events overlapping [start, end), oldest first."""

    @abstractmethod
    def create_event(
        self,
        *,
        title: str,
        start: datetime,
        end: datetime,
        description: str | None = None,
        location: str | None = None,
        attendees: list[str] | None = None,
        metadata: dict | None = None,
    ) -> ExternalEvent:
        """Create an event and return it as the contract sees it."""

    def available(self) -> bool:
        """False when the backend cannot be reached, so /health can say so."""
        return True
