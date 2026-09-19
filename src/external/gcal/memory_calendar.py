"""An in-process calendar. Needs no credentials, no GCP project, no network.

This is to gcal what FixtureAdapter is to adapters: the thing that makes a fresh
clone runnable. The ExternalEvent output, the API shape
and every test are identical to the Google backend, so swapping CALENDAR_BACKEND
to google later changes where the events live and nothing else.

It emits source "google-calendar" because that is the only value the contract
enum allows. The id prefix says memory, so nobody mistakes a rehearsal event for
one that exists in a real account.
"""

import threading
import uuid
from datetime import datetime

import normalize
from config import FIXTURES_DIR
from gcal.base import CalendarBackend
from schemas import ExternalEvent

_SEED_FIXTURE = "external-event.calendar.json"


class MemoryCalendar(CalendarBackend):
    name = "memory"

    def __init__(self, tz_mode: str = "local", seed: bool = False) -> None:
        self._lock = threading.Lock()
        self._tz_mode = tz_mode
        self._events: list[ExternalEvent] = []
        if seed:
            self._seed()

    def _seed(self) -> None:
        """Load the committed example event, so a fresh run is not empty.

        Off by default: the fixture is dated 2026-09-19 and would sit in the
        past on any other day, which makes a demo look broken rather than seeded.
        """
        import json

        path = FIXTURES_DIR / _SEED_FIXTURE
        try:
            self._events.append(
                ExternalEvent.model_validate(json.loads(path.read_text(encoding="utf-8")))
            )
        except Exception:  # noqa: BLE001 - a bad fixture must not stop startup
            pass

    # -- reads ----------------------------------------------------------------

    def _in_window(self, start: datetime, end: datetime) -> list[tuple[datetime, datetime, ExternalEvent]]:
        out = []
        with self._lock:
            events = list(self._events)
        for event in events:
            event_start = normalize.parse_rfc3339(event.startTime)
            event_end = normalize.parse_rfc3339(event.endTime)
            if event_start is None or event_end is None:
                continue
            # Overlap, not containment: a meeting that began before the window
            # still occupies the first part of it.
            if event_end > start and event_start < end:
                out.append((event_start, event_end, event))
        out.sort(key=lambda row: row[0])
        return out

    def events(self, start: datetime, end: datetime) -> list[ExternalEvent]:
        return [event for _, _, event in self._in_window(start, end)]

    # -- writes ---------------------------------------------------------------

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
        event = ExternalEvent(
            id="google-calendar:memory-" + uuid.uuid4().hex[:12],
            source="google-calendar",
            title=title,
            description=description,
            startTime=normalize.to_rfc3339(start, self._tz_mode),
            endTime=normalize.to_rfc3339(end, self._tz_mode),
            location=location,
            attendees=attendees if attendees is not None else [],
            metadata=metadata or None,
        )
        with self._lock:
            self._events.append(event)
        return event
